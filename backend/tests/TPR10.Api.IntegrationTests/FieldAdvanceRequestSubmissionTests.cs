using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Policies;
using TPR10.Api.FieldAdvances.Requests;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Organization.Data;
using TPR10.Api.Attendance.Directory;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using P = TPR10.Api.IntegrationTests.FieldAdvancePolicyTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceRequestSubmissionTests(PostgresFixture postgres)
{
    // Missing submission boundary/DI registration must fail without a compiler failure.
    [Fact]
    public void Submission_service_is_registered() => Assert.Contains(new ServiceCollection().AddFieldAdvances(),
        x => x.ServiceType.FullName == "TPR10.Api.FieldAdvances.Requests.RequestSubmissionService" && x.Lifetime == ServiceLifetime.Scoped);

    // Knowing the owner's hash must never confer owner authority on the delegate.
    [Fact]
    public async Task Delegate_cannot_confirm()
    {
        await using var f = await Ready();
        var draft = await Create(f, RecipientId);
        Assert.Equal(403, (await Confirm(f, draft, "known-hash")).Status);
        var sent = await Send(f, draft);
        Assert.Equal(200, sent.Status); Assert.Equal("AwaitingOwner", sent.Value!.State);
        var quote = await Quote(f, sent.Value);
        Assert.Equal(200, quote.Status);
        var before = await Snapshot(f);
        var denied = await Confirm(f, sent.Value, quote.Value!.ConfirmationHash, actor: RecipientId);
        Assert.Equal(403, denied.Status); Assert.Null(denied.Value);
        Assert.Equal(before, await Snapshot(f));
        Assert.Equal(200, (await Confirm(f, sent.Value, quote.Value.ConfirmationHash)).Status);
    }

    // A content/version or effective policy change cannot freeze an unseen quote.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Policy_or_content_changed_requires_new_quote(bool policy)
    {
        await using var f = await Ready();
        var draft = await Create(f);
        var quote = (await Quote(f, draft)).Value!;
        if (policy) await ReplacePolicy(f);
        else
        {
            var session = await f.SessionAsync(AuthorId);
            await using var db = f.CreateContext();
            var changed = await Draft(f, db, session).UpdateAsync(AuthorId, draft.Id,
                draft.Content with { Purpose = "งานที่แก้แล้ว" }, Meta(draft.Version), default);
            Assert.Equal(200, changed.Status); draft = changed.Value!;
        }
        var before = await Snapshot(f);
        Assert.Equal(409, (await Confirm(f, draft, quote.ConfirmationHash)).Status);
        Assert.Equal(before, await Snapshot(f));
        await using var check = f.CreateContext();
        var revision = await check.Set<AdvanceRequestRevision>().SingleAsync();
        Assert.Null(revision.PolicySnapshotJson); Assert.Null(revision.SubmittedAtUtc);
        Assert.Empty(await check.Set<AdvanceApprovalStep>().ToArrayAsync());
        var fresh = (await Quote(f, draft)).Value!;
        Assert.NotEqual(quote.ConfirmationHash, fresh.ConfirmationHash);
        Assert.Equal(200, (await Confirm(f, draft, fresh.ConfirmationHash)).Status);
    }

    [Fact]
    public async Task Override_keeps_approved_company_basis()
    {
        await using var f = await Ready();
        var company = await ActiveCompany(f);
        var project = await P.Review(f, await P.Submit(f, await P.Create(f, project: true, basis: company.Id,
            content: P.Content(9), actor: RecipientId), actor: RecipientId));
        var draft = await Create(f);
        await ReplacePolicy(f);
        var quote = (await Quote(f, draft)).Value!;
        Assert.Equal(company.Id, quote.Policy.CompanyVersionId);
        Assert.Equal(project.Id, quote.Policy.ProjectVersionId);
        Assert.Equal(9, quote.Policy.Content.ClearingDays);
        Assert.True(quote.Policy.RequiresReview);
        Assert.Equal(200, (await Confirm(f, draft, quote.ConfirmationHash)).Status);
        await using var db = f.CreateContext();
        var revision = await db.Set<AdvanceRequestRevision>().SingleAsync();
        Assert.Equal(company.Id, revision.CompanyPolicyRevisionId); Assert.Equal(project.Id, revision.ProjectPolicyRevisionId);
    }

    // Omitting a later step, preassigning it or freezing before saving child rows breaks this.
    [Fact]
    public async Task Confirmation_freezes_all_steps()
    {
        await using var f = await Ready();
        var draft = await Create(f);
        var quote = (await Quote(f, draft)).Value!;
        Assert.Equal(new[] { "supervisor", "project-manager", "director" }, quote.Levels);
        var result = await Confirm(f, draft, quote.ConfirmationHash);
        Assert.Equal(200, result.Status); Assert.Equal("Held", result.Value!.State);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.HoldCode));
        Assert.Equal(2, result.Value.Version); Assert.Equal(123.45m, result.Value.Estimate!.Total);
        await using var db = f.CreateContext();
        var revision = await db.Set<AdvanceRequestRevision>().SingleAsync();
        Assert.Equal(quote.Policy.CompanyVersionId, revision.CompanyPolicyRevisionId);
        Assert.Equal(quote.Policy.ProjectVersionId, revision.ProjectPolicyRevisionId);
        Assert.Equal(quote.Policy.ContentHash, revision.PolicyHash);
        Assert.Equal(123.45m, revision.Total); Assert.Equal(AuthorPersonId, revision.ConfirmedByPersonId);
        Assert.Equal(f.Clock.GetUtcNow(), revision.SubmittedAtUtc);
        var frozen = JsonSerializer.Deserialize<PolicySnapshot>(revision.PolicySnapshotJson!)!;
        Assert.Equal(quote.Policy.ContentHash, frozen.ContentHash);
        Assert.Equal(7, frozen.Content.ClearingDays);
        var steps = await db.Set<AdvanceApprovalStep>().OrderBy(x => x.StepOrder).ToArrayAsync();
        Assert.Equal(new[] { 1, 2, 3 }, steps.Select(x => x.StepOrder));
        Assert.Equal(new[] { "supervisor", "project-manager", "director" }, steps.Select(x => x.LevelCode));
        Assert.Equal(result.Value.HoldCode, steps[0].HoldCode);
        Assert.All(steps, x => { Assert.Null(x.Outcome); Assert.Null(x.AssigneePersonId); Assert.Null(x.DecidedAtUtc); });
        Assert.All(steps.Skip(1), x => Assert.Null(x.HoldCode));
        var line = await db.Set<AdvanceRequestLine>().SingleAsync();
        Assert.Equal(123.45m, line.Amount); Assert.Equal(100m, line.UnitRate);
        Assert.Equal(2, await db.Set<AdvanceRequestEvent>().CountAsync());
    }

    [Fact]
    public async Task Replay_after_submission_requires_current_owner_authority()
    {
        await using var f = await Ready();
        var draft = await Create(f); var quote = (await Quote(f, draft)).Value!; var meta = Meta(1);
        var first = await Confirm(f, draft, quote.ConfirmationHash, meta);
        Assert.Equal(200, first.Status);
        var before = await Snapshot(f);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(await Confirm(f, draft, quote.ConfirmationHash, meta)));
        Assert.Equal(before, await Snapshot(f));
        Assert.Equal(409, (await Confirm(f, draft, "different", meta)).Status);
        await using var db = f.CreateContext();
        await db.Set<GrantProposal>().Where(x => x.RecipientId == AuthorId && x.ProjectId == ProjectId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "report.read" }));
        var denied = await Confirm(f, draft, quote.ConfirmationHash, meta);
        Assert.Equal(403, denied.Status); Assert.Null(denied.Value);
        Assert.Equal(before, await Snapshot(f));
    }

    [Fact]
    public async Task Only_current_step_is_assigned_without_candidate_session()
    {
        await using var f = await Ready();
        await Supervisor(f);
        await using var db = f.CreateContext();
        await db.Set<TPR10.Api.Identity.Data.IdentitySession>().Where(x => x.UserId == ReviewerId).ExecuteDeleteAsync();
        var draft = await Create(f); var quote = (await Quote(f, draft)).Value!;
        var result = await Confirm(f, draft, quote.ConfirmationHash);
        Assert.Equal(200, result.Status); Assert.Equal("PendingApproval", result.Value!.State); Assert.Null(result.Value.HoldCode);
        var steps = await db.Set<AdvanceApprovalStep>().OrderBy(x => x.StepOrder).ToArrayAsync();
        Assert.Equal(3, steps.Length); Assert.Equal(ReviewerPersonId, steps[0].AssigneePersonId);
        Assert.All(steps.Skip(1), x => { Assert.Null(x.AssigneePersonId); Assert.Null(x.HoldCode); });
        Assert.All(steps, x => Assert.Null(x.Outcome));
    }

    [Fact]
    public async Task Send_replays_after_confirmation_without_repeating_effects()
    {
        await using var f = await Ready();
        var draft = await Create(f, RecipientId); var meta = Meta(1);
        var sent = await Send(f, draft, meta); Assert.Equal(200, sent.Status);
        var quote = (await Quote(f, sent.Value!)).Value!;
        Assert.Equal(200, (await Confirm(f, sent.Value!, quote.ConfirmationHash)).Status);
        var before = await Snapshot(f);
        Assert.Equal(JsonSerializer.Serialize(sent), JsonSerializer.Serialize(await Send(f, draft, meta)));
        Assert.Equal(before, await Snapshot(f));
        await using var db = f.CreateContext();
        await db.Set<GrantProposal>().Where(x => x.RecipientId == RecipientId && x.ProjectId == ProjectId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "report.read" }));
        Assert.Equal(403, (await Send(f, draft, meta)).Status);
    }

    [Theory]
    [InlineData("audit")]
    [InlineData("event")]
    [InlineData("commit")]
    public async Task Failure_rolls_back_submission_and_context_can_be_reused(string failure)
    {
        await using var f = await Ready();
        await Supervisor(f);
        var draft = await Create(f); var quote = (await Quote(f, draft)).Value!; var meta = Meta(1);
        var session = await f.SessionAsync(AuthorId); await using var db = f.CreateContext();
        var before = await Snapshot(f);
        await db.Database.ExecuteSqlRawAsync(failure switch
        {
            "audit" => """
                CREATE FUNCTION fail_submission() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'test-submission-fault'; END $$;
                CREATE TRIGGER fail_submission BEFORE INSERT ON audit_events
                FOR EACH ROW EXECUTE FUNCTION fail_submission();
                """,
            "event" => """
                CREATE FUNCTION fail_submission() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'test-submission-fault'; END $$;
                CREATE TRIGGER fail_submission BEFORE INSERT ON field_advance_request_events
                FOR EACH ROW EXECUTE FUNCTION fail_submission();
                """,
            _ => """
                CREATE FUNCTION fail_submission() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'test-submission-fault'; END $$;
                CREATE CONSTRAINT TRIGGER fail_submission AFTER INSERT ON field_advance_command_receipts
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fail_submission();
                """
        });
        var service = Service(f, db, session);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => service.ConfirmAsync(AuthorId, draft.Id, quote.ConfirmationHash, meta, default));
        while (error.InnerException is not null) error = error.InnerException;
        Assert.Equal("P0001", Assert.IsType<Npgsql.PostgresException>(error).SqlState);
        Assert.Empty(db.ChangeTracker.Entries()); await db.SaveChangesAsync();
        Assert.Equal(before, await Snapshot(f));
        await db.Database.ExecuteSqlRawAsync("DROP FUNCTION fail_submission() CASCADE");
        Assert.Equal(200, (await service.ConfirmAsync(AuthorId, draft.Id, quote.ConfirmationHash, meta, default)).Status);
        Assert.Equal(3, await db.Set<AdvanceApprovalStep>().CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expiry_during_audit_fails_closed_and_clears_tracker(bool candidate)
    {
        await using var f = await Ready(); await Supervisor(f);
        var draft = await Create(f); var quote = (await Quote(f, draft)).Value!;
        var session = await f.SessionAsync(AuthorId); await using var db = f.CreateContext();
        await db.Set<GrantProposal>().Where(x => x.RecipientId == (candidate ? ReviewerId : AuthorId) && x.ProjectId == ProjectId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.EndsAtUtc, f.Clock.GetUtcNow().AddMinutes(1)));
        var before = await Snapshot(f);
        var audit = new CallbackAudit(new AuditEventWriter(db, new CorrelationContext(), f.Clock), () =>
        { f.Clock.Advance(TimeSpan.FromMinutes(2)); return Task.CompletedTask; });
        var result = await Service(f, db, session, audit).ConfirmAsync(AuthorId, draft.Id, quote.ConfirmationHash, Meta(1), default);
        Assert.Equal(403, result.Status); Assert.Null(result.Value);
        Assert.Empty(db.ChangeTracker.Entries()); await db.SaveChangesAsync(); Assert.Equal(before, await Snapshot(f));
    }

    [Fact]
    public async Task Snapshot_lines_and_step_roles_are_immutable_after_submission()
    {
        await using var f = await Ready(); var draft = await Create(f); var quote = (await Quote(f, draft)).Value!;
        Assert.Equal(200, (await Confirm(f, draft, quote.ConfirmationHash)).Status);
        var before = await Snapshot(f);
        foreach (var sql in new[]
        {
            "UPDATE field_advance_request_revisions SET total=999",
            "UPDATE field_advance_request_revisions SET policy_snapshot_json=jsonb_build_object()",
            "UPDATE field_advance_request_revisions SET content_json=jsonb_build_object()",
            "UPDATE field_advance_request_lines SET amount=999",
            "UPDATE field_advance_approval_steps SET level_code='other'",
            "DELETE FROM field_advance_approval_steps"
        })
        {
            await using var db = f.CreateContext();
            var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
            Assert.Equal("23514", error.SqlState);
        }
        Assert.Equal(before, await Snapshot(f));
        await ReplacePolicy(f);
        await using var check = f.CreateContext();
        var revision = await check.Set<AdvanceRequestRevision>().SingleAsync();
        Assert.Equal(quote.Policy.CompanyVersionId, revision.CompanyPolicyRevisionId);
        Assert.Equal(7, JsonSerializer.Deserialize<PolicySnapshot>(revision.PolicySnapshotJson!)!.Content.ClearingDays);
        Assert.Equal(123.45m, revision.Total);
    }

    [Theory]
    [InlineData("policy", false)]
    [InlineData("policy", true)]
    [InlineData("content", false)]
    [InlineData("content", true)]
    public async Task Competing_change_and_confirmation_serialize_on_identity_lock(string change, bool confirmFirst)
    {
        await using var f = await Ready(); var draft = await Create(f); var quote = (await Quote(f, draft)).Value!;
        PolicyView? pending = null;
        if (change == "policy")
        {
            var company = await ActiveCompany(f);
            pending = await P.Submit(f, await P.Create(f, source: company, active: company, content: P.Content(8)));
        }
        var ownerSession = await f.SessionAsync(AuthorId); var reviewerSession = await f.SessionAsync(ReviewerId);
        await using var confirming = f.CreateContext(); await using var changing = f.CreateContext();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IAuditEventWriter Audit(Tpr10DbContext db, bool block) => new CallbackAudit(new AuditEventWriter(db, new CorrelationContext(), f.Clock), async () =>
        {
            if (block) { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        });
        Task<FinanceResult<RequestView>> ConfirmNow() => Service(f, confirming, ownerSession, Audit(confirming, confirmFirst))
            .ConfirmAsync(AuthorId, draft.Id, quote.ConfirmationHash, Meta(1), default);
        async Task<int> ChangeNow()
        {
            if (change == "policy") return (await P.Service(f, changing, reviewerSession, Audit(changing, !confirmFirst))
                .ReviewAsync(ReviewerId, pending!.Id, "approve", P.Meta(pending), default)).Status;
            return (await Draft(f, changing, ownerSession, Audit(changing, !confirmFirst)).UpdateAsync(AuthorId, draft.Id,
                draft.Content with { Purpose = "แก้ขณะยืนยัน" }, Meta(1), default)).Status;
        }
        Task<FinanceResult<RequestView>> confirmTask; Task<int> changeTask;
        if (confirmFirst)
        {
            confirmTask = ConfirmNow(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); changeTask = ChangeNow();
        }
        else
        {
            changeTask = ChangeNow(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); confirmTask = ConfirmNow();
        }
        try { await WaitForLock(f, confirmFirst ? changeTask : confirmTask); }
        finally { release.TrySetResult(); }
        var confirmed = await confirmTask.WaitAsync(TimeSpan.FromSeconds(15));
        var changed = await changeTask.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(confirmFirst ? 200 : 409, confirmed.Status);
        Assert.Equal(confirmFirst && change == "content" ? 403 : 200, changed);
        await using var db = f.CreateContext(); var revision = await db.Set<AdvanceRequestRevision>().SingleAsync();
        if (confirmFirst)
        {
            Assert.Equal(quote.Policy.CompanyVersionId, revision.CompanyPolicyRevisionId);
            Assert.Equal("สำรวจพื้นที่", JsonSerializer.Deserialize<RequestContent>(revision.ContentJson)!.Purpose);
            Assert.Equal(3, await db.Set<AdvanceApprovalStep>().CountAsync());
        }
        else
        {
            Assert.Null(revision.SubmittedAtUtc); Assert.Null(revision.PolicySnapshotJson);
            Assert.Empty(await db.Set<AdvanceApprovalStep>().ToArrayAsync());
        }
    }

    [Fact]
    public async Task Concurrent_same_confirmation_has_one_effect()
    {
        await using var f = await Ready(); var draft = await Create(f); var quote = (await Quote(f, draft)).Value!; var meta = Meta(1);
        var session = await f.SessionAsync(AuthorId); await using var a = f.CreateContext(); await using var b = f.CreateContext();
        var results = await Task.WhenAll(Service(f, a, session).ConfirmAsync(AuthorId, draft.Id, quote.ConfirmationHash, meta, default),
            Service(f, b, session).ConfirmAsync(AuthorId, draft.Id, quote.ConfirmationHash, meta, default));
        Assert.All(results, x => Assert.Equal(200, x.Status));
        Assert.Equal(JsonSerializer.Serialize(results[0]), JsonSerializer.Serialize(results[1]));
        Assert.Single(await a.Set<FinanceCommandReceipt>().Where(x => x.CommandId == meta.CommandId).ToArrayAsync());
        Assert.Equal(2, await a.Set<AdvanceRequestEvent>().CountAsync()); Assert.Equal(3, await a.Set<AdvanceApprovalStep>().CountAsync());
    }

    private static async Task WaitForLock(FieldAdvanceFixture f, Task waiting)
    {
        await using var probe = f.CreateContext(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await probe.Database.SqlQueryRaw<bool>("""
            SELECT EXISTS(SELECT 1 FROM pg_locks WHERE locktype='advisory' AND objid=7241002 AND NOT granted) AS "Value"
            """).SingleAsync(deadline.Token))
        {
            Assert.False(waiting.IsCompleted); await Task.Delay(20, deadline.Token);
        }
    }
    private sealed class CallbackAudit(IAuditEventWriter inner, Func<Task> after) : IAuditEventWriter
    {
        public Task WriteAsync(string eventType, Guid targetId, IReadOnlyDictionary<string, string> metadata, CancellationToken ct, Guid? actorId = null)
            => inner.WriteAsync(eventType, targetId, metadata, ct, actorId);
        public async Task WriteAsync(SecurityAuditRequest request, CancellationToken ct)
        { await inner.WriteAsync(request, ct); await after(); }
    }
    private static async Task Supervisor(FieldAdvanceFixture f)
    {
        var grant = await FieldAdvanceGrantTests.AuthorityAsync(f, ReviewerId, ReviewerPersonId, limit: 1000m);
        await using var db = f.CreateContext();
        await db.Set<GrantProposal>().Where(x => db.Set<GrantAssignment>().Any(a => a.Id == grant && a.ProposalId == x.Id))
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "request.approve" }).SetProperty(p => p.ApprovalLevels, new[] { "supervisor" }));
        db.Add(new ReportingLine
        {
            Id = Guid.NewGuid(),
            EmployeeMembershipId = (await db.Set<EmployeeMembership>().SingleAsync(x => x.UserId == AuthorId)).Id,
            EmployeeUserId = AuthorId,
            SupervisorUserId = ReviewerId,
            ValidFromUtc = f.Clock.GetUtcNow().AddDays(-1),
            CreatedBy = AuthorId,
            Reason = "test"
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Changed_policy_that_no_longer_covers_total_requires_new_quote_with_409()
    {
        await using var f = await Ready(); var draft = await Create(f); var quote = (await Quote(f, draft)).Value!;
        var company = await ActiveCompany(f);
        var changed = P.Content() with { Bands = [new(1, "director", 100m)] };
        await P.Review(f, await P.Submit(f, await P.Create(f, source: company, active: company, content: changed)));
        var before = await Snapshot(f);
        Assert.Equal(409, (await Confirm(f, draft, quote.ConfirmationHash)).Status);
        Assert.Equal(before, await Snapshot(f));
        var current = await Quote(f, draft);
        Assert.Equal(400, current.Status); Assert.Equal("unsupported-total", current.Code); Assert.Null(current.Value);
    }

    [Fact]
    public async Task Every_required_band_is_frozen_in_order()
    {
        await using var f = await Ready(); var company = await ActiveCompany(f);
        var content = P.Content() with { Bands = [new(1, "director", 100m), new(2, "executive", 1000m)] };
        await P.Review(f, await P.Submit(f, await P.Create(f, source: company, active: company, content: content)));
        var draft = await Create(f); var quote = (await Quote(f, draft)).Value!;
        Assert.Equal(new[] { "supervisor", "project-manager", "director", "executive" }, quote.Levels);
        Assert.Equal(200, (await Confirm(f, draft, quote.ConfirmationHash)).Status);
        await using var db = f.CreateContext();
        Assert.Equal(new[] { "supervisor", "project-manager", "director", "executive" },
            await db.Set<AdvanceApprovalStep>().OrderBy(x => x.StepOrder).Select(x => x.LevelCode).ToArrayAsync());
    }

    [Fact]
    public async Task Owner_alias_can_confirm_but_unrelated_actor_cannot_read_quote()
    {
        await using var f = await Ready(); var draft = await Create(f); var quote = (await Quote(f, draft)).Value!;
        await using var db = f.CreateContext();
        var unrelated = await f.SessionAsync(ReviewerId);
        var denied = await Service(f, db, unrelated).QuoteAsync(ReviewerId, draft.Id, default);
        Assert.Equal(403, denied.Status); Assert.Null(denied.Value);
        db.Add(new FinanceAccountLink
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            UserId = AliasAccountId,
            PersonId = AuthorPersonId,
            CreatedAtUtc = f.Clock.GetUtcNow(),
            CreatedBy = AuthorId,
            Reason = "alias"
        });
        await db.SaveChangesAsync();
        var grant = await FieldAdvanceGrantTests.AuthorityAsync(f, AliasAccountId, AuthorPersonId);
        await db.Set<GrantProposal>().Where(x => db.Set<GrantAssignment>().Any(a => a.Id == grant && a.ProposalId == x.Id))
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "request.create" }));
        var result = await Confirm(f, draft, quote.ConfirmationHash, actor: AliasAccountId);
        Assert.Equal(200, result.Status);
        var revision = await db.Set<AdvanceRequestRevision>().AsNoTracking().SingleAsync();
        Assert.Equal(AuthorPersonId, revision.ConfirmedByPersonId);
        var history = await db.Set<AdvanceRequestEvent>().OrderByDescending(x => x.AggregateVersion).FirstAsync();
        Assert.Equal(AliasAccountId, history.ActorId); Assert.Equal(AuthorPersonId, history.ActorPersonId);
    }

    [Fact]
    public async Task Quote_requires_request_read_but_never_policy_management_rights()
    {
        await using var f = await Ready(); var draft = await Create(f);
        await using var db = f.CreateContext();
        await db.Set<GrantProposal>().Where(x => x.RecipientId == AuthorId && x.ProjectId == null)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "report.read" }));
        var session = await f.SessionAsync(AuthorId);
        var access = FieldAdvanceAccessTests.Access(db, session, f.Clock);
        var management = await new PolicyResolver(db, access, session, f.Clock).ResolveAsync(new(WorkspaceId, ProjectId), f.Clock.GetUtcNow(), default);
        Assert.Equal(403, management.Status);
        var quote = await Quote(f, draft);
        Assert.Equal(200, quote.Status); Assert.Equal(123.45m, quote.Value!.Estimate.Total);
        Assert.Equal(200, (await Confirm(f, draft, quote.Value.ConfirmationHash)).Status);
    }

    private async Task<FieldAdvanceFixture> Ready()
    {
        var f = await P.Ready(postgres);
        await P.Review(f, await P.Submit(f, await P.Create(f)));
        await using var db = f.CreateContext();
        // Keep policy authority on the controller accounts; the request owner gets no management rights.
        await db.Set<GrantProposal>().Where(x => x.RecipientId == AuthorId && x.ProjectId == ProjectId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "request.create" }));
        await db.Set<GrantProposal>().Where(x => x.RecipientId == RecipientId && x.ProjectId == ProjectId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "request.delegate", "policy.edit.project", "policy.review" }));
        return f;
    }
    private static RequestCommand Meta(long version) => new(Guid.NewGuid(), version, "ทดสอบยืนยัน");
    private static async Task<RequestView> Create(FieldAdvanceFixture f, Guid? actor = null)
    {
        var who = actor ?? AuthorId; var session = await f.SessionAsync(who);
        await using var db = f.CreateContext();
        var input = new CreateRequest(new(WorkspaceId, ProjectId), AuthorPersonId,
            new("Individual", "สำรวจพื้นที่", new(2026, 10, 24), new(2026, 10, 25), new(2026, 10, 23), [],
                [new(Guid.NewGuid(), Guid.Parse("78000000-0000-0000-0000-000000000001"), "ค่าเดินทาง", "QuantityRate", "1.2345", null, null)]));
        var result = await Draft(f, db, session).CreateAsync(who, input, Meta(0), default);
        Assert.Equal(201, result.Status); return result.Value!;
    }
    private static RequestDraftService Draft(FieldAdvanceFixture f, Tpr10DbContext db, RequestSession session, IAuditEventWriter? audit = null)
    {
        var access = FieldAdvanceAccessTests.Access(db, session, f.Clock); var people = new FinancePersonResolver(db, f.Clock);
        return new(db, access, people, new(db, access, people, f.Clock), new(db, access, session, f.Clock),
            audit ?? new AuditEventWriter(db, new CorrelationContext(), f.Clock), f.Clock);
    }
    private static RequestSubmissionService Service(FieldAdvanceFixture f, Tpr10DbContext db, RequestSession session, IAuditEventWriter? audit = null)
    {
        var access = FieldAdvanceAccessTests.Access(db, session, f.Clock); var people = new FinancePersonResolver(db, f.Clock);
        return new(db, access, people, new(db, access, people, f.Clock), Draft(f, db, session), new(db, people, f.Clock),
            audit ?? new AuditEventWriter(db, new CorrelationContext(), f.Clock), f.Clock);
    }
    private static async Task<FinanceResult<ConfirmationQuote>> Quote(FieldAdvanceFixture f, RequestView draft)
    {
        var session = await f.SessionAsync(AuthorId); await using var db = f.CreateContext();
        return await Service(f, db, session).QuoteAsync(AuthorId, draft.Id, CancellationToken.None);
    }
    private static async Task<FinanceResult<RequestView>> Confirm(FieldAdvanceFixture f, RequestView draft, string hash,
        RequestCommand? meta = null, Guid? actor = null)
    {
        var who = actor ?? AuthorId; var session = await f.SessionAsync(who); await using var db = f.CreateContext();
        return await Service(f, db, session).ConfirmAsync(who, draft.Id, hash, meta ?? Meta(draft.Version), CancellationToken.None);
    }
    private static async Task<FinanceResult<RequestView>> Send(FieldAdvanceFixture f, RequestView draft, RequestCommand? meta = null)
    {
        var session = await f.SessionAsync(RecipientId); await using var db = f.CreateContext();
        return await Service(f, db, session).SendToOwnerAsync(RecipientId, draft.Id, meta ?? Meta(draft.Version), CancellationToken.None);
    }
    private static async Task<PolicyView> ActiveCompany(FieldAdvanceFixture f)
    {
        await using var db = f.CreateContext();
        var evidence = await (from review in db.Set<PolicyReview>()
                              join active in db.Set<ActivePolicy>() on review.PolicyRevisionId equals active.PolicyRevisionId
                              where active.ProjectId == null
                              select review).SingleAsync();
        return JsonSerializer.Deserialize<PolicyView>(evidence.ReviewedContentJson!)!;
    }
    private static async Task ReplacePolicy(FieldAdvanceFixture f)
    {
        var company = await ActiveCompany(f);
        await P.Review(f, await P.Submit(f, await P.Create(f, source: company, active: company, content: P.Content(8))));
    }
    private static async Task<string> Snapshot(FieldAdvanceFixture f)
    {
        await using var db = f.CreateContext();
        return JsonSerializer.Serialize(new
        {
            requests = await db.Set<AdvanceRequest>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            revisions = await db.Set<AdvanceRequestRevision>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            lines = await db.Set<AdvanceRequestLine>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            steps = await db.Set<AdvanceApprovalStep>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            events = await db.Set<AdvanceRequestEvent>().CountAsync(),
            receipts = await db.Set<FinanceCommandReceipt>().CountAsync(),
            participants = await db.Set<AdvanceRequestParticipant>().CountAsync(),
            audit = await db.AuditEvents.CountAsync(),
            metadata = await db.AuditMetadata.CountAsync()
        });
    }
}
