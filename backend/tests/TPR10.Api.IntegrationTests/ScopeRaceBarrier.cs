using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace TPR10.Api.IntegrationTests;

// One-shot, test-only gate. An actual SQL command reaches the selected boundary;
// no sleeps are used to infer ordering. Timeout only prevents a hanging test.
internal sealed class ScopeRaceBarrier(bool afterLock) : DbCommandInterceptor
{
    private int armed;
    public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Arm() => Interlocked.Exchange(ref armed, 1);
    private async Task PauseAsync(DbCommand command, bool after, CancellationToken ct)
    {
        if (after != afterLock || !command.CommandText.Contains("pg_advisory_xact_lock(7241002)", StringComparison.Ordinal)
            || Interlocked.CompareExchange(ref armed, 0, 1) != 1) return;
        Arrived.TrySetResult();
        await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
    }
    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    { await PauseAsync(command, false, cancellationToken); return result; }
    public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    { await PauseAsync(command, true, cancellationToken); return result; }
}
