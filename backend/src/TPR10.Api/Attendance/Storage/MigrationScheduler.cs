using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TPR10.Api.Data;
using TPR10.Api.Scopes;

namespace TPR10.Api.Attendance.Storage;

public sealed class MigrationScheduler(IServiceScopeFactory scopes, IOptionsMonitor<StorageOptions> options,
    TimeProvider clock, ILogger<MigrationScheduler> logger) : BackgroundService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private Guid? after;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await ProcessNextAsync(stoppingToken); }
                catch (Exception error) when (ScopeOperation.IsDatabaseFault(error) || error is IOException or UnauthorizedAccessException)
                { logger.LogWarning("Attendance migration unavailable; durable lease retained for recovery"); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    // Round-robin avoids a waiting reservation or retry starving later jobs. Database leases coordinate processes.
    public async Task ProcessNextAsync(CancellationToken ct)
    {
        if (!options.CurrentValue.MigrationWorkerEnabled || !await gate.WaitAsync(0, ct)) return;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>();
            var jobs = db.Set<MigrationJob>().AsNoTracking().Where(x => x.Status == "Pending" || x.Status == "Running");
            var id = await jobs.Where(x => after == null || x.Id.CompareTo(after.Value) > 0).OrderBy(x => x.Id).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
            id ??= await jobs.OrderBy(x => x.Id).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
            if (id is not { } selected) { after = null; return; }
            after = selected;
            await scope.ServiceProvider.GetRequiredService<MigrationWorker>().RunBatchAsync(selected, 100, ct);
        }
        finally { gate.Release(); }
    }
}
