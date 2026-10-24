using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Text.Json;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Requests;
using TPR10.Api.Organization.Data;
using TPR10.Api.Attendance.Directory;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using P = TPR10.Api.IntegrationTests.FieldAdvancePolicyTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceRequestLifecycleTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("RequestReviewService")]
    [InlineData("RequestRevisionService")]
    public void Lifecycle_commands_are_available_in_request_scope(string service)
        => Assert.Contains(new ServiceCollection().AddFieldAdvances(), x =>
            x.ServiceType.FullName == "TPR10.Api.FieldAdvances.Requests." + service
            && x.Lifetime == ServiceLifetime.Scoped);

    [Fact]
    public async Task Returned_revision_restarts_all_steps()
    {
        await using var f = await Ready(postgres);
        var submitted = await Submit(f, await Create(f));
        var returned = await Decide(f, submitted, "Return");
        Assert.Equal(200, returned.Status); Assert.Equal("Returned", returned.Value!.State);
        var old = await Frozen(f, submitted.RevisionId);
        var revised = await Command(f, returned.Value, "ReviseAsync");
        Assert.Equal(200, revised.Status); Assert.Equal("Draft", revised.Value!.State);
        Assert.NotEqual(submitted.RevisionId, revised.Value.RevisionId);
        var again = await Submit(f, revised.Value);
        await using var db = f.CreateContext();
        Assert.Equal(old, await Frozen(f, submitted.RevisionId));
        Assert.False((await db.Set<AdvanceRequestRevision>().SingleAsync(x => x.Id == submitted.RevisionId)).IsCurrent);
        var steps = await db.Set<AdvanceApprovalStep>().Where(x => x.RevisionId == again.RevisionId).OrderBy(x => x.StepOrder).ToArrayAsync();
        Assert.Equal(3, steps.Length); Assert.All(steps, x => Assert.Null(x.Outcome));
        Assert.Equal(ReviewerPersonId, steps[0].AssigneePersonId);
        Assert.All(steps.Skip(1), x => Assert.Null(x.AssigneePersonId));
        Assert.Equal(AuthorPersonId, again.OwnerPersonId); Assert.Null(again.DelegatePersonId);
    }

    [Fact]
    public async Task Prior_approver_can_vote_again_only_once()
    {
        await using var f = await Ready(postgres);
        var first = await Submit(f, await Create(f));
        var approved = await Decide(f, first, "Approve"); Assert.Equal(200, approved.Status);
        var returned = await Decide(f, approved.Value!, "Return", RecipientId); Assert.Equal(200, returned.Status);
        var revised = await Command(f, returned.Value!, "ReviseAsync");
        var second = await Submit(f, revised.Value!);
        var voted = await Decide(f, second, "Approve"); Assert.Equal(200, voted.Status);
        Assert.Equal(403, (await Decide(f, voted.Value!, "Approve")).Status);
        await using var db = f.CreateContext();
        Assert.Equal(2, await db.Set<AdvanceApprovalStep>().CountAsync(x => x.DecidedByPersonId == ReviewerPersonId && x.Outcome == "Approved"));
        Assert.Single(await db.Set<AdvanceRequestParticipant>().Where(x => x.PersonId == ReviewerPersonId && x.Role == "Approver").ToArrayAsync());
        Assert.Single(await db.Set<AdvanceRequestParticipant>().Where(x => x.PersonId == AuthorPersonId && x.Role == "Owner").ToArrayAsync());
    }

    [Fact]
    public async Task Owner_only_can_withdraw_before_final()
    {
        await using var f = await Ready(postgres);
        var request = await Submit(f, await Create(f));
        Assert.Equal(403, (await Command(f, request, "WithdrawAsync", RecipientId)).Status);
        Assert.Equal(400, (await Command(f, request, "WithdrawAsync", meta: new(Guid.NewGuid(), request.Version, ""))).Status);
        var meta = Meta(request.Version);
        var withdrawn = await Command(f, request, "WithdrawAsync", meta: meta);
        Assert.Equal(200, withdrawn.Status); Assert.Equal("Withdrawn", withdrawn.Value!.State);
        var before = await Snapshot(f);
        Assert.Equal(JsonSerializer.Serialize(withdrawn), JsonSerializer.Serialize(await Command(f, request, "WithdrawAsync", meta: meta)));
        Assert.False(await Reevaluate(f, request.Id)); Assert.Equal(before, await Snapshot(f));
        await using var db = f.CreateContext();
        Assert.All(await db.Set<AdvanceApprovalStep>().ToArrayAsync(), x =>
        { Assert.Null(x.Outcome); Assert.Null(x.DecidedBy); Assert.Null(x.AssigneePersonId); Assert.Null(x.HoldCode); });
        Assert.Equal(403, (await Command(f, withdrawn.Value, "ReviseAsync")).Status);
    }

    [Fact]
    public async Task Reevaluation_is_idempotent()
    {
        await using var f = await Ready(postgres);
        var request = await Submit(f, await Create(f));
        var frozen = await Frozen(f, request.RevisionId);
        Assert.False(await Reevaluate(f, request.Id));
        await ExpireReviewer(f);
        Assert.True(await Reevaluate(f, request.Id));
        var once = await Snapshot(f);
        Assert.False(await Reevaluate(f, request.Id)); Assert.Equal(once, await Snapshot(f));
        Assert.Equal(frozen, await Frozen(f, request.RevisionId));
        await using var db = f.CreateContext();
        var held = await db.Set<AdvanceRequest>().SingleAsync(); Assert.Equal("Held", held.State); Assert.Equal(3, held.Version);
        var audit = await db.AuditEvents.SingleAsync(x => x.EventType == "field-advance.request.reevaluate");
        Assert.Null(audit.ActorId);
        Assert.Contains(await db.AuditMetadata.Where(x => x.AuditEventId == audit.Id).ToArrayAsync(), x => x.Key == "actor-type" && x.Value == "system");
        await db.Set<GrantProposal>().Where(x => x.RecipientId == ReviewerId && x.ProjectId == ProjectId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.EndsAtUtc, (DateTimeOffset?)null));
        Assert.True(await Reevaluate(f, request.Id));
        Assert.Equal("PendingApproval", (await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync()).State);
        Assert.All(await db.Set<AdvanceApprovalStep>().ToArrayAsync(), x => Assert.Null(x.Outcome));
    }

    [Fact]
    public async Task Return_to_author_and_copy_preserve_identity_and_require_current_authority()
    {
        await using var f = await Ready(postgres);
        var draft = await Create(f, RecipientId);
        var session = await f.SessionAsync(RecipientId); await using var db = f.CreateContext();
        var sent = await Submission(f, db, session).SendToOwnerAsync(RecipientId, draft.Id, Meta(draft.Version), default);
        Assert.Equal(200, sent.Status);
        Assert.Equal(403, (await Command(f, sent.Value!, "ReturnToAuthorAsync", RecipientId)).Status);
        var returned = await Command(f, sent.Value!, "ReturnToAuthorAsync");
        Assert.Equal("Draft", returned.Value!.State); Assert.Equal(draft.RevisionId, returned.Value.RevisionId);
        var withdrawn = await Command(f, returned.Value, "WithdrawAsync");
        var copy = await Command(f, withdrawn.Value!, "CopyAsync", RecipientId);
        Assert.Equal(201, copy.Status); Assert.Equal("Draft", copy.Value!.State);
        Assert.NotEqual(draft.Id, copy.Value.Id); Assert.Equal(AuthorPersonId, copy.Value.OwnerPersonId);
        Assert.Equal(RecipientPersonId, copy.Value.DelegatePersonId);
        Assert.Equal(draft.Id, (await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == copy.Value.Id)).SourceRequestId);
        Assert.Empty(await db.Set<AdvanceApprovalStep>().Where(x => x.RequestId == copy.Value.Id).ToArrayAsync());
        await db.Set<GrantProposal>().Where(x => x.RecipientId == RecipientId && x.ProjectId == ProjectId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "report.read" })
                .SetProperty(p => p.ApprovalLevels, Array.Empty<string>()).SetProperty(p => p.PrimaryApprovalLevels, Array.Empty<string>()));
        Assert.Equal(403, (await Command(f, withdrawn.Value!, "CopyAsync", RecipientId)).Status);
    }

    [Theory]
    [InlineData("Return")]
    [InlineData("Reject")]
    public async Task Negative_decisions_require_reason_and_terminal_cannot_reopen(string outcome)
    {
        await using var f = await Ready(postgres); var request = await Submit(f, await Create(f));
        Assert.Equal(400, (await Decide(f, request, outcome, meta: new(Guid.NewGuid(), request.Version, " "))).Status);
        var result = await Decide(f, request, outcome); Assert.Equal(200, result.Status);
        Assert.Equal(outcome == "Return" ? "Returned" : "Rejected", result.Value!.State);
        var before = await Snapshot(f); Assert.False(await Reevaluate(f, request.Id)); Assert.Equal(before, await Snapshot(f));
        if (outcome == "Reject") Assert.Equal(201, (await Command(f, result.Value, "CopyAsync")).Status);
    }

    internal static RequestCommand Meta(long version) => new(Guid.NewGuid(), version, "เหตุผลทดสอบ");
    [Theory]
    [InlineData("grant")]
    [InlineData("reporting-line")]
    public async Task Authority_expiring_during_audit_rolls_back_decision(string expiring)
    {
        await using var f = await Ready(postgres); var request = await Submit(f, await Create(f));
        await using var db = f.CreateContext();
        if (expiring == "grant")
            await db.Set<GrantProposal>().Where(x => x.RecipientId == ReviewerId && x.ProjectId == ProjectId)
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.EndsAtUtc, f.Clock.GetUtcNow().AddSeconds(1)));
        else await db.Set<ReportingLine>().Where(x => x.EmployeeUserId == AuthorId)
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.ValidToUtc, f.Clock.GetUtcNow().AddSeconds(1)));
        var before = await Snapshot(f);
        var audit = new FieldAdvanceRequestRaceTests.CallbackAudit(new AuditEventWriter(db, new CorrelationContext(), f.Clock), () =>
        { f.Clock.Advance(TimeSpan.FromSeconds(2)); return Task.CompletedTask; });
        var service = await Service(f, db, ReviewerId, "RequestReviewService", audit);
        var step = await db.Set<AdvanceApprovalStep>().SingleAsync(x => x.RevisionId == request.RevisionId && x.StepOrder == 1);
        var result = await Invoke(service, "DecideAsync", ReviewerId, request.Id, new RequestDecision("Approve", step.Id), Meta(request.Version), CancellationToken.None);
        Assert.Equal(403, result.Status); Assert.Equal(before, await Snapshot(f)); Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Replay_returns_historical_result_but_needs_current_authority_and_read()
    {
        await using var f = await Ready(postgres); var request = await Submit(f, await Create(f));
        await using var db = f.CreateContext();
        var step = await db.Set<AdvanceApprovalStep>().SingleAsync(x => x.RevisionId == request.RevisionId && x.StepOrder == 1);
        var service = await Service(f, db, ReviewerId, "RequestReviewService"); var meta = Meta(request.Version);
        Task<FinanceResult<RequestView>> Send(string outcome = "Approve") => Invoke(service, "DecideAsync", ReviewerId, request.Id,
            new RequestDecision(outcome, step.Id), meta, CancellationToken.None);
        var first = await Send(); Assert.Equal(200, first.Status);
        Assert.Equal(200, (await Decide(f, first.Value!, "Return", RecipientId)).Status);
        var before = await Snapshot(f);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(await Send()));
        Assert.Equal(before, await Snapshot(f)); Assert.Equal(409, (await Send("Reject")).Status);
        await db.Set<GrantProposal>().Where(x => x.RecipientId == ReviewerId && x.ProjectId == ProjectId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "request.approve" }));
        Assert.Equal(403, (await Send()).Status);
        await ExpireReviewer(f); Assert.Equal(403, (await Send()).Status);
    }

    [Fact]
    public async Task Stale_version_and_future_step_cannot_change_any_effect()
    {
        await using var f = await Ready(postgres); var request = await Submit(f, await Create(f));
        await using var db = f.CreateContext();
        var steps = await db.Set<AdvanceApprovalStep>().Where(x => x.RevisionId == request.RevisionId).OrderBy(x => x.StepOrder).ToArrayAsync();
        var service = await Service(f, db, ReviewerId, "RequestReviewService"); var before = await Snapshot(f);
        Assert.Equal(409, (await Invoke(service, "DecideAsync", ReviewerId, request.Id, new RequestDecision("Approve", steps[0].Id), Meta(1), CancellationToken.None)).Status);
        Assert.Equal(409, (await Invoke(service, "DecideAsync", ReviewerId, request.Id, new RequestDecision("Approve", steps[1].Id), Meta(request.Version), CancellationToken.None)).Status);
        Assert.Equal(before, await Snapshot(f));
    }

    [Fact]
    public async Task Completed_outcomes_survive_hold_and_request_wide_separation_survives_revision()
    {
        await using var f = await Ready(postgres); var request = await Submit(f, await Create(f));
        var approved = await Decide(f, request, "Approve"); Assert.Equal(200, approved.Status);
        await using var db = f.CreateContext();
        var completed = JsonSerializer.Serialize(await db.Set<AdvanceApprovalStep>().SingleAsync(x => x.RevisionId == request.RevisionId && x.StepOrder == 1));
        await db.Set<GrantProposal>().Where(x => x.RecipientId == RecipientId && x.ProjectId == ProjectId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.EndsAtUtc, f.Clock.GetUtcNow()));
        Assert.True(await Reevaluate(f, request.Id));
        Assert.Equal(completed, JsonSerializer.Serialize(await db.Set<AdvanceApprovalStep>().AsNoTracking().SingleAsync(x => x.RevisionId == request.RevisionId && x.StepOrder == 1)));
        await db.Set<GrantProposal>().Where(x => x.RecipientId == RecipientId && x.ProjectId == ProjectId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.EndsAtUtc, (DateTimeOffset?)null));
        Assert.True(await Reevaluate(f, request.Id));
        var version = (await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync()).Version;
        var returned = await Decide(f, approved.Value! with { Version = version }, "Return", RecipientId);
        var revised = await Command(f, returned.Value!, "ReviseAsync");
        db.Add(new AdvanceRequestParticipant
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            RequestId = request.Id,
            PersonId = ReviewerPersonId,
            Role = "Checker",
            CreatedBy = AuthorId,
            CreatedAtUtc = f.Clock.GetUtcNow()
        });
        await db.SaveChangesAsync();
        var newSubmission = await Submit(f, revised.Value!);
        Assert.Equal("Held", newSubmission.State);
        Assert.Equal(403, (await Decide(f, newSubmission, "Approve")).Status);
        Assert.Equal(completed, JsonSerializer.Serialize(await db.Set<AdvanceApprovalStep>().AsNoTracking().SingleAsync(x => x.RevisionId == request.RevisionId && x.StepOrder == 1)));
    }

    internal static async Task<FieldAdvanceFixture> Ready(PostgresFixture postgres)
    {
        var f = await P.Ready(postgres);
        await P.Review(f, await P.Submit(f, await P.Create(f)));
        await using var db = f.CreateContext();
        await db.Set<GrantProposal>().Where(x => x.RecipientId == AuthorId && x.ProjectId == ProjectId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "request.create" }));
        await db.Set<GrantProposal>().Where(x => x.RecipientId == ReviewerId && x.ProjectId == ProjectId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "request.approve", "report.read" })
                .SetProperty(p => p.ApprovalLevels, new[] { "supervisor", "project-manager" }).SetProperty(p => p.ApprovalLimit, 1000m));
        await db.Set<GrantProposal>().Where(x => x.RecipientId == RecipientId && x.ProjectId == ProjectId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "request.delegate", "request.approve", "report.read" })
                .SetProperty(p => p.ApprovalLevels, new[] { "project-manager" }).SetProperty(p => p.PrimaryApprovalLevels, new[] { "project-manager" })
                .SetProperty(p => p.ApprovalLimit, 1000m));
        db.Add(new ReportingLine
        {
            Id = Guid.NewGuid(),
            EmployeeMembershipId = (await db.Set<EmployeeMembership>().SingleAsync(x => x.UserId == AuthorId)).Id,
            EmployeeUserId = AuthorId,
            SupervisorUserId = ReviewerId,
            ValidFromUtc = f.Clock.GetUtcNow().AddDays(-1),
            CreatedBy = AuthorId,
            Reason = "ทดสอบ"
        });
        await db.SaveChangesAsync(); return f;
    }
    internal static async Task<RequestView> Create(FieldAdvanceFixture f, Guid? actor = null)
    {
        var who = actor ?? AuthorId; var session = await f.SessionAsync(who); await using var db = f.CreateContext();
        var input = new CreateRequest(new(WorkspaceId, ProjectId), AuthorPersonId,
            new("Individual", "สำรวจพื้นที่", new(2026, 10, 24), new(2026, 10, 25), new(2026, 10, 23), [],
                [new(Guid.NewGuid(), Guid.Parse("78000000-0000-0000-0000-000000000001"), "ค่าเดินทาง", "QuantityRate", "1.2345", null, null)]));
        var result = await Draft(f, db, session).CreateAsync(who, input, Meta(0), default);
        Assert.Equal(201, result.Status); return result.Value!;
    }
    internal static RequestDraftService Draft(FieldAdvanceFixture f, Tpr10DbContext db, TPR10.Api.Identity.Sessions.RequestSession session, IAuditEventWriter? audit = null)
    {
        var access = FieldAdvanceAccessTests.Access(db, session, f.Clock); var people = new FinancePersonResolver(db, f.Clock);
        return new(db, access, people, new(db, access, people, f.Clock), new(db, access, session, f.Clock),
            audit ?? new AuditEventWriter(db, new CorrelationContext(), f.Clock), f.Clock);
    }
    internal static RequestSubmissionService Submission(FieldAdvanceFixture f, Tpr10DbContext db, TPR10.Api.Identity.Sessions.RequestSession session)
    {
        var access = FieldAdvanceAccessTests.Access(db, session, f.Clock); var people = new FinancePersonResolver(db, f.Clock);
        return new(db, access, people, new(db, access, people, f.Clock), Draft(f, db, session), new(db, people, f.Clock),
            new AuditEventWriter(db, new CorrelationContext(), f.Clock), f.Clock);
    }
    internal static async Task<RequestView> Submit(FieldAdvanceFixture f, RequestView draft)
    {
        var session = await f.SessionAsync(AuthorId); await using var db = f.CreateContext(); var service = Submission(f, db, session);
        var quote = await service.QuoteAsync(AuthorId, draft.Id, default); Assert.Equal(200, quote.Status);
        var result = await service.ConfirmAsync(AuthorId, draft.Id, quote.Value!.ConfirmationHash, Meta(draft.Version), default);
        Assert.Equal(200, result.Status); return result.Value!;
    }
    // ใช้ reflection เฉพาะช่วง RED เพื่อให้ข้อผิดพลาดเป็น assertion ว่ายังไม่มี service ไม่ใช่ compiler error
    internal static async Task<object> Service(FieldAdvanceFixture f, Tpr10DbContext db, Guid actor, string name, IAuditEventWriter? audit = null)
    {
        var type = typeof(RequestDraftService).Assembly.GetType("TPR10.Api.FieldAdvances.Requests." + name); Assert.NotNull(type);
        var session = await f.SessionAsync(actor); var access = FieldAdvanceAccessTests.Access(db, session, f.Clock); var people = new FinancePersonResolver(db, f.Clock);
        var services = new ServiceCollection().AddSingleton(db).AddSingleton(access).AddSingleton(people)
            .AddSingleton(new RequestAccess(db, access, people, f.Clock)).AddSingleton(Draft(f, db, session, audit))
            .AddSingleton(new ApprovalRouteResolver(db, people, f.Clock)).AddSingleton<TimeProvider>(f.Clock)
            .AddSingleton<IAuditEventWriter>(audit ?? new AuditEventWriter(db, new CorrelationContext(), f.Clock));
        using var provider = services.BuildServiceProvider(); return ActivatorUtilities.CreateInstance(provider, type);
    }
    internal static async Task<FinanceResult<RequestView>> Command(FieldAdvanceFixture f, RequestView view, string method, Guid? actor = null, RequestCommand? meta = null)
    {
        await using var db = f.CreateContext(); var who = actor ?? AuthorId;
        var service = await Service(f, db, who, "RequestRevisionService");
        return await Invoke(service, method, who, view.Id, meta ?? Meta(view.Version), CancellationToken.None);
    }
    internal static Task<FinanceResult<RequestView>> Invoke(object service, string method, params object[] args)
        => (Task<FinanceResult<RequestView>>)service.GetType().GetMethod(method)!.Invoke(service, args)!;
    internal static async Task<FinanceResult<RequestView>> Decide(FieldAdvanceFixture f, RequestView view, string outcome, Guid? actor = null, RequestCommand? meta = null)
    {
        await using var db = f.CreateContext(); var who = actor ?? ReviewerId;
        var step = await db.Set<AdvanceApprovalStep>().Where(x => x.RevisionId == view.RevisionId && x.Outcome == null).OrderBy(x => x.StepOrder).FirstAsync();
        var service = await Service(f, db, who, "RequestReviewService");
        return await Invoke(service, "DecideAsync", who, view.Id, new RequestDecision(outcome, step.Id), meta ?? Meta(view.Version), CancellationToken.None);
    }
    internal static async Task<bool> Reevaluate(FieldAdvanceFixture f, Guid id)
    {
        await using var db = f.CreateContext(); var service = await Service(f, db, AuthorId, "RequestReviewService");
        var method = service.GetType().GetMethod("ReevaluateAsync", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        return await (Task<bool>)method.Invoke(service, [id, CancellationToken.None])!;
    }
    internal static async Task ExpireReviewer(FieldAdvanceFixture f)
    {
        await using var db = f.CreateContext();
        await db.Set<GrantProposal>().Where(x => x.RecipientId == ReviewerId && x.ProjectId == ProjectId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.EndsAtUtc, f.Clock.GetUtcNow()));
    }
    internal static async Task<string> Frozen(FieldAdvanceFixture f, Guid revision)
    {
        await using var db = f.CreateContext(); var row = await db.Set<AdvanceRequestRevision>().SingleAsync(x => x.Id == revision);
        row.IsCurrent = false;
        return JsonSerializer.Serialize(new { row, lines = await db.Set<AdvanceRequestLine>().Where(x => x.RevisionId == revision).OrderBy(x => x.LineOrder).ToArrayAsync() });
    }
    internal static async Task<string> Snapshot(FieldAdvanceFixture f)
    {
        await using var db = f.CreateContext();
        return JsonSerializer.Serialize(new
        {
            requests = await db.Set<AdvanceRequest>().OrderBy(x => x.Id).ToArrayAsync(),
            steps = await db.Set<AdvanceApprovalStep>().OrderBy(x => x.Id).ToArrayAsync(),
            receipts = await db.Set<FinanceCommandReceipt>().CountAsync(),
            events = await db.Set<AdvanceRequestEvent>().CountAsync(),
            audit = await db.AuditEvents.CountAsync(),
            metadata = await db.AuditMetadata.CountAsync()
        });
    }
}
