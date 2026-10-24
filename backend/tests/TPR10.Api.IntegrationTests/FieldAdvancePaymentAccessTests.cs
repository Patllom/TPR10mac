using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Requests;
using TPR10.Api.FieldAdvances.Payments;
using TPR10.Api.Identity.Sessions;
using Npgsql;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvancePaymentAccessTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("check", "finance.check")]
    [InlineData("reconcile", "finance.check")]
    [InlineData("terminate-propose", "finance.check")]
    [InlineData("prepare", "payment.record")]
    [InlineData("start", "payment.record")]
    [InlineData("record", "payment.record")]
    [InlineData("takeover", "payment.record")]
    public async Task Root_approval_history_blocks_payment_actions_on_supplement(string action, string capability)
    {
        await using var f = await CreateAsync(postgres); await f.SeedPoliciesAsync();
        await Grant(f, ReviewerId, ReviewerPersonId, capability);
        var (root, target, _, _) = await Requests(f);
        await AddHistory(f, root, ReviewerPersonId, "Approver");
        Assert.False((await PaymentCheck(f, target, ReviewerId, action)).Allowed);
    }

    [Theory]
    [InlineData("finance.check", "check", true)]
    [InlineData("finance.check", "prepare", false)]
    [InlineData("payment.record", "prepare", true)]
    [InlineData("payment.record", "check", false)]
    [InlineData("report.read", "read", true)]
    [InlineData("report.read", "prepare", false)]
    [InlineData("report.read", "check", false)]
    [InlineData("request.approve", "read", false)]
    [InlineData("payment.record", "unknown", false)]
    public async Task Current_actor_needs_exact_action_grant(string capability, string action, bool allowed)
    {
        await using var f = await CreateAsync(postgres); await f.SeedPoliciesAsync();
        await Grant(f, RecipientId, RecipientPersonId, capability);
        var (_, target, _, _) = await Requests(f);
        Assert.Equal(allowed, (await PaymentCheck(f, target, RecipientId, action)).Allowed);
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("Delegate")]
    [InlineData("Approver")]
    public async Task Alias_with_own_grant_cannot_escape_person_history(string role)
    {
        await using var f = await CreateAsync(postgres); await f.SeedPoliciesAsync();
        await Alias(f, RecipientPersonId);
        await Grant(f, AliasAccountId, RecipientPersonId, "payment.record");
        var (root, target, _, _) = await Requests(f);
        Assert.True((await PaymentCheck(f, target, AliasAccountId, "prepare")).Allowed);
        await AddHistory(f, root, RecipientPersonId, role);
        Assert.False((await PaymentCheck(f, target, AliasAccountId, "prepare")).Allowed);
    }

    [Fact]
    public async Task Alias_cannot_borrow_other_accounts_grants_or_other_scope()
    {
        await using var f = await CreateAsync(postgres); await f.SeedPoliciesAsync();
        await Alias(f, RecipientPersonId);
        await Grant(f, RecipientId, RecipientPersonId, "payment.record");
        await Grant(f, AliasAccountId, RecipientPersonId, "payment.record", Guid.Parse("71000000-0000-0000-0000-000000000012"));
        var (_, target, _, _) = await Requests(f);
        Assert.True((await PaymentCheck(f, target, RecipientId, "prepare")).Allowed);
        Assert.False((await PaymentCheck(f, target, AliasAccountId, "prepare")).Allowed);
    }

    [Fact]
    public async Task Preparing_and_returned_checking_record_immediately_and_survive_regrant()
    {
        await using var f = await CreateAsync(postgres); await f.SeedPoliciesAsync();
        await Alias(f, RecipientPersonId);
        var grant = await Grant(f, AliasAccountId, RecipientPersonId, "payment.record");
        await Grant(f, RecipientId, RecipientPersonId, "finance.check");
        var (root, target, _, _) = await Requests(f);
        Assert.True(await Participate(f, root, AliasAccountId, "prepare", "Payer"));
        Assert.False((await PaymentCheck(f, target, RecipientId, "check")).Allowed);
        await using (var db = f.CreateContext())
        {
            var record = await db.Set<AdvancePaymentParticipation>().SingleAsync();
            Assert.Equal(AliasAccountId, record.CreatedBy); Assert.Equal(RecipientPersonId, record.PersonId);
            var assignment = await db.Set<GrantAssignment>().SingleAsync(x => x.Id == grant);
            assignment.RevokedAtUtc = f.Clock.GetUtcNow(); assignment.RevokedBy = AuthorId;
            assignment.RevocationReason = "ถอนจำลอง"; assignment.Version++;
            await db.SaveChangesAsync();
        }
        Assert.False((await PaymentCheck(f, target, AliasAccountId, "prepare")).Allowed);
        await Grant(f, AliasAccountId, RecipientPersonId, "payment.record");
        Assert.True(await Participate(f, target, AliasAccountId, "prepare", "Payer"));
        Assert.False((await PaymentCheck(f, target, RecipientId, "check")).Allowed);
        await using var verify = f.CreateContext();
        Assert.Single(await verify.Set<AdvancePaymentParticipation>().ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Opposite_roles_compete_atomically_across_requests(bool checkerFirst)
    {
        await using var f = await CreateAsync(postgres); await f.SeedPoliciesAsync();
        await Grant(f, RecipientId, RecipientPersonId, "payment.record");
        await Grant(f, RecipientId, RecipientPersonId, "finance.check");
        var (root, target, _, _) = await Requests(f);
        var session = await f.SessionAsync(RecipientId);
        await using var first = f.CreateContext(); await using var second = f.CreateContext();
        await second.Database.OpenConnectionAsync();
        var competingPid = ((NpgsqlConnection)second.Database.GetDbConnection()).ProcessID;
        await using var tx = await first.Database.BeginTransactionAsync();
        var firstAction = checkerFirst ? "check" : "prepare";
        var secondAction = checkerFirst ? "prepare" : "check";
        Assert.True((await PaymentCall(f, first, session, RecipientId, root, firstAction)).Allowed);
        var competing = Task.Run(async () =>
        {
            await using var otherTx = await second.Database.BeginTransactionAsync();
            var decision = await PaymentCall(f, second, session, RecipientId, target, secondAction);
            if (decision.Allowed) await Record(f, second, session, target, decision.PersonId!.Value, checkerFirst ? "Payer" : "Checker");
            await otherTx.CommitAsync(); return decision.Allowed;
        });
        await WaitForBlocked(first, competingPid, competing);
        await Record(f, first, session, root, RecipientPersonId, checkerFirst ? "Checker" : "Payer");
        await tx.CommitAsync();
        Assert.False(await competing.WaitAsync(TimeSpan.FromSeconds(15)));
        await using var verify = f.CreateContext();
        var retained = Assert.Single(await verify.Set<AdvancePaymentParticipation>().ToArrayAsync());
        Assert.Equal(checkerFirst ? "Checker" : "Payer", retained.Role);
        Assert.False((await PaymentCheck(f, target, RecipientId, secondAction)).Allowed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Approval_and_payment_role_compete_in_the_same_lock_domain(bool approveFirst)
    {
        await using var f = await FieldAdvanceRequestLifecycleTests.Ready(postgres);
        var view = await FieldAdvanceRequestLifecycleTests.Submit(f, await FieldAdvanceRequestLifecycleTests.Create(f));
        await Grant(f, ReviewerId, ReviewerPersonId, "finance.check");
        await using var a = f.CreateContext(); await using var b = f.CreateContext();
        await a.Database.OpenConnectionAsync(); await b.Database.OpenConnectionAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var audit = new FieldAdvanceRequestRaceTests.CallbackAudit(new AuditEventWriter(a, new CorrelationContext(), f.Clock), async () =>
        { if (approveFirst) { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(15)); } });
        var service = (RequestReviewService)await FieldAdvanceRequestLifecycleTests.Service(f, a, ReviewerId, "RequestReviewService", audit);
        var session = await f.SessionAsync(ReviewerId);
        var step = await a.Set<AdvanceApprovalStep>().Where(x => x.RequestId == view.Id).OrderBy(x => x.StepOrder).FirstAsync();
        Task<FinanceResult<RequestView>> Approve() => service.DecideAsync(ReviewerId, view.Id, new("Approve", step.Id),
            new(Guid.NewGuid(), view.Version, "แข่งหน้าที่"), default);
        async Task<bool> Check()
        {
            await using var tx = await b.Database.BeginTransactionAsync();
            var result = await PaymentCall(f, b, session, ReviewerId, view.Id, "check");
            if (result.Allowed) await Record(f, b, session, view.Id, ReviewerPersonId, "Checker");
            if (!approveFirst) { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
            await tx.CommitAsync(); return result.Allowed;
        }
        Task<FinanceResult<RequestView>> approval; Task<bool> check;
        if (approveFirst) { approval = Approve(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); check = Check(); }
        else { check = Check(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); approval = Approve(); }
        try
        {
            await using var probe = f.CreateContext();
            await WaitForBlocked(probe, ((NpgsqlConnection)(approveFirst ? b : a).Database.GetDbConnection()).ProcessID,
                approveFirst ? check : approval);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(approveFirst ? 200 : 403, (await approval).Status);
        Assert.Equal(!approveFirst, await check);
        await using var verify = f.CreateContext();
        var role = Assert.Single(await verify.Set<AdvancePaymentParticipation>().Where(x => x.PersonId == ReviewerPersonId).ToArrayAsync());
        Assert.Equal(approveFirst ? "Approver" : "Checker", role.Role);
    }

    private static async Task WaitForBlocked(Tpr10DbContext probe, int pid, Task pending)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await probe.Database.SqlQuery<int>($"SELECT cardinality(pg_blocking_pids({pid})) AS \"Value\"").SingleAsync(deadline.Token) == 0)
        {
            Assert.False(pending.IsCompleted, "คำสั่งคู่แข่งต้องรอ lock จริง");
            await Task.Delay(20, deadline.Token);
        }
    }

    [Fact]
    public async Task Server_approval_steps_without_participant_rows_are_retained_across_family()
    {
        await using var f = await CreateAsync(postgres); await f.SeedPoliciesAsync();
        var payments = new FieldAdvancePaymentFixture(f);
        var root = await payments.SeedApprovedAsync(100m, default);
        await Grant(f, ReviewerId, ReviewerPersonId, "finance.check");
        var (_, target, _, _) = await Requests(f, existingRoot: root);
        Assert.False((await PaymentCheck(f, target, ReviewerId, "check")).Allowed);
    }

    [Fact]
    public async Task Read_and_evidence_access_do_not_follow_family_membership_or_admin_status()
    {
        await using var f = await CreateAsync(postgres); await f.SeedPoliciesAsync();
        var (root, target, _, _) = await Requests(f);
        foreach (var actor in new[] { AuthorId, RecipientId, ReviewerId })
            foreach (var action in new[] { "read", "evidence-read" })
                Assert.False((await PaymentCheck(f, target, actor, action)).Allowed);
        await Grant(f, ReviewerId, ReviewerPersonId, "request.approve");
        Assert.True((await PaymentCheck(f, target, ReviewerId, "read")).Allowed);
        Assert.False((await PaymentCheck(f, root, ReviewerId, "read")).Allowed);
        Assert.False((await PaymentCheck(f, root, ReviewerId, "evidence-read")).Allowed);
        await Grant(f, AuthorId, AuthorPersonId, "request.create");
        Assert.True((await PaymentCheck(f, target, AuthorId, "read")).Allowed);
        await Grant(f, AuthorId, AuthorPersonId, "payment.record");
        Assert.False((await PaymentCheck(f, target, AuthorId, "prepare")).Allowed);
    }

    [Fact]
    public async Task Returned_checker_payment_history_blocks_approval_and_invalid_record_context_is_rejected()
    {
        await using var f = await CreateAsync(postgres); await f.SeedPoliciesAsync();
        await Grant(f, ReviewerId, ReviewerPersonId, "finance.check");
        await Grant(f, ReviewerId, ReviewerPersonId, "request.approve");
        var (root, target, _, _) = await Requests(f);
        Assert.True(await Participate(f, root, ReviewerId, "check", "Checker"));
        var session = await f.SessionAsync(ReviewerId); await using var db = f.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        var access = FieldAdvanceAccessTests.Access(db, session, f.Clock);
        Assert.False((await new RequestAccess(db, access, new(db, f.Clock), f.Clock).CheckAsync(ReviewerId, target, "review", default)).Allowed);
        var participation = new FamilyParticipation(db, session, access, new(db, f.Clock), f.Clock);
        var family = await db.Set<AdvanceRequest>().Where(x => x.Id == root).Select(x => x.FamilyId).SingleAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => participation.RecordAsync(Guid.NewGuid(), target, ReviewerPersonId, "Checker", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => participation.RecordAsync(family, target, RecipientPersonId, "Checker", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => participation.RecordAsync(family, target, ReviewerPersonId, "Unknown", default));
        Assert.Single(await db.Set<AdvancePaymentParticipation>().ToArrayAsync());
    }

    [Fact]
    public async Task Failed_review_rolls_back_family_role_version_and_decision_and_clears_tracked_state()
    {
        await using var f = await FieldAdvanceRequestLifecycleTests.Ready(postgres);
        var view = await FieldAdvanceRequestLifecycleTests.Submit(f, await FieldAdvanceRequestLifecycleTests.Create(f));
        await using var db = f.CreateContext();
        var familyId = await db.Set<AdvanceRequest>().Where(x => x.Id == view.Id).Select(x => x.FamilyId).SingleAsync();
        var version = await db.Set<AdvanceFamily>().Where(x => x.Id == familyId).Select(x => x.Version).SingleAsync();
        var step = await db.Set<AdvanceApprovalStep>().AsNoTracking().Where(x => x.RequestId == view.Id).OrderBy(x => x.StepOrder).FirstAsync();
        var audit = new FieldAdvanceRequestRaceTests.CallbackAudit(new AuditEventWriter(db, new CorrelationContext(), f.Clock),
            () => throw new InvalidOperationException("ทดสอบ audit ล้มเหลว"));
        var review = (RequestReviewService)await FieldAdvanceRequestLifecycleTests.Service(f, db, ReviewerId, "RequestReviewService", audit);
        await Assert.ThrowsAsync<InvalidOperationException>(() => review.DecideAsync(ReviewerId, view.Id,
            new("Approve", step.Id), new(Guid.NewGuid(), view.Version, "จำลอง rollback"), default));
        Assert.Empty(db.ChangeTracker.Entries());
        await using var verify = f.CreateContext();
        Assert.Empty(await verify.Set<AdvancePaymentParticipation>().ToArrayAsync());
        Assert.Equal(version, await verify.Set<AdvanceFamily>().Where(x => x.Id == familyId).Select(x => x.Version).SingleAsync());
        Assert.Null((await verify.Set<AdvanceApprovalStep>().SingleAsync(x => x.Id == step.Id)).Outcome);
    }

    [Fact]
    public async Task Participation_requires_transaction_and_rolls_back_with_caller()
    {
        await using var f = await CreateAsync(postgres); await f.SeedPoliciesAsync();
        await Grant(f, RecipientId, RecipientPersonId, "payment.record");
        var (root, _, _, _) = await Requests(f); var session = await f.SessionAsync(RecipientId);
        await using var db = f.CreateContext();
        Assert.False((await PaymentCall(f, db, session, RecipientId, root, "prepare")).Allowed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Record(f, db, session, root, RecipientPersonId, "Payer"));
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await Record(f, db, session, root, RecipientPersonId, "Payer");
            Assert.Single(await db.Set<AdvancePaymentParticipation>().AsNoTracking().ToArrayAsync());
            await tx.RollbackAsync();
        }
        await using var verify = f.CreateContext();
        Assert.Empty(await verify.Set<AdvancePaymentParticipation>().ToArrayAsync());
    }

    private static async Task Alias(FieldAdvanceFixture f, Guid person)
    {
        await using var db = f.CreateContext();
        db.Add(new FinanceAccountLink
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            UserId = AliasAccountId,
            PersonId = person,
            CreatedBy = AuthorId,
            CreatedAtUtc = f.Clock.GetUtcNow().AddMinutes(-1),
            Reason = "alias จำลอง"
        });
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("insufficient-frozen-basis", false)]
    [InlineData("former-final-role", false)]
    [InlineData("nonfinal-level", false)]
    [InlineData("checker-history", false)]
    [InlineData("expired", false)]
    [InlineData("unapproved", false)]
    [InlineData("new-draft", true)]
    public async Task Termination_confirmation_requires_current_final_role_and_frozen_basis(string scenario, bool allowed)
    {
        await using var f = await CreateAsync(postgres); await f.SeedPoliciesAsync();
        await Grant(f, ReviewerId, ReviewerPersonId, "request.approve", limit: scenario == "insufficient-frozen-basis" ? 100m : 300m);
        var (root, target, revisionId, stepId) = await Requests(f, basis: 300m);
        await using (var db = f.CreateContext())
        {
            var grant = await db.Set<GrantProposal>().SingleAsync(x => x.RecipientId == ReviewerId);
            grant.ApprovalLevels = scenario == "nonfinal-level" ? ["supervisor"] : ["director"];
            grant.PrimaryApprovalLevels = scenario is "former-final-role" or "nonfinal-level" ? [] : ["director"];
            if (scenario == "expired") grant.EndsAtUtc = f.Clock.GetUtcNow();
            var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == target);
            request.State = "Approved";
            var step = await db.Set<AdvanceApprovalStep>().SingleAsync(x => x.Id == stepId);
            step.Outcome = "Approved"; step.DecidedBy = ReviewerId; step.DecidedByPersonId = ReviewerPersonId;
            step.DecidedAtUtc = f.Clock.GetUtcNow(); step.DecisionReason = "อนุมัติจำลอง";
            var account = await db.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == target);
            if (scenario != "unapproved") { account.ApprovedRevisionId = revisionId; account.Approved = 100m; account.Spendable = 100m; }
            await db.SaveChangesAsync();
            if (scenario == "new-draft")
            {
                var previous = await db.Set<AdvanceRequestRevision>().SingleAsync(x => x.Id == revisionId);
                previous.IsCurrent = false; await db.SaveChangesAsync();
                db.Add(new AdvanceRequestRevision
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = WorkspaceId,
                    ProjectId = ProjectId,
                    RequestId = target,
                    RevisionNumber = 2,
                    IsCurrent = true,
                    Total = 50m,
                    ApprovalBasisAmount = 50m,
                    ApprovalBasisJson = "{\"Amount\":50,\"Items\":[]}",
                    ContentJson = "{}",
                    ContentHash = new('c', 64),
                    CreatedBy = AuthorId,
                    CreatedByPersonId = AuthorPersonId
                });
                await db.SaveChangesAsync();
            }
        }
        if (scenario == "checker-history") await AddHistory(f, root, ReviewerPersonId, "Checker");
        Assert.Equal(allowed, (await PaymentCheck(f, target, ReviewerId, "terminate-confirm")).Allowed);
        // This separate action must never relax normal approval's one-step-per-revision rule.
        Assert.False((await PaymentCheck(f, target, RecipientId, "terminate-confirm")).Allowed);
    }

    [Theory]
    [InlineData("quote")]
    [InlineData("review")]
    [InlineData("worker")]
    public async Task Shared_request_callers_wait_for_family_before_locking_request(string caller)
    {
        await using var f = await FieldAdvanceRequestLifecycleTests.Ready(postgres);
        var draft = await FieldAdvanceRequestLifecycleTests.Create(f);
        var view = caller == "quote" ? draft : await FieldAdvanceRequestLifecycleTests.Submit(f, draft);
        await using var blocker = f.CreateContext(); await using var blockerTx = await blocker.Database.BeginTransactionAsync();
        var familyId = await blocker.Set<AdvanceRequest>().Where(x => x.Id == view.Id).Select(x => x.FamilyId).SingleAsync();
        await blocker.Set<AdvanceFamily>().FromSqlInterpolated($"SELECT * FROM field_advance_families WHERE id={familyId} FOR UPDATE").SingleAsync();
        await using var worker = f.CreateContext(); await worker.Database.OpenConnectionAsync();
        var pid = ((NpgsqlConnection)worker.Database.GetDbConnection()).ProcessID;
        var session = await f.SessionAsync(AuthorId);
        var review = caller == "quote" ? null : (RequestReviewService)await FieldAdvanceRequestLifecycleTests.Service(f, worker, ReviewerId, "RequestReviewService");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        async Task Run()
        {
            if (caller == "quote") Assert.Equal(200, (await FieldAdvanceRequestLifecycleTests.Submission(f, worker, session).QuoteAsync(AuthorId, view.Id, timeout.Token)).Status);
            else if (caller == "worker")
                await (Task<bool>)typeof(RequestReviewService).GetMethod("ReevaluateAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(review, [view.Id, timeout.Token])!;
            else
            {
                var step = await worker.Set<AdvanceApprovalStep>().Where(x => x.RequestId == view.Id).OrderBy(x => x.StepOrder).FirstAsync();
                Assert.Equal(200, (await review!.DecideAsync(ReviewerId, view.Id, new("Approve", step.Id), new(Guid.NewGuid(), view.Version, "ทดสอบลำดับล็อก"), timeout.Token)).Status);
            }
        }
        var pending = Run();
        var blocked = false;
        for (var i = 0; i < 100 && !pending.IsCompleted; i++)
        {
            if (await blocker.Database.SqlQuery<int>($"SELECT cardinality(pg_blocking_pids({pid})) AS \"Value\"").SingleAsync() > 0) { blocked = true; break; }
            await Task.Delay(20);
        }
        Assert.True(blocked, "ผู้เรียกต้องรอ Family lock");
        await using (var probe = f.CreateContext())
        {
            await using var probeTx = await probe.Database.BeginTransactionAsync();
            Assert.Equal(view.Id, (await probe.Set<AdvanceRequest>().FromSqlInterpolated(
                $"SELECT * FROM field_advance_requests WHERE id={view.Id} FOR UPDATE NOWAIT").SingleAsync()).Id);
        }
        await blockerTx.CommitAsync(); await pending;
    }

    [Theory]
    [InlineData("Return")]
    [InlineData("Reject")]
    public async Task Review_decisions_retain_family_role_even_without_approval(string outcome)
    {
        await using var f = await FieldAdvanceRequestLifecycleTests.Ready(postgres);
        var view = await FieldAdvanceRequestLifecycleTests.Submit(f, await FieldAdvanceRequestLifecycleTests.Create(f));
        Assert.Equal(200, (await FieldAdvanceRequestLifecycleTests.Decide(f, view, outcome)).Status);
        await using var db = f.CreateContext();
        var row = Assert.Single(await db.Set<AdvancePaymentParticipation>().Where(x => x.PersonId == ReviewerPersonId).ToArrayAsync());
        Assert.Equal("Approver", row.Role); Assert.Equal(ReviewerId, row.CreatedBy);
        await Grant(f, ReviewerId, ReviewerPersonId, "finance.check");
        Assert.False((await PaymentCheck(f, view.Id, ReviewerId, "check")).Allowed);
    }

    [Fact]
    public async Task Expiry_does_not_erase_returned_checker_history_and_new_grants_do_not_reopen_other_roles()
    {
        await using var f = await CreateAsync(postgres); await f.SeedPoliciesAsync();
        var grant = await Grant(f, RecipientId, RecipientPersonId, "finance.check");
        var (root, target, _, _) = await Requests(f);
        Assert.True(await Participate(f, root, RecipientId, "check", "Checker"));
        await using (var db = f.CreateContext())
        {
            var assignment = await db.Set<GrantAssignment>().SingleAsync(x => x.Id == grant);
            var source = await db.Set<GrantProposal>().SingleAsync(x => x.Id == assignment.ProposalId);
            source.EndsAtUtc = f.Clock.GetUtcNow(); await db.SaveChangesAsync();
        }
        Assert.False((await PaymentCheck(f, target, RecipientId, "check")).Allowed);
        await Grant(f, RecipientId, RecipientPersonId, "payment.record");
        Assert.False((await PaymentCheck(f, target, RecipientId, "prepare")).Allowed);
        await Grant(f, RecipientId, RecipientPersonId, "finance.check");
        Assert.True((await PaymentCheck(f, target, RecipientId, "check")).Allowed);
    }

    private static async Task AddHistory(FieldAdvanceFixture f, Guid request, Guid person, string role)
    {
        await using var db = f.CreateContext();
        db.Add(new AdvanceRequestParticipant
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            RequestId = request,
            PersonId = person,
            Role = role,
            CreatedBy = AuthorId,
            CreatedAtUtc = f.Clock.GetUtcNow()
        });
        await db.SaveChangesAsync();
    }

    private static async Task<PaymentAccessDecision> PaymentCall(FieldAdvanceFixture f, Tpr10DbContext db, RequestSession session,
        Guid actor, Guid request, string action)
    {
        var access = FieldAdvanceAccessTests.Access(db, session, f.Clock); var people = new FinancePersonResolver(db, f.Clock);
        return await new PaymentAccess(db, access, people, new(db, access, people, f.Clock), f.Clock).CheckAsync(actor, request, action, default);
    }

    private static async Task Record(FieldAdvanceFixture f, Tpr10DbContext db, RequestSession session, Guid request, Guid person, string role)
    {
        var family = await db.Set<AdvanceRequest>().Where(x => x.Id == request).Select(x => x.FamilyId).SingleAsync();
        await new FamilyParticipation(db, session, FieldAdvanceAccessTests.Access(db, session, f.Clock), new(db, f.Clock), f.Clock)
            .RecordAsync(family, request, person, role, default);
    }

    private static async Task<PaymentAccessDecision> PaymentCheck(FieldAdvanceFixture f, Guid request, Guid actor, string action)
    {
        var session = await f.SessionAsync(actor); await using var db = f.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        return await PaymentCall(f, db, session, actor, request, action);
    }

    private static async Task<bool> Participate(FieldAdvanceFixture f, Guid request, Guid actor, string action, string role)
    {
        var session = await f.SessionAsync(actor); await using var db = f.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        var decision = await PaymentCall(f, db, session, actor, request, action);
        if (!decision.Allowed) return false;
        await Record(f, db, session, request, decision.PersonId!.Value, role);
        await tx.CommitAsync(); return true;
    }

    // A request-local history reader incorrectly permits the four conflicting roles.
    [Theory]
    [InlineData("Owner", false, false)]
    [InlineData("Delegate", false, false)]
    [InlineData("Checker", false, false)]
    [InlineData("Payer", false, false)]
    [InlineData("Approver", false, true)]
    [InlineData("Checker", true, true)]
    public async Task SamePersonCannotSwitchAcrossFamily(string previousRole, bool copy, bool allowed)
    {
        await using var f = await CreateAsync(postgres);
        await f.SeedPoliciesAsync();
        await Grant(f, ReviewerId, ReviewerPersonId, "request.approve");
        var (root, target, _, _) = await Requests(f, copy);
        await using (var seed = f.CreateContext())
        {
            seed.Add(new AdvanceRequestParticipant
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                ProjectId = ProjectId,
                RequestId = root,
                PersonId = ReviewerPersonId,
                Role = previousRole,
                CreatedBy = ReviewerId,
                CreatedAtUtc = f.Clock.GetUtcNow()
            });
            await seed.SaveChangesAsync();
        }
        var session = await f.SessionAsync(ReviewerId);
        await using var db = f.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        var access = new RequestAccess(db, FieldAdvanceAccessTests.Access(db, session, f.Clock), new(db, f.Clock), f.Clock);
        var decision = await access.CheckAsync(ReviewerId, target, "review", default);
        if (allowed) Assert.True(decision.Allowed);
        else Assert.False(decision.Allowed);
    }

    private static async Task<Guid> Grant(FieldAdvanceFixture f, Guid actor, Guid person, string capability,
        Guid? project = null, decimal limit = 1000m)
    {
        var id = await FieldAdvanceGrantTests.AuthorityAsync(f, actor, person, limit, projectId: project);
        await using var db = f.CreateContext();
        var assignment = await db.Set<GrantAssignment>().SingleAsync(x => x.Id == id);
        var proposal = await db.Set<GrantProposal>().SingleAsync(x => x.Id == assignment.ProposalId);
        proposal.Capabilities = [capability];
        if (capability == "request.approve") proposal.ApprovalLevels = ["supervisor"];
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<(Guid Root, Guid Target, Guid Revision, Guid Step)> Requests(FieldAdvanceFixture f, bool copy = false,
        decimal basis = 100m, Guid? existingRoot = null)
    {
        await using var db = f.CreateContext();
        var root = existingRoot is { } rootId ? await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == rootId) : new AdvanceRequest
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            OwnerPersonId = AuthorPersonId,
            CreatedBy = AuthorId
        };
        if (existingRoot is null) { db.Add(root); await db.SaveChangesAsync(); }
        var target = new AdvanceRequest
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            OwnerPersonId = AuthorPersonId,
            CreatedBy = AuthorId,
            State = "PendingApproval",
            FamilyId = copy ? Guid.NewGuid() : root.FamilyId,
            RelationKind = copy ? "Normal" : "Supplement",
            SourceRequestId = copy ? root.Id : null,
            SupplementOfRequestId = copy ? null : root.Id
        };
        var revision = new AdvanceRequestRevision
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            RequestId = target.Id,
            RevisionNumber = 1,
            IsCurrent = true,
            Total = 100m,
            ApprovalBasisAmount = basis,
            ApprovalBasisJson = JsonSerializer.Serialize(new ApprovalBasis(basis, [])),
            ContentJson = "{}",
            ContentHash = new('a', 64),
            CreatedBy = AuthorId,
            CreatedByPersonId = AuthorPersonId,
            CompanyPolicyRevisionId = CompanyRevisionId,
            PolicySnapshotJson = JsonSerializer.Serialize(new PolicySnapshot(CompanyRevisionId, ProjectRevisionId,
                FieldAdvancePolicyTests.Content(), new('b', 64))
            { Scope = new(WorkspaceId, ProjectId) }),
            PolicyHash = new('b', 64)
        };
        var step = new AdvanceApprovalStep
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            RequestId = target.Id,
            RevisionId = revision.Id,
            StepOrder = 1,
            LevelCode = "supervisor",
            AssigneePersonId = ReviewerPersonId
        };
        db.AddRange(target, revision, step);
        db.Add(new ReportingLine
        {
            Id = Guid.NewGuid(),
            EmployeeMembershipId = (await db.Set<EmployeeMembership>().SingleAsync(x => x.UserId == AuthorId)).Id,
            EmployeeUserId = AuthorId,
            SupervisorUserId = ReviewerId,
            ValidFromUtc = f.Clock.GetUtcNow().AddDays(-1),
            CreatedBy = AuthorId,
            Reason = "เส้นทางอนุมัติจำลอง"
        });
        await db.SaveChangesAsync();
        revision.SubmittedAtUtc = f.Clock.GetUtcNow(); revision.ConfirmedByPersonId = AuthorPersonId;
        await db.SaveChangesAsync();
        return (root.Id, target.Id, revision.Id, step.Id);
    }
}
