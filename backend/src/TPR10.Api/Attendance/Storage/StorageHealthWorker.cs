using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.Scopes;

namespace TPR10.Api.Attendance.Storage;

public sealed class StorageHealthWorker(IServiceScopeFactory scopes, IOptionsMonitor<StorageOptions> options,
    TimeProvider clock, ILogger<StorageHealthWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        LoopAsync(TimeSpan.FromSeconds(30), true, stoppingToken), LoopAsync(TimeSpan.FromSeconds(60), false, stoppingToken));

    private async Task LoopAsync(TimeSpan interval, bool readiness, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (!options.CurrentValue.HealthWorkerEnabled) continue;
                try
                {
                    using var scope = scopes.CreateScope();
                    var scanner = scope.ServiceProvider.GetRequiredService<StorageHealthScanner>();
                    if (readiness) await scanner.RefreshAsync(ct); else await scanner.ScanManifestAsync(ct);
                }
                catch (Exception error) when (ScopeOperation.IsDatabaseFault(error) || error is IOException or UnauthorizedAccessException)
                { logger.LogWarning("Attendance storage health check unavailable; retry on next tick"); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}

// Independent native readiness and readonly integrity scans. No image I/O holds the identity lock.
public sealed class StorageHealthScanner(Tpr10DbContext db, StorageRuntime runtime, IAuditEventWriter audit,
    TimeProvider clock, IServiceScopeFactory scopes)
{
    public async Task ScanAsync(CancellationToken ct) { await RefreshAsync(ct); await ScanManifestAsync(ct); }

    public async Task RefreshAsync(CancellationToken ct)
    {
        if (!await runtime.RefreshGate.WaitAsync(0, ct)) return;
        try
        {
            Guid? after = null;
            while (true)
            {
                var query = db.Set<StorageLocation>().AsNoTracking();
                if (after is { } cursor) query = query.Where(x => x.Id.CompareTo(cursor) > 0);
                var rows = await query.OrderBy(x => x.Id).Take(100).ToArrayAsync(ct);
                await Parallel.ForEachAsync(rows, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (row, token) =>
                {
                    using var scope = scopes.CreateScope();
                    var checker = ActivatorUtilities.CreateInstance<StorageHealthScanner>(scope.ServiceProvider, runtime);
                    await checker.CheckReadinessAsync(row, token);
                });
                if (rows.Length < 100) break;
                after = rows[^1].Id;
            }
        }
        finally { runtime.RefreshGate.Release(); }
    }

    private async Task CheckReadinessAsync(StorageLocation snapshot, CancellationToken ct)
    {
        StorageHealthView health;
        try
        {
            health = await runtime.Resolve(snapshot).ProbeAsync(ct);
            health = health with
            {
                Status = health.Status == "healthy" ? "ready" : health.Status,
                ErrorCode = health.Status == "unknown" ? "capacity-unknown" : health.ErrorCode
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { health = new(snapshot.Id, "unavailable", null, null, 0, 0, clock.GetUtcNow(), "storage-unavailable"); }
        await using var tx = await ScopeOperation.BeginAsync(db, ct);
        var current = await db.Set<StorageLocation>().SingleAsync(x => x.Id == snapshot.Id, ct);
        if (current.Version != snapshot.Version || current.Version == long.MaxValue) return;
        bool matches;
        try { matches = runtime.Matches(current); } catch (IOException) { matches = false; }
        if (!matches) health = new(snapshot.Id, "unavailable", null, null, 0, 0, clock.GetUtcNow(), "storage-config-changed");
        current.Health = health.Status; current.FreeBytes = health.FreeBytes; current.TotalBytes = health.TotalBytes;
        current.CheckedAtUtc = health.CheckedAtUtc; current.Version++;
        await audit.WriteAsync(new SecurityAuditRequest(null, null, null, null, null, "attendance.storage.health",
            "attendance-storage", current.Id, health.Status == "unavailable" ? "failure" : "success",
            new Dictionary<string, string> { ["reason"] = health.ErrorCode ?? "scheduled-health-check" }), ct);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        runtime.Remember(current, health);
    }

    public async Task ScanManifestAsync(CancellationToken ct)
    {
        if (!await runtime.ScanGate.WaitAsync(0, ct)) return;
        try
        {
            // A bounded tick cannot monopolize the scanner; resume at the next storage, then wrap.
            var deadline = clock.GetUtcNow().AddSeconds(20);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(20));
            var query = db.Set<StorageLocation>().AsNoTracking();
            if (runtime.ScanStorageAfter is { } after) query = query.Where(x => x.Id.CompareTo(after) > 0);
            var rows = await query.OrderBy(x => x.Id).Take(100).ToArrayAsync(ct);
            if (rows.Length == 0) { runtime.ScanStorageAfter = null; return; }
            foreach (var row in rows)
            {
                await CheckManifestAsync(row, deadline, budget.Token, ct);
                runtime.ScanStorageAfter = row.Id;
                if (budget.IsCancellationRequested || clock.GetUtcNow() >= deadline) return;
            }
            if (rows.Length < 100) runtime.ScanStorageAfter = null;
        }
        finally { runtime.ScanGate.Release(); }
    }

    private async Task CheckManifestAsync(StorageLocation snapshot, DateTimeOffset deadline, CancellationToken budget, CancellationToken ct)
    {
        var progress = runtime.Scans.GetValueOrDefault(snapshot.Id) ?? new(null, 0, 0, 0, false);
        var orphans = await db.Set<EvidenceObject>().CountAsync(x => x.StorageId == snapshot.Id && x.State == EvidenceState.Orphan, ct);
        runtime.Scans[snapshot.Id] = progress with { Orphans = orphans };
        if (!runtime.Ready(snapshot)) return;
        var query = db.Set<EvidenceLocation>().AsNoTracking().Where(x => x.StorageId == snapshot.Id
            && (x.State == CopyState.Active || x.State == CopyState.Verified || x.State == CopyState.Fallback));
        if (progress.After is { } after) query = query.Where(x => x.Id.CompareTo(after) > 0);
        var copies = await query.OrderBy(x => x.Id).Take(100).ToArrayAsync(ct);
        var missing = progress.After is null ? 0 : progress.Missing;
        var cursor = progress.After;
        var processed = 0;
        foreach (var copy in copies)
        {
            ct.ThrowIfCancellationRequested();
            if (budget.IsCancellationRequested || clock.GetUtcNow() >= deadline) break;
            try { _ = await runtime.Resolve(snapshot).ReadVerifiedAsync(copy.ObjectKey, copy.Sha256, copy.Length, budget); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && budget.IsCancellationRequested) { break; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { missing++; }
            cursor = copy.Id; processed++;
        }
        if (!runtime.Matches(snapshot)) return;
        var complete = processed == copies.Length && copies.Length < 100;
        runtime.Scans[snapshot.Id] = new(complete ? null : cursor, missing,
            complete ? missing : progress.CompletedMissing, orphans, !complete);
    }
}
