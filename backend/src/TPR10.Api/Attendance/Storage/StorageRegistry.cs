using Microsoft.EntityFrameworkCore;
using TPR10.Api.Auditing;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Data;
using TPR10.Api.Identity.Authorization;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Scopes;

namespace TPR10.Api.Attendance.Storage;

public sealed partial class StorageRegistry(Tpr10DbContext db, ScopeAccess session, RequestSession current,
    PermissionMutationGuard guard, PermissionContext permission, IAuditEventWriter audit, StorageRuntime runtime, TimeProvider clock)
{
    public const string Capability = "attendance:storage-manage";
    private Guid Actor => current.Entity?.UserId ?? Guid.Empty;
    private sealed record Change(IResult Result, Guid? Target = null, string? Reason = null, bool PersistFailure = false, Action? Committed = null, string? AuditAction = null);

    public Task<IResult> OptionsAsync(int offset, int limit, CancellationToken ct) => RunAsync("options", () =>
    {
        if (!ValidPage(offset, limit)) return Task.FromResult(Reject(400));
        var rows = runtime.Definitions().OrderBy(x => x.Alias, StringComparer.Ordinal).Skip(offset).Take(limit + 1)
            .Select(x => new StorageOptionView(x.Alias, x.Kind)).ToArray();
        return Task.FromResult(new Change(Results.Ok(new Page<StorageOptionView>(rows.Take(limit).ToArray(), offset, rows.Length > limit))));
    }, ct);

    public Task<IResult> LocationsAsync(int offset, int limit, CancellationToken ct) => RunAsync("locations", async () =>
    {
        if (!ValidPage(offset, limit)) return Reject(400);
        var rows = await db.Set<StorageLocation>().AsNoTracking().OrderBy(x => x.Alias).Skip(offset).Take(limit + 1).ToArrayAsync(ct);
        return new(Results.Ok(new Page<StorageView>(rows.Take(limit).Select(View).ToArray(), offset, rows.Length > limit)));
    }, ct);

    public Task<IResult> HealthAsync(int offset, int limit, CancellationToken ct) => RunAsync("health", async () =>
    {
        if (!ValidPage(offset, limit)) return Reject(400);
        var rows = await db.Set<StorageLocation>().AsNoTracking().OrderBy(x => x.Alias).Skip(offset).Take(limit + 1).ToArrayAsync(ct);
        return new(Results.Ok(new Page<StorageHealthView>(rows.Take(limit).Select(runtime.Health).ToArray(), offset, rows.Length > limit)));
    }, ct);

    public Task<IResult> TargetAsync(CancellationToken ct) => RunAsync("target", async () =>
    {
        var target = await db.Set<StorageWriteTarget>().AsNoTracking().SingleOrDefaultAsync(ct);
        return new(Results.Ok(new StorageTargetView(target?.StorageId, target?.Version ?? 1)));
    }, ct);

    public Task<IResult> RegisterAsync(RegisterStorage request, CancellationToken ct) => RunAsync("register", async () =>
    {
        if (!StorageRuntime.ValidAlias(request.Alias) || !DirectoryRules.ValidReason(request.Reason)) return Reject(400);
        var definition = runtime.Find(request.Alias);
        if (definition is null) return Reject(404);
        if (await db.Set<StorageLocation>().AnyAsync(x => x.Alias == request.Alias, ct)) return Reject(409);
        var row = new StorageLocation
        {
            Id = Guid.NewGuid(),
            Alias = definition.Alias,
            Kind = definition.Kind,
            ConfigFingerprint = StorageRuntime.Fingerprint(definition),
            AcceptWrites = true,
            CreatedAtUtc = clock.GetUtcNow()
        };
        db.Add(row);
        return new(Results.Json(View(row), statusCode: 201), row.Id, request.Reason);
    }, ct);

    public async Task<IResult> ProbeAsync(Guid id, ProbeStorage request, CancellationToken ct)
    {
        StorageLocation? snapshot = null;
        var preflight = await RunAsync("probe", async () =>
        {
            if (id == Guid.Empty || request.ExpectedVersion < 1 || !DirectoryRules.ValidReason(request.Reason)) return Reject(400);
            snapshot = await db.Set<StorageLocation>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (snapshot is null) return Reject(404);
            return snapshot.Version != request.ExpectedVersion ? Reject(409) : new(Results.Ok());
        }, ct, auditSuccess: false);
        if (Status(preflight) >= 400) return preflight;
        var health = await ProbeOutsideLockAsync(snapshot!, ct);
        return await RunAsync("probe", async () =>
        {
            var row = await db.Set<StorageLocation>().SingleAsync(x => x.Id == id, ct);
            if (row.Version != snapshot!.Version || row.Version == long.MaxValue) return Reject(409);
            if (!runtime.Matches(row)) health = Unavailable(id);
            row.Health = health.Status; row.CheckedAtUtc = health.CheckedAtUtc; row.FreeBytes = health.FreeBytes; row.TotalBytes = health.TotalBytes;
            row.Version++;
            return new(health.Status == "unavailable" ? Problem(503) : Results.Ok(runtime.MergeIntegrity(health)), id, request.Reason,
                PersistFailure: true, Committed: () => runtime.Remember(row, health));
        }, ct);
    }

    public async Task<IResult> SwitchAsync(SwitchWriteTarget request, CancellationToken ct)
    {
        StorageLocation? snapshot = null;
        var preflight = await RunAsync("switch", async () =>
        {
            if (request.StorageId == Guid.Empty || request.ExpectedVersion < 1 || !DirectoryRules.ValidReason(request.Reason)) return Reject(400);
            var target = await db.Set<StorageWriteTarget>().AsNoTracking().SingleOrDefaultAsync(ct);
            if ((target?.Version ?? 1) != request.ExpectedVersion || request.ExpectedVersion == long.MaxValue) return Reject(409);
            snapshot = await db.Set<StorageLocation>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.StorageId, ct);
            if (snapshot is null) return Reject(404);
            if (!runtime.Matches(snapshot)) return Reject(503);
            return !runtime.Ready(snapshot) ? Reject(409) : new(Results.Ok());
        }, ct, auditSuccess: false);
        if (Status(preflight) >= 400) return preflight;
        var health = await ProbeOutsideLockAsync(snapshot!, ct);
        return await RunAsync("switch", async () =>
        {
            var row = await db.Set<StorageLocation>().AsNoTracking().SingleAsync(x => x.Id == request.StorageId, ct);
            if (health.Status is not ("ready" or "warning") || !runtime.Matches(row)) { runtime.Invalidate(row.Id); return Reject(503); }
            var target = await db.Set<StorageWriteTarget>().SingleOrDefaultAsync(ct);
            if ((target?.Version ?? 1) != request.ExpectedVersion || row.Version != snapshot!.Version || !runtime.Ready(row)) return Reject(409);
            if (target is null) { target = new StorageWriteTarget(); db.Add(target); }
            target.StorageId = row.Id; target.Version++;
            return new(Results.Ok(new StorageTargetView(target.StorageId, target.Version)), row.Id, request.Reason);
        }, ct);
    }

    private async Task<StorageHealthView> ProbeOutsideLockAsync(StorageLocation row, CancellationToken ct)
    {
        try
        {
            var health = await runtime.Resolve(row).ProbeAsync(ct);
            return health with { Status = health.Status == "healthy" ? "ready" : health.Status };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return Unavailable(row.Id); }
    }

    private StorageHealthView Unavailable(Guid id) => new(id, "unavailable", null, null, 0, 0, clock.GetUtcNow(), "storage-unavailable");
    public Task<IResult> RejectBindingAsync(CancellationToken ct) => RunAsync("binding", () => Task.FromResult(Reject(400)), ct);

    private async Task<IResult> RunAsync(string action, Func<Task<Change>> work, CancellationToken ct, bool auditSuccess = true)
    {
        var status = 403;
        try
        {
            await using (var tx = await ScopeOperation.BeginAsync(db, ct))
            {
                permission.ActingRoleId = null;
                var identity = await session.ValidateSessionAsync(true, ct);
                if (identity.Status is { } denial) status = denial;
                else if (await guard.AllowsAsync(identity.ActorId, Capability, ct))
                {
                    Change change;
                    try { change = await work(); }
                    catch (IOException) { change = Reject(503); }
                    status = Status(change.Result);
                    if (status < 400 || change.PersistFailure)
                    {
                        if (auditSuccess) await WriteAuditAsync("attendance.storage." + (change.AuditAction ?? action), change.Target, status < 400 ? "success" : "failure", change.Reason, ct);
                        await db.SaveChangesAsync(ct);
                        await tx.CommitAsync(ct);
                        change.Committed?.Invoke();
                        return change.Result;
                    }
                }
                await tx.RollbackAsync(ct);
            }
            db.ChangeTracker.Clear();
            await using var denied = await ScopeOperation.BeginAsync(db, ct);
            await WriteAuditAsync("attendance.storage.denied", null, "denied", "request-denied-" + status, ct);
            await db.SaveChangesAsync(ct); await denied.CommitAsync(ct);
            return Problem(status);
        }
        catch (Exception error) when (ScopeOperation.IsDatabaseFault(error)) { db.ChangeTracker.Clear(); return Problem(503); }
    }

    private Task WriteAuditAsync(string action, Guid? target, string outcome, string? reason, CancellationToken ct)
    {
        var metadata = new Dictionary<string, string> { ["capability"] = Capability };
        if (DirectoryRules.ValidReason(reason)) metadata["reason"] = reason!.Trim();
        return audit.WriteAsync(new SecurityAuditRequest(Actor == Guid.Empty ? null : Actor, permission.ActingRoleId, null, null, null,
            action, "attendance-storage", target, outcome, metadata), ct);
    }

    private StorageView View(StorageLocation row) => new(row.Id, row.Alias, row.Kind, row.Version, row.AcceptWrites, runtime.Health(row).Status, row.CheckedAtUtc);
    private static bool ValidPage(int offset, int limit) => offset >= 0 && limit is >= 1 and <= 100;
    private static int Status(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 500;
    private static Change Reject(int status) => new(Problem(status));
    internal static IResult Problem(int status) => Results.Problem(statusCode: status, type: $"urn:tpr10:storage-{status}", title: status switch
    {
        400 => "ข้อมูลที่เก็บหรือคำขอไม่ถูกต้อง",
        401 => "กรุณาเข้าสู่ระบบใหม่",
        403 => "ไม่มีสิทธิ์หรือจำเป็นต้องยืนยันตัวตนเพิ่มเติม",
        404 => "ไม่พบที่เก็บที่กำหนดไว้",
        409 => "ข้อมูลหรือความพร้อมเปลี่ยนแปลง กรุณาโหลดใหม่และตรวจที่เก็บอีกครั้ง",
        _ => "ที่เก็บหรือบริการไม่พร้อมใช้งาน กรุณาลองใหม่ภายหลัง"
    });
}
