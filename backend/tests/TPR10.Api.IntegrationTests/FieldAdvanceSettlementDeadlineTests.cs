using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Net.Http.Json;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Policies;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Settlements;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementAccessTests;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementDraftTests;
using static TPR10.Api.IntegrationTests.FieldAdvancePolicyHttpTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceSettlementDeadlineTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Competing_end_corrections_preserve_proposals_and_allow_reconciled_closure()
    {
        await using var f = await ReadyAt();
        await FieldAdvanceSettlementReviewTests.Draft(f); // establish the approved route actors
        var (request, evidence) = await FieldAdvanceRefundTests.RefundReady(f);
        // RefundReady seeds payment facts for refund tests, not a completed payment lifecycle.
        // Match the closure fixture's Paid/Matched/account setup before testing closure.
        await using (var paid = f.CreateContext())
        {
            var account = await paid.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == request);
            account.Approved = account.Spendable = account.Paid = 2000m; account.EverPaid = true;
            (await paid.Set<AdvanceInstallment>().SingleAsync(x => x.RequestId == request)).State = "Paid";
            (await paid.Set<AdvancePaymentAttempt>().SingleAsync(x => x.RequestId == request)).State = "Matched";
            await paid.SaveChangesAsync();
        }
        var refund = await FieldAdvanceRefundTests.RefundCall(f, request, "Propose", input: FieldAdvanceRefundTests.RefundInputFor(2000, evidence));
        Assert.Equal(201, refund.Status);
        Assert.Equal(200, (await FieldAdvanceRefundTests.RefundCall(f, request, "Confirm", refund.Value!.EntityId)).Status);
        Assert.Equal(200, (await FieldAdvanceRefundTests.RefundCall(f, request, "Approve", refund.Value.EntityId, actor: ReviewerId)).Status);
        Assert.Equal(200, (await End(f, request, new(2026, 10, 20))).Status);
        Assert.Equal(200, (await Declare(f, request, true)).Status);
        var a = await Propose(f, request, new(2026, 10, 25));
        var b = await Propose(f, request, new(2026, 10, 26));
        Assert.Equal(200, a.Status); Assert.Equal(200, b.Status);
        await using var db = f.CreateContext();
        var before = await db.Set<WorkCompletion>().AsNoTracking().SingleAsync(x => x.Id == b.Value!.EntityId);
        var closure = FieldAdvanceSettlementDeliverySupport.Closure(f, db);
        Assert.False(await closure.TryCloseAsync(request, default));
        Assert.Equal(200, (await Accept(f, request, a.Value!.EntityId!.Value, AliasAccountId)).Status);
        var cancelled = await db.Set<WorkCompletion>().AsNoTracking().SingleAsync(x => x.Id == before.Id);
        Assert.Equal("Cancelled", cancelled.State); Assert.Null(cancelled.AcceptedAtUtc);
        Assert.Equal(before.Version + 1, cancelled.Version);
        Assert.Equal(before.SnapshotJson, cancelled.SnapshotJson);
        Assert.Equal(before.ProposedByPersonId, cancelled.ProposedByPersonId);
        Assert.Equal(before.Reason, cancelled.Reason);
        Assert.Equal(before.CompletedOn, cancelled.CompletedOn);
        // Recreate a pre-fix stale Pending row (only this unaccepted fixture row).
        // The production API must resolve it; closure itself must keep blocking it.
        var legacy = await db.Set<WorkCompletion>().SingleAsync(x => x.Id == before.Id);
        legacy.State = "Pending"; legacy.ReviewedBy = null; legacy.ReviewedByPersonId = null; legacy.ReviewedAtUtc = null;
        await db.SaveChangesAsync();
        Assert.False(await closure.TryCloseAsync(request, default));
        var resolved = await Accept(f, request, before.Id, AliasAccountId);
        Assert.Equal(200, resolved.Status);
        Assert.Equal("Cancelled", resolved.Value!.State);
        Assert.Equal(before.SnapshotJson, (await db.Set<WorkCompletion>().AsNoTracking().SingleAsync(x => x.Id == before.Id)).SnapshotJson);
        Assert.True(await closure.TryCloseAsync(request, default));
        Assert.False(await closure.TryCloseAsync(request, default));
        Assert.Single(await db.Set<ClosureRecord>().Where(x => x.RequestId == request).ToArrayAsync());
        Assert.Equal(3, await db.Set<WorkCompletion>().CountAsync(x => x.RequestId == request));
    }

    [Fact]
    public async Task Completion_review_shows_original_and_proposed_dates_to_checker()
    {
        await using var f = await ReadyAt();
        var (request, _) = await Draft(f);
        Assert.Equal(200, (await End(f, request, new(2026, 10, 20))).Status);
        Assert.Equal(200, (await Propose(f, request, new(2026, 10, 30))).Status);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var checker = await Client(factory, f, AliasAccountId);
        var path = $"/api/v1/field-advances/settlement-work?workspaceId={WorkspaceId}&projectId={ProjectId}&lane=completion";
        var response = await checker.GetAsync(path);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        var completion = item.GetProperty("detail").GetProperty("completion");
        Assert.Equal("2026-10-20", completion.GetProperty("previousCompletedOn").GetString());
        Assert.Equal("2026-10-30", completion.GetProperty("completedOn").GetString());
    }
    [Fact]
    public void Calendar_days_include_weekends_and_reject_negative_windows()
    {
        Assert.Equal(new DateOnly(2026, 11, 2), SettlementDeadlineCalculator.DueOn(new(2026, 10, 31), 2));
        Assert.Equal(new DateOnly(2026, 10, 31), SettlementDeadlineCalculator.DueOn(new(2026, 10, 31), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SettlementDeadlineCalculator.DueOn(new(2026, 10, 31), -1));
    }

    [Fact]
    public async Task Calendar_deadline_and_return_window_keep_original_submission()
    {
        await using var f = await ReadyAt(); var (request, round) = await Draft(f);
        Assert.Equal(200, (await End(f, request, new(2026, 10, 25))).Status);
        Assert.Equal(new DateOnly(2026, 11, 2), SettlementDeadlineCalculator.DueOn(new(2026, 10, 31), 2));
        await ActivateCorrectionPolicy(f, 2);
        Assert.Equal(200, (await Submit(f, AuthorId, request, round, await ConfirmCommand(f, request, round))).Status);
        await using var db = f.CreateContext();
        var initial = await db.Set<SettlementRevision>().AsNoTracking().SingleAsync(x => x.RoundId == round);
        // 17:00 UTC is the next Bangkok calendar day (Sunday); no business-day skipping.
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(200, (await Review(f, ReviewerId, request, round, "Return")).Status);
        var returned = await db.Set<SettlementRevision>().AsNoTracking().SingleAsync(x => x.Id == initial.Id);
        var step = await db.Set<SettlementApprovalStep>().AsNoTracking().SingleAsync(x => x.State == "Returned");
        Assert.Equal(new DateOnly(2026, 11, 3), returned.CorrectionDueOn);
        await ActivateCorrectionPolicy(f, 20);
        f.Clock.Advance(TimeSpan.FromDays(3));
        var doc = await db.Set<ExpenseDocument>().SingleAsync();
        Assert.Equal(200, (await Save(f, request, round, Input(90, doc.Id))).Status);
        Assert.Equal(200, (await Submit(f, AuthorId, request, round, await ConfirmCommand(f, request, round))).Status);
        var corrected = await db.Set<SettlementRevision>().AsNoTracking().Where(x => x.RoundId == round).OrderByDescending(x => x.RevisionNumber).FirstAsync();
        using var snapshot = JsonDocument.Parse(corrected.SnapshotJson);
        var timing = snapshot.RootElement.GetProperty("Timing");
        var history = new { InitialSubmissionOnTime = timing.GetProperty("InitialSubmissionOnTime").GetBoolean(), CorrectionOnTime = timing.GetProperty("CorrectionOnTime").GetBoolean() };
        Assert.True(history.InitialSubmissionOnTime);
        Assert.False(history.CorrectionOnTime);
        Assert.Equal(initial.SubmittedAtUtc, timing.GetProperty("InitialSubmittedAtUtc").GetDateTimeOffset());
        Assert.Equal(new DateOnly(2026, 11, 3), corrected.CorrectionDueOn);
        Assert.Equal(initial.SubmittedAtUtc, (await db.Set<SettlementRevision>().AsNoTracking().SingleAsync(x => x.Id == initial.Id)).SubmittedAtUtc);
        Assert.Equal(step.SnapshotJson, (await db.Set<SettlementApprovalStep>().AsNoTracking().SingleAsync(x => x.Id == step.Id)).SnapshotJson);
        var submittedSnapshot = corrected.SnapshotJson;
        f.Clock.Advance(TimeSpan.FromDays(5));
        foreach (var actor in new[] { ReviewerId, RecipientId, AliasAccountId }) Assert.Equal(200, (await Review(f, actor, request, round)).Status);
        var accepted = await db.Set<SettlementRevision>().AsNoTracking().SingleAsync(x => x.Id == corrected.Id);
        Assert.Equal(submittedSnapshot, accepted.SnapshotJson);
        Assert.Equal(corrected.SubmittedAtUtc, accepted.SubmittedAtUtc);
        Assert.True(accepted.AcceptedAtUtc > accepted.SubmittedAtUtc);
    }

    [Fact]
    public async Task Correction_has_no_effect_until_separate_checker_accepts_and_preserves_old_lateness()
    {
        await using var f = await ReadyAt(); var (request, round) = await Draft(f);
        var original = await End(f, request, new(2026, 10, 20)); Assert.Equal(200, original.Status);
        Assert.Equal(200, (await Submit(f, AuthorId, request, round, await ConfirmCommand(f, request, round))).Status);
        await using var db = f.CreateContext();
        var before = await db.Set<WorkCompletion>().AsNoTracking().SingleAsync();
        var revisionBefore = await db.Set<SettlementRevision>().AsNoTracking().SingleAsync();
        var proposed = await Propose(f, request, new(2026, 10, 30)); Assert.Equal(200, proposed.Status);
        var id = proposed.Value!.EntityId!.Value;
        var pending = await db.Set<WorkCompletion>().AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Null(pending.AcceptedAtUtc); Assert.Equal(before.Id, pending.SupersedesId);
        Assert.Single(await db.Set<WorkCompletion>().Where(x => x.AcceptedAtUtc != null).ToArrayAsync());
        var declare = await Declare(f, request, true); Assert.Equal(200, declare.Status);
        var declaration = await db.Set<SubmissionDeclaration>().AsNoTracking().SingleAsync();
        Assert.Equal(before.Id, declaration.WorkCompletionId);
        Assert.Equal(403, (await Accept(f, request, id, RecipientId)).Status);
        Assert.Equal(403, (await Accept(f, request, id, AuthorId)).Status);
        f.Clock.Advance(TimeSpan.FromDays(2));
        var command = await Meta(f, request, pending.Version);
        var accepted = await Accept(f, request, id, AliasAccountId, command); Assert.Equal(200, accepted.Status);
        Assert.Equal(accepted, await Accept(f, request, id, AliasAccountId, command));
        var after = await db.Set<WorkCompletion>().AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(pending.CreatedAtUtc, after.CreatedAtUtc);
        Assert.True(after.AcceptedAtUtc > after.CreatedAtUtc);
        Assert.Equal(new DateOnly(2026, 11, 6), after.ClearingDueOn);
        Assert.Equal(after.ClearingDueOn, after.RefundDueOn);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await db.Set<WorkCompletion>().AsNoTracking().SingleAsync(x => x.Id == before.Id)));
        Assert.Equal(revisionBefore.SnapshotJson, (await db.Set<SettlementRevision>().AsNoTracking().SingleAsync()).SnapshotJson);
        using var snap = JsonDocument.Parse(after.SnapshotJson);
        var assessment = snap.RootElement.GetProperty("Assessments").EnumerateArray().Single(x => x.GetProperty("Kind").GetString() == "Submission");
        Assert.False(assessment.GetProperty("PreviousOnTime").GetBoolean()); Assert.True(assessment.GetProperty("NewOnTime").GetBoolean());
        Assert.Equal(declaration.SnapshotJson, (await db.Set<SubmissionDeclaration>().AsNoTracking().SingleAsync()).SnapshotJson);
    }

    [Fact]
    public async Task Declarations_append_withdrawal_require_reason_and_hold_new_expense_until_withdrawn()
    {
        await using var f = await ReadyAt(); var (request, round) = await Draft(f);
        Assert.Equal(200, (await End(f, request, new(2026, 10, 25))).Status);
        Assert.Equal(200, (await Submit(f, AuthorId, request, round, await ConfirmCommand(f, request, round))).Status);
        var command = await Meta(f, request);
        var first = await Declare(f, request, true, command: command); Assert.Equal(200, first.Status);
        Assert.Equal(first, await Declare(f, request, true, command: command));
        Assert.Equal(409, (await Declare(f, request, false, command: command)).Status);
        await using var db = f.CreateContext();
        var before = await db.Set<SubmissionDeclaration>().AsNoTracking().SingleAsync();
        Assert.Equal(409, (await Save(f, request, null, Input(10))).Status);
        Assert.Equal(400, (await Declare(f, request, false, command: (await Meta(f, request)) with { Reason = " " })).Status);
        Assert.Equal(403, (await Declare(f, request, false, RecipientId)).Status);
        f.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(200, (await Declare(f, request, false)).Status);
        var withdrawal = await db.Set<SubmissionDeclaration>().AsNoTracking().SingleAsync(x => x.IsWithdrawal);
        Assert.Equal(before.Id, withdrawal.SupersedesId); Assert.NotNull(withdrawal.AcceptedAtUtc);
        Assert.Equal(201, (await Save(f, request, null, Input(10))).Status);
        Assert.Equal(200, (await Declare(f, request, true)).Status);
        Assert.Equal(3, await db.Set<SubmissionDeclaration>().CountAsync());
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await db.Set<SubmissionDeclaration>().AsNoTracking().SingleAsync(x => x.Id == before.Id)));
        Assert.Null((await db.Set<SettlementRevision>().AsNoTracking().SingleAsync(x => x.RoundId == round)).AcceptedAtUtc);
        await db.Set<AdvanceRequest>().Where(x => x.Id == request).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Closed"));
        Assert.Equal(403, (await Declare(f, request, false)).Status);
        Assert.Equal(403, (await Save(f, request, null, Input(10))).Status);
        Assert.Equal(3, await db.Set<SubmissionDeclaration>().CountAsync());
    }

    [Fact]
    public async Task Original_deadline_uses_approved_snapshot_and_cannot_be_reconfirmed()
    {
        await using var f = await ReadyAt(); var (request, _) = await Draft(f);
        await ActivateCorrectionPolicy(f, 20);
        var command = await Meta(f, request);
        var result = await End(f, request, new(2026, 10, 31), command: command); Assert.Equal(200, result.Status);
        Assert.Equal(result, await End(f, request, new(2026, 10, 31), command: command));
        Assert.Equal(409, (await End(f, request, new(2026, 10, 30), command: command)).Status);
        Assert.Equal(409, (await End(f, request, new(2026, 10, 30))).Status);
        await using var db = f.CreateContext(); var completion = await db.Set<WorkCompletion>().SingleAsync();
        Assert.Equal(7, completion.ClearingDays); Assert.Equal(new DateOnly(2026, 11, 7), completion.ClearingDueOn);
        Assert.Equal(completion.ClearingDueOn, completion.RefundDueOn);
        Assert.Equal(RecipientPersonId, completion.ProposedByPersonId); Assert.NotNull(completion.AcceptedAtUtc);
    }

    [Fact]
    public async Task Competing_proposals_bind_to_accepted_basis_and_stale_versions_roll_back()
    {
        await using var f = await ReadyAt(); var (request, _) = await Draft(f);
        Assert.Equal(200, (await End(f, request, new(2026, 10, 20))).Status);
        var one = await Propose(f, request, new(2026, 10, 25)); Assert.Equal(200, one.Status);
        var two = await Propose(f, request, new(2026, 10, 26)); Assert.Equal(200, two.Status);
        var id = one.Value!.EntityId!.Value;
        foreach (var field in new[] { "request", "family", "entity" })
        {
            var command = await Meta(f, request, 1);
            command = field switch { "request" => command with { RequestVersion = command.RequestVersion + 1 }, "family" => command with { FamilyVersion = command.FamilyVersion + 1 }, _ => command with { EntityVersion = 2 } };
            Assert.Equal(409, (await Accept(f, request, id, AliasAccountId, command)).Status);
            await using var verify = f.CreateContext();
            Assert.False(await verify.Set<FinanceCommandReceipt>().AnyAsync(x => x.CommandId == command.CommandId));
            Assert.Null((await verify.Set<WorkCompletion>().SingleAsync(x => x.Id == id)).AcceptedAtUtc);
        }
        Assert.Equal(200, (await Accept(f, request, id, AliasAccountId)).Status);
        Assert.Equal(409, (await Accept(f, request, two.Value!.EntityId!.Value, AliasAccountId)).Status);
        await using var history = f.CreateContext();
        var stale = await history.Set<WorkCompletion>().SingleAsync(x => x.Id == two.Value!.EntityId!.Value);
        Assert.Equal("Cancelled", stale.State);
        Assert.Null(stale.AcceptedAtUtc);
        Assert.Equal(new DateOnly(2026, 10, 26), stale.CompletedOn);
        Assert.Equal(3, await history.Set<WorkCompletion>().CountAsync(x => x.RequestId == request));

    }

    [Fact]
    public async Task Current_pm_and_current_checker_authority_are_required_even_for_replay()
    {
        await using var f = await ReadyAt(); var (request, _) = await Draft(f);
        Assert.Equal(403, (await End(f, request, new(2026, 10, 25), ReviewerId)).Status);
        var command = await Meta(f, request);
        Assert.Equal(200, (await End(f, request, new(2026, 10, 25), command: command)).Status);
        await using var db = f.CreateContext();
        await db.Set<GrantAssignment>().Where(x => db.Set<GrantProposal>().Any(p => p.Id == x.ProposalId && p.RecipientId == RecipientId))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAtUtc, f.Clock.GetUtcNow()).SetProperty(x => x.RevokedBy, AuthorId).SetProperty(x => x.RevocationReason, "ถอนสิทธิ์"));
        Assert.Equal(403, (await End(f, request, new(2026, 10, 25), command: command)).Status);
    }

    [Fact]
    public async Task Simultaneous_initial_confirmation_replays_one_immutable_history()
    {
        await using var f = await ReadyAt(); var (request, _) = await Draft(f); var command = await Meta(f, request);
        await f.SessionAsync(RecipientId);
        var results = await Task.WhenAll(End(f, request, new(2026, 10, 25), command: command), End(f, request, new(2026, 10, 25), command: command));
        Assert.Equal(200, results[0].Status); Assert.Equal(results[0], results[1]);
        await using var db = f.CreateContext(); Assert.Single(await db.Set<WorkCompletion>().ToArrayAsync());
    }

    [Fact]
    public async Task Declaration_requires_accepted_end_and_withdrawal_requires_prior_complete()
    {
        await using var f = await ReadyAt(); var (request, _) = await Draft(f);
        Assert.Equal(409, (await Declare(f, request, true)).Status);
        Assert.Equal(409, (await Propose(f, request, new(2026, 10, 25))).Status);
        Assert.Equal(200, (await End(f, request, new(2026, 10, 25))).Status);
        Assert.Equal(409, (await Declare(f, request, false)).Status);
        Assert.Equal(200, (await Declare(f, request, true)).Status);
        Assert.Equal(409, (await Declare(f, request, true)).Status);
    }

    [Theory]
    [InlineData("2026-11-02T16:59:59Z", true)]
    [InlineData("2026-11-02T17:00:00Z", false)]
    public void Bangkok_cutoff_uses_system_submission_time(string utc, bool expected)
        => Assert.Equal(expected, SettlementDeadlineCalculator.OnTime(DateTimeOffset.Parse(utc), new(2026, 11, 2)));

    [Theory]
    [InlineData("revoked")]
    [InlineData("identity-unlinked")]
    [InlineData("owner-alias")]
    [InlineData("delegate")]
    [InlineData("payer")]
    [InlineData("approver")]
    [InlineData("foreign-request")]
    public async Task Correction_checker_requires_current_identity_scope_and_family_separation(string change)
    {
        await using var f = await ReadyAt(); var (request, _) = await Draft(f);
        Assert.Equal(200, (await End(f, request, new(2026, 10, 25))).Status);
        var proposal = await Propose(f, request, new(2026, 10, 26)); Assert.Equal(200, proposal.Status);
        var id = proposal.Value!.EntityId!.Value;
        await using var db = f.CreateContext();
        var checker = (await db.Set<FinanceAccountLink>().SingleAsync(x => x.UserId == AliasAccountId)).PersonId;
        if (change == "revoked")
            await db.Set<GrantAssignment>().Where(x => db.Set<GrantProposal>().Any(p => p.Id == x.ProposalId && p.RecipientId == AliasAccountId))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAtUtc, f.Clock.GetUtcNow()).SetProperty(x => x.RevokedBy, AuthorId).SetProperty(x => x.RevocationReason, "ถอนสิทธิ์"));
        else if (change is "identity-unlinked" or "owner-alias")
        {
            await db.Set<FinanceAccountLink>().Where(x => x.UserId == AliasAccountId).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.EndedAtUtc, f.Clock.GetUtcNow()).SetProperty(x => x.EndedBy, AuthorId)
                .SetProperty(x => x.EndReason, "เปลี่ยนบัญชีจำลอง").SetProperty(x => x.Version, x => x.Version + 1));
            if (change == "owner-alias")
            {
                db.Add(new FinanceAccountLink
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = WorkspaceId,
                    UserId = AliasAccountId,
                    PersonId = AuthorPersonId,
                    CreatedBy = AuthorId,
                    CreatedAtUtc = f.Clock.GetUtcNow(),
                    Reason = "บัญชีเจ้าของอีกบัญชี"
                });
                await db.SaveChangesAsync();
                await Grant(f, AliasAccountId, AuthorPersonId, "finance.check");
            }
        }
        else if (change is "delegate" or "payer" or "approver")
        {
            // A sibling's history participates in the same family's separation decision.
            var family = (await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == request)).FamilyId;
            var sibling = await Request(f, family, request);
            db.Add(new AdvanceRequestParticipant
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                ProjectId = ProjectId,
                RequestId = sibling,
                PersonId = checker,
                Role = change == "delegate" ? "Delegate" : change == "payer" ? "Payer" : "Approver",
                CreatedBy = AuthorId,
                CreatedAtUtc = f.Clock.GetUtcNow()
            });
            await db.SaveChangesAsync();
        }
        var target = change == "foreign-request" ? await Approved(f) : request;
        var meta = await Meta(f, target, 1);
        var before = await Meta(f, target);
        var receipts = await db.Set<FinanceCommandReceipt>().CountAsync();
        Assert.Equal(403, (await Accept(f, target, id, AliasAccountId, meta)).Status);
        Assert.Equal(receipts, await db.Set<FinanceCommandReceipt>().CountAsync());
        var after = await Meta(f, target);
        Assert.Equal(before.RequestVersion, after.RequestVersion); Assert.Equal(before.FamilyVersion, after.FamilyVersion);
        var pending = await db.Set<WorkCompletion>().AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Null(pending.AcceptedAtUtc); Assert.Equal(1, pending.Version);
    }

    [Fact]
    public async Task Accepted_correction_replay_rechecks_revocation_without_touching_immutable_row()
    {
        await using var f = await ReadyAt(); var (request, _) = await Draft(f);
        Assert.Equal(200, (await End(f, request, new(2026, 10, 25))).Status);
        var proposal = await Propose(f, request, new(2026, 10, 26)); Assert.Equal(200, proposal.Status);
        var id = proposal.Value!.EntityId!.Value; var command = await Meta(f, request, 1);
        Assert.Equal(200, (await Accept(f, request, id, AliasAccountId, command)).Status);
        await using var db = f.CreateContext(); var original = await db.Set<WorkCompletion>().AsNoTracking().SingleAsync(x => x.Id == id);
        await db.Set<GrantAssignment>().Where(x => db.Set<GrantProposal>().Any(p => p.Id == x.ProposalId && p.RecipientId == AliasAccountId))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAtUtc, f.Clock.GetUtcNow()).SetProperty(x => x.RevokedBy, AuthorId).SetProperty(x => x.RevocationReason, "ถอนสิทธิ์"));
        Assert.Equal(403, (await Accept(f, request, id, AliasAccountId, command)).Status);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(await db.Set<WorkCompletion>().AsNoTracking().SingleAsync(x => x.Id == id)));
    }

    [Fact]
    public async Task Owner_declaration_denies_delegate_and_revoked_replay()
    {
        await using var f = await ReadyAt(); var (request, _) = await Draft(f, ReviewerPersonId);
        Assert.Equal(200, (await End(f, request, new(2026, 10, 25))).Status);
        await using var db = f.CreateContext();
        await Grant(f, ReviewerId, ReviewerPersonId, "request.delegate");
        Assert.Equal(403, (await Declare(f, request, true, ReviewerId)).Status);
        var command = await Meta(f, request); Assert.Equal(200, (await Declare(f, request, true, command: command)).Status);
        await db.Set<GrantAssignment>().Where(x => db.Set<GrantProposal>().Any(p => p.Id == x.ProposalId && p.RecipientId == AuthorId))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAtUtc, f.Clock.GetUtcNow()).SetProperty(x => x.RevokedBy, AuthorId).SetProperty(x => x.RevocationReason, "ถอนสิทธิ์"));
        Assert.Equal(403, (await Declare(f, request, true, command: command)).Status);
        Assert.Single(await db.Set<SubmissionDeclaration>().ToArrayAsync());
    }

    [Theory]
    [InlineData("missing-policy", 409)]
    [InlineData("invalid-date", 422)]
    [InlineData("overflow", 422)]
    public async Task Invalid_end_basis_or_date_rolls_back_without_receipt(string mode, int status)
    {
        await using var f = await ReadyAt(); var (request, _) = await Draft(f);
        await using var db = f.CreateContext();
        if (mode == "missing-policy") await db.Set<AdvanceRequestRevision>().Where(x => x.RequestId == request).ExecuteUpdateAsync(s => s.SetProperty(x => x.PolicySnapshotJson, (string?)null).SetProperty(x => x.PolicyHash, (string?)null));
        var command = await Meta(f, request);
        Assert.Equal(status, (await End(f, request, mode == "overflow" ? DateOnly.MaxValue : mode == "invalid-date" ? default : new(2026, 10, 25), command: command)).Status);
        Assert.Empty(await db.Set<WorkCompletion>().ToArrayAsync());
        Assert.False(await db.Set<FinanceCommandReceipt>().AnyAsync(x => x.CommandId == command.CommandId));
        var after = await Meta(f, request);
        Assert.Equal(command.RequestVersion, after.RequestVersion); Assert.Equal(command.FamilyVersion, after.FamilyVersion);
    }

    [Fact]
    public async Task Returned_correction_can_continue_while_declared_and_keeps_late_initial_axis()
    {
        await using var f = await ReadyAt(); var (request, round) = await Draft(f);
        Assert.Equal(200, (await End(f, request, new(2026, 10, 20))).Status);
        await ActivateCorrectionPolicy(f, 2);
        Assert.Equal(200, (await Submit(f, AuthorId, request, round, await ConfirmCommand(f, request, round))).Status);
        Assert.Equal(200, (await Declare(f, request, true)).Status);
        Assert.Equal(200, (await Review(f, ReviewerId, request, round, "Return")).Status);
        await using var db = f.CreateContext(); var doc = await db.Set<ExpenseDocument>().SingleAsync();
        Assert.Equal(200, (await Save(f, request, round, Input(90, doc.Id))).Status);
        Assert.Equal(200, (await Save(f, request, round, Input(80, doc.Id))).Status);
        Assert.Equal(200, (await Submit(f, AuthorId, request, round, await ConfirmCommand(f, request, round))).Status);
        var corrected = await db.Set<SettlementRevision>().AsNoTracking().Where(x => x.RoundId == round).OrderByDescending(x => x.RevisionNumber).FirstAsync();
        using var snapshot = JsonDocument.Parse(corrected.SnapshotJson);
        var timing = snapshot.RootElement.GetProperty("Timing");
        Assert.False(timing.GetProperty("InitialSubmissionOnTime").GetBoolean());
        Assert.True(timing.GetProperty("CorrectionOnTime").GetBoolean());
        Assert.Single(await db.Set<SubmissionDeclaration>().ToArrayAsync());
        Assert.Equal(409, (await Save(f, request, null, Input(10, doc.Id))).Status);
    }

    [Fact]
    public async Task Initial_submission_before_end_confirmation_is_retained_and_assessed_without_backdating()
    {
        await using var f = await ReadyAt(); var (request, round) = await Draft(f);
        Assert.Equal(200, (await Submit(f, AuthorId, request, round, await ConfirmCommand(f, request, round))).Status);
        await using var db = f.CreateContext(); var first = await db.Set<SettlementRevision>().AsNoTracking().SingleAsync();
        using var submitted = JsonDocument.Parse(first.SnapshotJson);
        Assert.Equal(JsonValueKind.Null, submitted.RootElement.GetProperty("Timing").GetProperty("InitialSubmissionOnTime").ValueKind);
        f.Clock.Advance(TimeSpan.FromDays(5));
        Assert.Equal(200, (await End(f, request, new(2026, 10, 25))).Status);
        var completion = await db.Set<WorkCompletion>().AsNoTracking().SingleAsync();
        using var snapshot = JsonDocument.Parse(completion.SnapshotJson);
        var assessment = Assert.Single(snapshot.RootElement.GetProperty("Assessments").EnumerateArray());
        Assert.True(assessment.GetProperty("NewOnTime").GetBoolean());
        Assert.Equal(first.SubmittedAtUtc, assessment.GetProperty("SubmittedAtUtc").GetDateTimeOffset());
        Assert.Equal(first.SnapshotJson, (await db.Set<SettlementRevision>().AsNoTracking().SingleAsync()).SnapshotJson);
    }

    [Fact]
    public async Task First_declaration_after_corrected_end_keeps_original_deadline_axis()
    {
        await using var f = await ReadyAt(); var (request, _) = await Draft(f);
        Assert.Equal(200, (await End(f, request, new(2026, 10, 20))).Status);
        var proposal = await Propose(f, request, new(2026, 10, 30)); Assert.Equal(200, proposal.Status);
        Assert.Equal(200, (await Accept(f, request, proposal.Value!.EntityId!.Value, AliasAccountId)).Status);
        Assert.Equal(200, (await Declare(f, request, true)).Status);
        await using var db = f.CreateContext(); var declaration = await db.Set<SubmissionDeclaration>().SingleAsync();
        using var snapshot = JsonDocument.Parse(declaration.SnapshotJson);
        Assert.False(snapshot.RootElement.GetProperty("InitialCompletionOnTime").GetBoolean());
        Assert.True(snapshot.RootElement.GetProperty("CompletionOnTime").GetBoolean());
    }

    [Fact]
    public async Task Declaration_lineage_keeps_first_timestamp_even_if_system_clock_moves_back()
    {
        await using var f = await ReadyAt(); var (request, _) = await Draft(f);
        Assert.Equal(200, (await End(f, request, new(2026, 10, 25))).Status);
        Assert.Equal(200, (await Declare(f, request, true)).Status);
        await using var db = f.CreateContext(); var first = await db.Set<SubmissionDeclaration>().AsNoTracking().SingleAsync();
        Assert.Equal(200, (await Declare(f, request, false)).Status);
        f.Clock.Advance(TimeSpan.FromSeconds(-1));
        Assert.Equal(200, (await Declare(f, request, true)).Status);
        Assert.Equal(200, (await Declare(f, request, false)).Status);
        f.Clock.Advance(TimeSpan.FromSeconds(2));
        var final = await Declare(f, request, true); Assert.Equal(200, final.Status);
        var latest = await db.Set<SubmissionDeclaration>().AsNoTracking().SingleAsync(x => x.Id == final.Value!.EntityId);
        using var snapshot = JsonDocument.Parse(latest.SnapshotJson);
        Assert.Equal(first.CreatedAtUtc, snapshot.RootElement.GetProperty("InitialDeclaredAtUtc").GetDateTimeOffset());
    }

    private async Task<FieldAdvanceFixture> ReadyAt()
    {
        var f = await CreateAsync(postgres);
        f.Clock.Advance(DateTimeOffset.Parse("2026-10-31T16:59:00Z") - f.Clock.GetUtcNow());
        await f.SeedPoliciesAsync(); return f;
    }
    private static async Task<FinanceResult<SettlementCommandResult>> CompletionCommand(FieldAdvanceFixture f, Guid request, Guid actor,
        Func<SettlementCompletionService, Task<FinanceResult<SettlementCommandResult>>> run)
    {
        await using var db = f.CreateContext();
        return await run(new(db, await Access(f, db, actor), await Executor(f, db, actor), f.Clock));
    }
    private static async Task<FinanceResult<SettlementCommandResult>> End(FieldAdvanceFixture f, Guid request, DateOnly end, Guid? actor = null, SettlementCommand? command = null)
    {
        var who = actor ?? RecipientId; var meta = command ?? await Meta(f, request);
        return await CompletionCommand(f, request, who, s => s.ConfirmEndAsync(who, request, end, meta, default));
    }
    private static async Task<FinanceResult<SettlementCommandResult>> Propose(FieldAdvanceFixture f, Guid request, DateOnly end)
    {
        var command = await Meta(f, request);
        return await CompletionCommand(f, request, RecipientId, s => s.ProposeEndCorrectionAsync(RecipientId, request, end, command, default));
    }
    private static async Task<FinanceResult<SettlementCommandResult>> Accept(FieldAdvanceFixture f, Guid request, Guid proposal, Guid actor, SettlementCommand? command = null)
    {
        await using var db = f.CreateContext(); var version = (await db.Set<WorkCompletion>().SingleAsync(x => x.Id == proposal)).Version;
        var meta = command ?? await Meta(f, request, version);
        return await CompletionCommand(f, request, actor, s => s.ConfirmEndCorrectionAsync(actor, request, proposal, meta, default));
    }
    private static async Task<FinanceResult<SettlementCommandResult>> Declare(FieldAdvanceFixture f, Guid request, bool complete, Guid? actor = null, SettlementCommand? command = null)
    {
        var who = actor ?? AuthorId; var meta = command ?? await Meta(f, request);
        return await CompletionCommand(f, request, who, s => s.DeclareCompleteAsync(who, request, complete, meta, default));
    }

    private static async Task ActivateCorrectionPolicy(FieldAdvanceFixture f, int days)
    {
        foreach (var (actor, person) in new[] { (AuthorId, AuthorPersonId), (ReviewerId, ReviewerPersonId) })
        {
            var id = await FieldAdvanceGrantTests.AuthorityAsync(f, actor, person, company: true);
            await using var db = f.CreateContext();
            var assignment = await db.Set<GrantAssignment>().SingleAsync(x => x.Id == id);
            var grant = await db.Set<GrantProposal>().SingleAsync(x => x.Id == assignment.ProposalId);
            grant.Capabilities = ["policy.edit.company", "policy.review"]; await db.SaveChangesAsync();
        }
        await using var policyDb = f.CreateContext();
        var active = await policyDb.Set<ActivePolicy>().AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == WorkspaceId && x.ProjectId == null);
        var content = FieldAdvancePolicyTests.Content(30) with { CorrectionDays = days };
        var created = await FieldAdvancePolicyTests.Run(f, AuthorId, service => service.CreateDraftAsync(AuthorId,
            new(WorkspaceId, null), null, content,
            new(Guid.NewGuid(), 1, "นโยบายแก้ไข") { Policy = new("Company", active?.PolicyRevisionId ?? CompanyRevisionId, ExpectedActiveRevisionId: active?.PolicyRevisionId, ExpectedActiveVersion: active?.Version ?? 0) }, default));
        Assert.True(created.Status == 201, created.Code);
        await FieldAdvancePolicyTests.Review(f, await FieldAdvancePolicyTests.Submit(f, created.Value!));
    }

    private static async Task<(Guid Request, Guid Round)> Draft(FieldAdvanceFixture f, Guid? delegatePerson = null)
    {
        var request = await Approved(f, delegatePerson); var doc = await Document(f, request);
        await Grant(f, ReviewerId, ReviewerPersonId, "request.approve");
        await Grant(f, RecipientId, RecipientPersonId, "request.approve");
        await using var db = f.CreateContext();
        var checker = Guid.NewGuid();
        db.Add(new FinancePerson { Id = checker, WorkspaceId = WorkspaceId, CreatedBy = AuthorId, Reason = "ผู้ตรวจจำลอง" });
        db.Add(new FinanceAccountLink
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            UserId = AliasAccountId,
            PersonId = checker,
            CreatedBy = AuthorId,
            CreatedAtUtc = f.Clock.GetUtcNow().AddMinutes(-1),
            Reason = "ผู้ตรวจจำลอง"
        });
        var pm = await db.Set<GrantProposal>().SingleAsync(x => x.RecipientId == RecipientId);
        pm.PrimaryApprovalLevels = ["project-manager"];
        db.Add(new ReportingLine
        {
            Id = Guid.NewGuid(),
            EmployeeMembershipId = (await db.Set<EmployeeMembership>().SingleAsync(x => x.UserId == AuthorId)).Id,
            EmployeeUserId = AuthorId,
            SupervisorUserId = ReviewerId,
            ValidFromUtc = f.Clock.GetUtcNow().AddDays(-1),
            CreatedBy = AuthorId,
            Reason = "สายงาน"
        });
        await db.SaveChangesAsync(); await Grant(f, AliasAccountId, checker, "finance.check");
        var saved = await Save(f, request, null, Input(100, doc)); Assert.Equal(201, saved.Status);
        return (request, saved.Value!.EntityId!.Value);
    }
    private static async Task<SettlementSubmissionService> Submission(FieldAdvanceFixture f, Tpr10DbContext db, Guid actor)
    {
        var executor = await Executor(f, db, actor);
        return new(db, await Access(f, db, actor), executor, new(db, executor), new(db, new(db, f.Clock), f.Clock), f.Clock);
    }
    private static async Task<SettlementCommand> ConfirmCommand(FieldAdvanceFixture f, Guid request, Guid round)
    {
        await using var db = f.CreateContext(); var preview = await (await Submission(f, db, AuthorId)).PreviewAsync(AuthorId, request, round, default);
        Assert.Equal(200, preview.Status);
        return (await Meta(f, request, preview.Value!.RevisionVersion)) with { ConfirmationHash = preview.Value.ConfirmationHash };
    }
    private static async Task<FinanceResult<SettlementCommandResult>> Submit(FieldAdvanceFixture f, Guid actor, Guid request, Guid round, SettlementCommand command)
    {
        await using var db = f.CreateContext(); return await (await Submission(f, db, actor)).SubmitAsync(actor, request, round, command, default);
    }
    private static async Task<SettlementCommand> ReviewCommand(FieldAdvanceFixture f, Guid request, Guid round)
    {
        await using var db = f.CreateContext(); var revision = await db.Set<SettlementRevision>().Where(x => x.RoundId == round).OrderByDescending(x => x.RevisionNumber).FirstAsync();
        var step = await db.Set<SettlementApprovalStep>().Where(x => x.RevisionId == revision.Id && x.State == "Pending").OrderBy(x => x.StepOrder).FirstOrDefaultAsync();
        return await Meta(f, request, step?.Version);
    }
    private static async Task<FinanceResult<SettlementCommandResult>> Review(FieldAdvanceFixture f, Guid actor, Guid request, Guid round,
        string decision = "Approve", string reason = "ตรวจแล้ว", SettlementCommand? command = null)
    {
        await using var db = f.CreateContext(); var session = await f.SessionAsync(actor);
        var policies = new PolicyResolver(db, FieldAdvanceAccessTests.Access(db, session, f.Clock), session, f.Clock);
        var service = new SettlementReviewService(db, await Access(f, db, actor), await Executor(f, db, actor),
            new(db, new(db, f.Clock), f.Clock), new(db, policies, f.Clock), f.Clock);
        return await service.ReviewAsync(actor, request, round, new(decision, reason), command ?? await ReviewCommand(f, request, round), default);
    }
}
