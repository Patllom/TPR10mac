using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Requests;
using L = TPR10.Api.IntegrationTests.FieldAdvanceRequestLifecycleTests;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class RequestRoutingWorkerTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("postgres", "PostgresException", "23505")]
    [InlineData("update", "DbUpdateException", "23505")]
    [InlineData("transport", "NpgsqlException", null)]
    [InlineData("unknown", "DbException", null)]
    [InlineData("invalid-state", "PostgresException", null)]
    public async Task Request_failure_logs_safe_identity_and_retries_without_starving_next_request(
        string fault, string kind, string? sqlState)
    {
        await using var f = await L.Ready(postgres);
        await L.Submit(f, await L.Create(f)); await L.Submit(f, await L.Create(f));
        await using var db = f.CreateContext();
        var ids = await db.Set<AdvanceRequest>().OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync();
        // The real resolver must move both requests to Held once their supervisor is absent.
        await db.Set<GrantAssignment>().Where(x => db.Set<GrantProposal>().Any(p => p.Id == x.ProposalId && p.ProjectId == ProjectId && p.RecipientId == ReviewerId))
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.RevokedAtUtc, f.Clock.GetUtcNow()).SetProperty(p => p.RevokedBy, AuthorId).SetProperty(p => p.RevocationReason, "ทดสอบถอน"));
        var injection = new RoutingFault(ids[0], fault);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock,
            new(keys.Settings) { ["FieldAdvances:RequestRouting:IntervalSeconds"] = "3600", ["Logging:LogLevel:Default"] = "None" }, injection);
        var logs = new WorkerLogs();
        using var worker = Worker(factory, f, logs);
        await worker.StartAsync(default);
        try
        {
            await Until(async () => logs.Entries.Count >= 2 &&
                await db.Set<AdvanceRequest>().AnyAsync(x => x.Id == ids[1] && x.State == "Held"));
            Assert.Equal("PendingApproval", await db.Set<AdvanceRequest>().Where(x => x.Id == ids[0]).Select(x => x.State).SingleAsync());
            foreach (var log in logs.Entries)
            {
                AssertSafe(log, "reevaluate", kind, sqlState);
                Assert.Equal(ids[0], log.Fields["RequestId"]);
            }
            injection.Enabled = false;
            await Until(() => db.Set<AdvanceRequest>().AllAsync(x => x.State == "Held"));
        }
        finally { await worker.StopAsync(default); }
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Batch_failure_logs_phase_without_request_identity_and_recovers_on_next_tick()
    {
        await using var f = await L.Ready(postgres);
        await L.Submit(f, await L.Create(f));
        await using var db = f.CreateContext();
        await db.Set<GrantAssignment>().Where(x => db.Set<GrantProposal>().Any(p => p.Id == x.ProposalId && p.ProjectId == ProjectId && p.RecipientId == ReviewerId))
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.RevokedAtUtc, f.Clock.GetUtcNow()).SetProperty(p => p.RevokedBy, AuthorId).SetProperty(p => p.RevocationReason, "ทดสอบถอน"));
        var injection = new RoutingFault(null, "postgres");
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock,
            new(keys.Settings) { ["FieldAdvances:RequestRouting:IntervalSeconds"] = "3600", ["Logging:LogLevel:Default"] = "None" }, injection);
        var logs = new WorkerLogs();
        using var worker = Worker(factory, f, logs);
        await worker.StartAsync(default);
        try
        {
            await Until(() => Task.FromResult(logs.Entries.Count >= 2));
            foreach (var log in logs.Entries)
            {
                AssertSafe(log, "batch-read", "PostgresException", "23505");
                Assert.False(log.Fields.ContainsKey("RequestId"));
            }
            injection.Enabled = false;
            await Until(() => db.Set<AdvanceRequest>().AllAsync(x => x.State == "Held"));
        }
        finally { await worker.StopAsync(default); }
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
    }

    private static RequestRoutingWorker Worker(ApiFactory factory, FieldAdvanceFixture f, WorkerLogs logs)
        => new(factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FieldAdvances:RequestRouting:IntervalSeconds"] = "0.02",
                ["FieldAdvances:RequestRouting:BatchSize"] = "1"
            }).Build(), f.Clock, logs);

    private static void AssertSafe(LogEntry log, string phase, string kind, string? sqlState)
    {
        Assert.Equal(LogLevel.Warning, log.Level); Assert.Null(log.Exception);
        Assert.True(log.Fields.TryGetValue("Phase", out var actualPhase), "worker warning must include structured Phase");
        Assert.Equal(phase, actualPhase);
        Assert.Equal(kind, log.Fields["ExceptionKind"]); Assert.Equal(sqlState, log.Fields["SqlState"]);
        Assert.DoesNotContain("PRIVATE", log.Message);
        Assert.All(log.Fields.Values, value => Assert.DoesNotContain("PRIVATE", value?.ToString() ?? ""));
    }

    private static async Task Until(Func<Task<bool>> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15), "worker failed to make progress");
            await Task.Delay(20);
        }
    }

    // Fault injection at the database boundary: resolver, transaction, worker loop and logging stay real.
    private sealed class RoutingFault(Guid? requestId, string fault) : DbCommandInterceptor
    {
        public volatile bool Enabled = true;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            var isLock = command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal);
            if (Enabled && command.CommandText.Contains("field_advance_requests", StringComparison.Ordinal) &&
                (requestId is null ? !isLock : isLock && command.Parameters.Cast<DbParameter>().Any(p => Equals(p.Value, requestId.Value))))
            {
                var pg = new PostgresException("PRIVATE message SQL credentials", "ERROR", "ERROR",
                    fault == "invalid-state" ? "PRIVATE\nstate" : "23505", detail: "PRIVATE detail");
                throw fault switch
                {
                    "update" => new DbUpdateException("PRIVATE wrapper", pg),
                    "transport" => new NpgsqlException("PRIVATE transport"),
                    "unknown" => new PRIVATEUntrustedException(),
                    _ => pg
                };
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class PRIVATEUntrustedException() : DbException("PRIVATE database")
    {
        public override string SqlState => "PRIVATE untrusted state";
    }

    private sealed record LogEntry(LogLevel Level, Exception? Exception, string Message, Dictionary<string, object?> Fields);
    private sealed class WorkerLogs : ILogger<RequestRoutingWorker>
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue(new(logLevel, exception, formatter(state, exception),
                ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary()));
    }
}
