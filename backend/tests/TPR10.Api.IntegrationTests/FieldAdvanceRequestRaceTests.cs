using System.Reflection;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Requests;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvanceRequestLifecycleTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceRequestRaceTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(null, 30)]
    [InlineData("2", 2)]
    public async Task Worker_uses_virtual_time_fresh_scopes_and_cursor_past_unchanged_first_page(string? configuredSeconds, int seconds)
    {
        await using var f = await Ready(postgres);
        var requests = new List<RequestView>();
        for (var i = 0; i < 3; i++) requests.Add(await Submit(f, await Create(f)));
        await using var db = f.CreateContext();
        var ordered = await db.Set<AdvanceRequest>().OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync();
        var session = await f.SessionAsync(AuthorId); var timer = new TickClock(); var contexts = new List<Tpr10DbContext>();
        var disposed = Channel.CreateUnbounded<bool>();
        var services = new ServiceCollection().AddLogging().AddSingleton<TimeProvider>(timer)
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["FieldAdvances:RequestRouting:IntervalSeconds"] = configuredSeconds, ["FieldAdvances:RequestRouting:BatchSize"] = "1" }).Build())
            .AddScoped(_ => new ScopeCompleted(() => disposed.Writer.TryWrite(true)))
            .AddScoped(sp => { _ = sp.GetRequiredService<ScopeCompleted>(); var context = f.CreateContext(); contexts.Add(context); return context; })
            .AddScoped(sp =>
            {
                var context = sp.GetRequiredService<Tpr10DbContext>(); var access = FieldAdvanceAccessTests.Access(context, session, f.Clock);
                var people = new FinancePersonResolver(context, f.Clock);
                return new RequestReviewService(context, access, people, new(context, access, people, f.Clock), Draft(f, context, session),
                    new(context, people, f.Clock), new AuditEventWriter(context, new CorrelationContext(), f.Clock), f.Clock);
            });
        using var provider = services.BuildServiceProvider();
        var type = typeof(RequestReviewService).Assembly.GetType("TPR10.Api.FieldAdvances.Requests.RequestRoutingWorker"); Assert.NotNull(type);
        using var worker = (BackgroundService)ActivatorUtilities.CreateInstance(provider, type);
        await worker.StartAsync(default); await timer.Created.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(seconds), timer.Period);
        async Task Tick(int scopes)
        {
            timer.Tick();
            for (var i = 0; i < scopes; i++)
            {
                var done = disposed.Reader.ReadAsync().AsTask();
                var completed = await Task.WhenAny(done, worker.ExecuteTask!).WaitAsync(TimeSpan.FromSeconds(15));
                if (completed == worker.ExecuteTask) await worker.ExecuteTask!;
                Assert.True(await done.WaitAsync(TimeSpan.FromSeconds(15)));
            }
        }
        await Tick(2);
        // หน้าแรกไม่เปลี่ยน ตอนต่อไปต้องไปหน้าที่สองแม้หน้าแรกยังอยู่ในสถานะ pending
        await ExpireReviewer(f);
        await Tick(2);
        Assert.Equal("Held", (await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == ordered[1])).State);
        Assert.Equal("PendingApproval", (await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == ordered[0])).State);
        await Tick(2);
        Assert.Equal("Held", (await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == ordered[2])).State);
        await Tick(1); // จบหน้าแล้ววน cursor กลับต้นรายการ
        await Tick(2);
        Assert.Equal("Held", (await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == ordered[0])).State);
        await worker.StopAsync(default);
        Assert.True(contexts.Distinct().Count() >= 6);
        Assert.Equal(3, await db.AuditEvents.CountAsync(x => x.EventType == "field-advance.request.reevaluate"));
    }

    private sealed class ScopeCompleted(Action completed) : IDisposable
    {
        public void Dispose() => completed();
    }
    private sealed class TickClock : TimeProvider
    {
        private TimerCallback? callback; private object? state;
        internal TaskCompletionSource Created { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TimeSpan Period { get; private set; }
        public override ITimer CreateTimer(TimerCallback cb, object? value, TimeSpan dueTime, TimeSpan period)
        { callback = cb; state = value; Period = period; Created.TrySetResult(); return new Timer(); }
        internal void Tick() => callback!(state);
        private sealed class Timer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Final_approval_races_with_withdrawal(bool approveFirst)
    {
        await using var f = await Ready(postgres); var request = await FinalStep(f);
        await using var a = f.CreateContext(); await using var b = f.CreateContext();
        var gate = new Gate();
        var review = await Service(f, a, AliasAccountId, "RequestReviewService", gate.Audit(a, f, approveFirst));
        var withdraw = await Service(f, b, AuthorId, "RequestRevisionService", gate.Audit(b, f, !approveFirst));
        var step = await a.Set<AdvanceApprovalStep>().SingleAsync(x => x.RequestId == request.Id && x.Outcome == null);
        var approveMeta = Meta(request.Version); var withdrawMeta = Meta(request.Version);
        Task<FinanceResult<RequestView>> Approve() => Invoke(review, "DecideAsync", AliasAccountId, request.Id,
            new RequestDecision("Approve", step.Id), approveMeta, CancellationToken.None);
        Task<FinanceResult<RequestView>> Withdraw() => Invoke(withdraw, "WithdrawAsync", AuthorId, request.Id, withdrawMeta, CancellationToken.None);
        var first = approveFirst ? Approve() : Withdraw();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var second = approveFirst ? Withdraw() : Approve();
        try { await WaitForLock(f, second); } finally { gate.Release.TrySetResult(); }
        Assert.Equal(200, (await first).Status); Assert.Contains((await second).Status, new[] { 403, 409 });
        await using var check = f.CreateContext();
        var saved = await check.Set<AdvanceRequest>().SingleAsync();
        Assert.Equal(approveFirst ? "Approved" : "Withdrawn", saved.State);
        Assert.Equal(request.Version + 1, saved.Version);
        Assert.Single(await check.Set<FinanceCommandReceipt>().Where(x => x.CommandId == approveMeta.CommandId || x.CommandId == withdrawMeta.CommandId).ToArrayAsync());
        var final = await check.Set<AdvanceApprovalStep>().SingleAsync(x => x.Id == step.Id);
        Assert.Equal(approveFirst ? "Approved" : null, final.Outcome);
        var before = await Snapshot(f); Assert.False(await Reevaluate(f, request.Id)); Assert.Equal(before, await Snapshot(f));
        Assert.Equal(403, (await Command(f, request with { Version = saved.Version, State = saved.State }, "WithdrawAsync")).Status);
    }

    [Fact]
    public async Task Two_workers_have_one_assignment_effect()
    {
        await using var f = await Ready(postgres); var request = await Submit(f, await Create(f)); await ExpireReviewer(f);
        var results = await Task.WhenAll(Reevaluate(f, request.Id), Reevaluate(f, request.Id));
        Assert.Single(results, x => x);
        await using var db = f.CreateContext(); Assert.Equal(3, (await db.Set<AdvanceRequest>().SingleAsync()).Version);
        Assert.Single(await db.AuditEvents.Where(x => x.EventType == "field-advance.request.reevaluate").ToArrayAsync());
        Assert.All(await db.Set<AdvanceApprovalStep>().ToArrayAsync(), x => Assert.Null(x.Outcome));
    }

    [Fact]
    public async Task System_reevaluation_needs_no_session_and_audit_failure_rolls_back()
    {
        await using var f = await Ready(postgres); var request = await Submit(f, await Create(f)); await ExpireReviewer(f);
        await using var db = f.CreateContext(); var service = await Service(f, db, AuthorId, "RequestReviewService");
        await db.Set<TPR10.Api.Identity.Data.IdentitySession>().ExecuteDeleteAsync();
        var before = await Snapshot(f);
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION fail_system_route() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'test-system-route'; END $$; CREATE TRIGGER fail_system_route BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION fail_system_route();");
        Task<bool> Run() => (Task<bool>)service.GetType().GetMethod("ReevaluateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [request.Id, CancellationToken.None])!;
        var error = await Assert.ThrowsAnyAsync<Exception>(Run);
        while (error.InnerException is not null) error = error.InnerException;
        Assert.Equal("P0001", Assert.IsType<Npgsql.PostgresException>(error).SqlState);
        Assert.Equal(before, await Snapshot(f)); Assert.Empty(db.ChangeTracker.Entries());
        await db.Database.ExecuteSqlRawAsync("DROP FUNCTION fail_system_route() CASCADE");
        Assert.True(await Run()); Assert.False(await Run());
        Assert.Empty(await db.Set<TPR10.Api.Identity.Data.IdentitySession>().ToArrayAsync());
        Assert.Null((await db.AuditEvents.SingleAsync(x => x.EventType == "field-advance.request.reevaluate")).ActorId);
        Assert.All(await db.Set<AdvanceApprovalStep>().ToArrayAsync(), x => { Assert.Null(x.DecidedBy); Assert.Null(x.Outcome); });
    }

    [Fact]
    public async Task Concurrent_same_decision_has_one_receipt_event_audit_and_participant()
    {
        await using var f = await Ready(postgres); var request = await Submit(f, await Create(f));
        await using var a = f.CreateContext(); await using var b = f.CreateContext();
        var step = await a.Set<AdvanceApprovalStep>().SingleAsync(x => x.RevisionId == request.RevisionId && x.StepOrder == 1);
        var first = await Service(f, a, ReviewerId, "RequestReviewService"); var second = await Service(f, b, ReviewerId, "RequestReviewService");
        var meta = Meta(request.Version); var decision = new RequestDecision("Approve", step.Id);
        var results = await Task.WhenAll(Invoke(first, "DecideAsync", ReviewerId, request.Id, decision, meta, CancellationToken.None),
            Invoke(second, "DecideAsync", ReviewerId, request.Id, decision, meta, CancellationToken.None));
        Assert.All(results, x => Assert.Equal(200, x.Status));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(results[0]), System.Text.Json.JsonSerializer.Serialize(results[1]));
        Assert.Single(await a.Set<FinanceCommandReceipt>().Where(x => x.CommandId == meta.CommandId).ToArrayAsync());
        Assert.Single(await a.Set<AdvanceRequestEvent>().Where(x => x.CommandId == meta.CommandId).ToArrayAsync());
        Assert.Single(await a.AuditEvents.Where(x => x.EventType == "field-advance.request.request.decide").ToArrayAsync());
        Assert.Single(await a.Set<AdvanceRequestParticipant>().Where(x => x.Role == "Approver").ToArrayAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Revocation_serializes_with_approval(bool approveFirst)
    {
        await using var f = await Ready(postgres); var request = await Submit(f, await Create(f));
        await using var a = f.CreateContext(); await using var b = f.CreateContext(); var gate = new Gate();
        var service = await Service(f, a, ReviewerId, "RequestReviewService", gate.Audit(a, f, approveFirst));
        var step = await a.Set<AdvanceApprovalStep>().SingleAsync(x => x.RequestId == request.Id && x.StepOrder == 1);
        Task<FinanceResult<RequestView>> Approve() => Invoke(service, "DecideAsync", ReviewerId, request.Id,
            new RequestDecision("Approve", step.Id), Meta(request.Version), CancellationToken.None);
        async Task Revoke()
        {
            await using var tx = await b.Database.BeginTransactionAsync();
            await b.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
            await b.Set<GrantAssignment>().Where(x => x.ProjectId == ProjectId && b.Set<GrantProposal>().Any(p => p.Id == x.ProposalId && p.RecipientId == ReviewerId))
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.RevokedAtUtc, f.Clock.GetUtcNow()).SetProperty(p => p.RevokedBy, AuthorId)
                    .SetProperty(p => p.RevocationReason, "ถอนสิทธิ์ทดสอบ"));
            if (!approveFirst) { gate.Entered.TrySetResult(); await gate.Release.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
            await tx.CommitAsync();
        }
        Task<FinanceResult<RequestView>> approve; Task revoke;
        if (approveFirst) { approve = Approve(); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); revoke = Revoke(); }
        else { revoke = Revoke(); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); approve = Approve(); }
        try { await WaitForLock(f, approveFirst ? revoke : approve); } finally { gate.Release.TrySetResult(); }
        await revoke; Assert.Equal(approveFirst ? 200 : 403, (await approve).Status);
        await using var check = f.CreateContext();
        Assert.Equal(approveFirst ? "Approved" : null, (await check.Set<AdvanceApprovalStep>().SingleAsync(x => x.Id == step.Id)).Outcome);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reassignment_serializes_with_withdrawal(bool workerFirst)
    {
        await using var f = await Ready(postgres); var request = await Submit(f, await Create(f)); await ExpireReviewer(f);
        await using var a = f.CreateContext(); await using var b = f.CreateContext(); var gate = new Gate();
        var worker = await Service(f, a, AuthorId, "RequestReviewService", gate.Audit(a, f, workerFirst));
        var owner = await Service(f, b, AuthorId, "RequestRevisionService", gate.Audit(b, f, !workerFirst));
        Task<bool> Reassign() => (Task<bool>)worker.GetType().GetMethod("ReevaluateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(worker, [request.Id, CancellationToken.None])!;
        Task<FinanceResult<RequestView>> Withdraw() => Invoke(owner, "WithdrawAsync", AuthorId, request.Id, Meta(request.Version), CancellationToken.None);
        Task<bool> route; Task<FinanceResult<RequestView>> withdraw;
        if (workerFirst) { route = Reassign(); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); withdraw = Withdraw(); }
        else { withdraw = Withdraw(); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); route = Reassign(); }
        try { await WaitForLock(f, workerFirst ? withdraw : route); } finally { gate.Release.TrySetResult(); }
        Assert.Equal(workerFirst, await route); Assert.Equal(workerFirst ? 409 : 200, (await withdraw).Status);
        await using var db = f.CreateContext();
        var saved = await db.Set<AdvanceRequest>().SingleAsync(); Assert.Equal(workerFirst ? "Held" : "Withdrawn", saved.State);
        Assert.Equal(request.Version + 1, saved.Version);
    }

    [Theory]
    [InlineData("audit")]
    [InlineData("receipt")]
    [InlineData("commit")]
    public async Task Failure_after_decision_rolls_back_receipt_audit_assignment_and_participant(string failure)
    {
        await using var f = await Ready(postgres); var request = await Submit(f, await Create(f));
        await using var db = f.CreateContext(); var step = await db.Set<AdvanceApprovalStep>().SingleAsync(x => x.RequestId == request.Id && x.StepOrder == 1);
        var before = await Snapshot(f); var participants = await db.Set<AdvanceRequestParticipant>().CountAsync();
        await db.Database.ExecuteSqlRawAsync(failure switch
        {
            "audit" => "CREATE FUNCTION fail_lifecycle() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'test-lifecycle'; END $$; CREATE TRIGGER fail_lifecycle BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION fail_lifecycle();",
            "receipt" => "CREATE FUNCTION fail_lifecycle() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'test-lifecycle'; END $$; CREATE TRIGGER fail_lifecycle AFTER INSERT ON field_advance_command_receipts FOR EACH ROW EXECUTE FUNCTION fail_lifecycle();",
            _ => "CREATE FUNCTION fail_lifecycle() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'test-lifecycle'; END $$; CREATE CONSTRAINT TRIGGER fail_lifecycle AFTER INSERT ON field_advance_command_receipts DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fail_lifecycle();"
        });
        var service = await Service(f, db, ReviewerId, "RequestReviewService"); var meta = Meta(request.Version);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Invoke(service, "DecideAsync", ReviewerId, request.Id,
            new RequestDecision("Approve", step.Id), meta, CancellationToken.None));
        while (error.InnerException is not null) error = error.InnerException;
        Assert.Equal("P0001", Assert.IsType<Npgsql.PostgresException>(error).SqlState);
        Assert.Empty(db.ChangeTracker.Entries()); await db.SaveChangesAsync();
        Assert.Equal(before, await Snapshot(f)); Assert.Equal(participants, await db.Set<AdvanceRequestParticipant>().CountAsync());
        await db.Database.ExecuteSqlRawAsync("DROP FUNCTION fail_lifecycle() CASCADE");
        Assert.Equal(200, (await Invoke(service, "DecideAsync", ReviewerId, request.Id, new RequestDecision("Approve", step.Id), meta, CancellationToken.None)).Status);
    }

    internal static async Task<RequestView> FinalStep(FieldAdvanceFixture f)
    {
        var director = Guid.NewGuid(); await using var db = f.CreateContext();
        db.Add(new FinancePerson { Id = director, WorkspaceId = WorkspaceId, CreatedBy = AuthorId, Reason = "ผู้อนุมัติขั้นสุดท้าย" });
        db.Add(new FinanceAccountLink
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            UserId = AliasAccountId,
            PersonId = director,
            CreatedBy = AuthorId,
            CreatedAtUtc = f.Clock.GetUtcNow(),
            Reason = "ทดสอบ"
        });
        await db.SaveChangesAsync();
        var grant = await FieldAdvanceGrantTests.AuthorityAsync(f, AliasAccountId, director);
        await db.Set<GrantProposal>().Where(x => db.Set<GrantAssignment>().Any(a => a.Id == grant && a.ProposalId == x.Id))
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "request.approve", "report.read" })
                .SetProperty(p => p.ApprovalLevels, new[] { "director" }).SetProperty(p => p.PrimaryApprovalLevels, new[] { "director" }));
        var submitted = await Submit(f, await Create(f));
        var first = await Decide(f, submitted, "Approve"); Assert.Equal(200, first.Status);
        var second = await Decide(f, first.Value!, "Approve", RecipientId); Assert.Equal(200, second.Status);
        return second.Value!;
    }
    internal sealed class CallbackAudit(IAuditEventWriter inner, Func<Task> after) : IAuditEventWriter
    {
        public Task WriteAsync(string type, Guid id, IReadOnlyDictionary<string, string> metadata, CancellationToken ct, Guid? actorId = null)
            => inner.WriteAsync(type, id, metadata, ct, actorId);
        public async Task WriteAsync(SecurityAuditRequest request, CancellationToken ct) { await inner.WriteAsync(request, ct); await after(); }
    }
    private sealed class Gate
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IAuditEventWriter Audit(Tpr10DbContext db, FieldAdvanceFixture f, bool block) => new CallbackAudit(
            new AuditEventWriter(db, new CorrelationContext(), f.Clock), async () =>
            { if (block) { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(15)); } });
    }
    private static async Task WaitForLock(FieldAdvanceFixture f, Task waiting)
    {
        await using var probe = f.CreateContext(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await probe.Database.SqlQueryRaw<bool>("SELECT EXISTS(SELECT 1 FROM pg_locks WHERE locktype='advisory' AND objid=7241002 AND NOT granted) AS \"Value\"").SingleAsync(deadline.Token))
        { Assert.False(waiting.IsCompleted); await Task.Yield(); }
    }
}
