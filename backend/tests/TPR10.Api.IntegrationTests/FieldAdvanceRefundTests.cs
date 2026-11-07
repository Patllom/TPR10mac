using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Policies;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Settlements;
using TPR10.Api.FieldAdvances.Data;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementAccessTests;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementReviewTests;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementCommandTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceRefundTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("Pending")]
    [InlineData("Returned")]
    [InlineData("Draft")]
    public async Task Final_expense_acceptance_preserves_existing_refund_claim(string state)
    {
        await using var f = await Ready(postgres);
        var (request, round) = await Draft(f);
        await SeedPaid(f, request, 1000m);
        Assert.Equal(200, (await Submit(f, AuthorId, request, round, await ConfirmCommand(f, request, round))).Status);
        Assert.Equal(200, (await Review(f, ReviewerId, request, round)).Status);
        Assert.Equal(200, (await Review(f, RecipientId, request, round)).Status);
        await using (var db = f.CreateContext())
        {
            var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request);
            var refund = await Row<Refund>(f, db, r);
            refund.Amount = 950m; refund.State = state; refund.Channel = "Transfer";
            refund.RefundedOn = new(2026, 10, 30);
            refund.EvidenceId = (await db.Set<AdvanceEvidence>().SingleAsync()).Id;
            db.Add(refund); await db.SaveChangesAsync();
        }
        var result = await Review(f, AliasAccountId, request, round);
        Assert.Equal(409, result.Status);
        await using var verify = f.CreateContext();
        Assert.Null((await verify.Set<SettlementRound>().SingleAsync(x => x.Id == round)).AcceptedAtUtc);
        Assert.Equal(950m, (await verify.Set<Refund>().SingleAsync()).Amount);
    }

    [Fact]
    public async Task Pending_refund_does_not_reduce_balance_or_allow_duplicate_proposal()
    {
        await using var f = await Ready(postgres); var (request, evidence) = await RefundReady(f);
        var proposal = await RefundCall(f, request, "Propose", input: RefundInputFor(1500, evidence));
        Assert.Equal(201, proposal.Status); var id = proposal.Value!.EntityId!.Value;
        Assert.Equal(200, (await RefundCall(f, request, "Confirm", id)).Status);
        var beforeReview = await Totals(f, request);
        Assert.Equal(2000m, beforeReview.OwnerOutstanding);
        Assert.Equal(1500m, beforeReview.PendingRefund);
        var secondProposalFor600 = await RefundCall(f, request, "Propose", input: RefundInputFor(600, evidence));
        Assert.Equal(409, secondProposalFor600.Status);
        var command = await RefundMeta(f, request, id);
        var accepted = await RefundCall(f, request, "Approve", id, actor: ReviewerId, command: command);
        Assert.Equal(200, accepted.Status);
        Assert.Equal(accepted, await RefundCall(f, request, "Approve", id, actor: ReviewerId, command: command));
        var after = await Totals(f, request);
        Assert.Equal(500m, after.OwnerOutstanding); Assert.Equal(1500m, after.ConfirmedRefund); Assert.Equal(0m, after.PendingRefund);
    }

    [Fact]
    public async Task Delegate_prepares_owner_confirms_and_return_retains_claim_until_reduction_or_cancel()
    {
        await using var f = await Ready(postgres); var (request, evidence) = await RefundReady(f, true);
        var created = await RefundCall(f, request, "Propose", input: RefundInputFor(1500, evidence), actor: RecipientId);
        Assert.Equal(201, created.Status); var id = created.Value!.EntityId!.Value;
        Assert.Equal(403, (await RefundCall(f, request, "Confirm", id, actor: RecipientId)).Status);
        Assert.Equal(409, (await RefundCall(f, request, "Approve", id, actor: ReviewerId)).Status);
        Assert.Equal(200, (await RefundCall(f, request, "Confirm", id)).Status);
        Assert.Equal(200, (await RefundCall(f, request, "Return", id, actor: ReviewerId)).Status);
        Assert.Equal(1500m, (await Totals(f, request)).PendingRefund);
        Assert.Equal(409, (await RefundCall(f, request, "Propose", input: RefundInputFor(600, evidence))).Status);
        Assert.Equal(200, (await RefundCall(f, request, "Update", id, RefundInputFor(1000, evidence), RecipientId)).Status);
        Assert.Equal(1000m, (await Totals(f, request)).PendingRefund);
        Assert.Equal(409, (await RefundCall(f, request, "Approve", id, actor: ReviewerId)).Status);
        Assert.Equal(200, (await RefundCall(f, request, "Confirm", id)).Status);
        Assert.Equal(200, (await RefundCall(f, request, "Cancel", id, actor: RecipientId)).Status);
        Assert.Equal(0m, (await Totals(f, request)).PendingRefund);
        Assert.Equal(2000m, (await Totals(f, request)).OwnerOutstanding);
        await using var db = f.CreateContext();
        Assert.DoesNotContain(await db.Set<AdvancePaymentParticipation>().ToArrayAsync(), x => x.PersonId == AuthorPersonId || x.PersonId == RecipientPersonId || x.Role == "Payer");
        Assert.Equal("Cancelled", (await db.Set<Refund>().SingleAsync()).State);
    }

    [Theory]
    [InlineData("precision")]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("overflow")]
    [InlineData("date")]
    [InlineData("future")]
    [InlineData("channel")]
    [InlineData("destination")]
    [InlineData("empty-evidence")]
    [InlineData("duplicate-evidence")]
    [InlineData("missing-evidence")]
    [InlineData("not-ready")]
    [InlineData("other-request")]
    [InlineData("case")]
    public async Task Invalid_proposals_do_not_create_claims(string kind)
    {
        await using var f = await Ready(postgres); var (request, evidence) = await RefundReady(f);
        var input = RefundInputFor(500, evidence);
        input = kind switch
        {
            "precision" => input with { Amount = 0.001m },
            "zero" => input with { Amount = 0 },
            "negative" => input with { Amount = -1 },
            "overflow" => input with { Amount = decimal.MaxValue },
            "date" => input with { ReturnedOn = default },
            "future" => input with { ReturnedOn = new(2099, 1, 1) },
            "channel" => input with { Channel = "BankAPI" },
            "destination" => input with { CompanyRecipient = " " },
            "empty-evidence" => input with { EvidenceIds = [] },
            "duplicate-evidence" => input with { EvidenceIds = [evidence, evidence] },
            "missing-evidence" => input with { EvidenceIds = [evidence, Guid.NewGuid()] },
            "case" => input with { AdjustmentCaseId = Guid.NewGuid() },
            _ => input
        };
        if (kind == "not-ready") { await using var db = f.CreateContext(); (await db.Set<AdvanceEvidence>().SingleAsync()).State = "Pending"; await db.SaveChangesAsync(); }
        if (kind == "other-request")
        {
            var other = await FieldAdvanceSettlementDraftTests.Approved(f);
            await FieldAdvanceSettlementDraftTests.Document(f, other);
            await using var db = f.CreateContext();
            input = input with { EvidenceIds = [evidence, (await db.Set<AdvanceEvidence>().SingleAsync(x => x.RequestId == other)).Id] };
        }
        Assert.Equal(422, (await RefundCall(f, request, "Propose", input: input)).Status);
        await using var verify = f.CreateContext(); Assert.Empty(await verify.Set<Refund>().ToArrayAsync());
    }

    [Fact]
    public async Task Accepted_refund_cannot_be_edited_cancelled_or_reviewed_again()
    {
        await using var f = await Ready(postgres); var (request, evidence) = await RefundReady(f);
        var created = await RefundCall(f, request, "Propose", input: RefundInputFor(500, evidence));
        Assert.Equal(201, created.Status); var id = created.Value!.EntityId!.Value;
        Assert.Equal(200, (await RefundCall(f, request, "Confirm", id)).Status);
        Assert.Equal(200, (await RefundCall(f, request, "Approve", id, actor: ReviewerId)).Status);
        await using var db = f.CreateContext(); var original = JsonSerializer.Serialize(await db.Set<Refund>().AsNoTracking().SingleAsync());
        foreach (var op in new[] { "Update", "Cancel", "Confirm", "Approve", "Return" })
            Assert.Equal(409, (await RefundCall(f, request, op, id, RefundInputFor(400, evidence), op is "Approve" or "Return" ? ReviewerId : AuthorId)).Status);
        Assert.Equal(original, JsonSerializer.Serialize(await db.Set<Refund>().AsNoTracking().SingleAsync()));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("ready")]
    public async Task Changed_evidence_requires_fresh_owner_confirmation(string change)
    {
        await using var f = await Ready(postgres); var (request, evidence) = await RefundReady(f);
        var created = await RefundCall(f, request, "Propose", input: RefundInputFor(500, evidence));
        Assert.Equal(201, created.Status); var id = created.Value!.EntityId!.Value;
        Assert.Equal(200, (await RefundCall(f, request, "Confirm", id)).Status);
        await using (var db = f.CreateContext()) { var row = await db.Set<AdvanceEvidence>().SingleAsync(); if (change == "hash") row.Sha256 = new('b', 64); else row.State = "Pending"; await db.SaveChangesAsync(); }
        Assert.Equal(409, (await RefundCall(f, request, "Approve", id, actor: ReviewerId)).Status);
        Assert.Equal(200, (await RefundCall(f, request, "Return", id, actor: ReviewerId)).Status);
        Assert.Equal(500m, (await Totals(f, request)).PendingRefund);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Refund_and_final_expense_serialize_both_winning_orders_and_retry_capacity(bool refundFirst)
    {
        await using var f = await Ready(postgres); var (request, round) = await Draft(f);
        await SeedPaid(f, request, 1000);
        await using var lookup = f.CreateContext(); var evidence = (await lookup.Set<AdvanceEvidence>().SingleAsync()).Id;
        Assert.Equal(200, (await Submit(f, AuthorId, request, round, await ConfirmCommand(f, request, round))).Status);
        Assert.Equal(200, (await Review(f, ReviewerId, request, round)).Status);
        Assert.Equal(200, (await Review(f, RecipientId, request, round)).Status);
        var refundCommand = await Meta(f, request); var expenseCommand = await ReviewCommand(f, request, round);
        var barrier = new FieldAdvancePaymentRaceTests.Barrier();
        var winner = refundFirst
            ? RefundCall(f, request, "Propose", input: RefundInputFor(950, evidence), command: refundCommand, barrier: barrier)
            : ExpenseFinal(f, request, round, expenseCommand, barrier);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var loser = refundFirst ? ExpenseFinal(f, request, round, expenseCommand)
            : RefundCall(f, request, "Propose", input: RefundInputFor(950, evidence), command: refundCommand);
        try { await FieldAdvancePaymentRaceTests.Waiting(f, loser); }
        finally { barrier.Release.TrySetResult(); }
        Assert.Equal(refundFirst ? 201 : 200, (await winner).Status);
        var stale = await loser; Assert.Equal(409, stale.Status); Assert.Equal("settlement-conflict", stale.Code);
        var retry = refundFirst ? await ExpenseFinal(f, request, round, await ReviewCommand(f, request, round))
            : await RefundCall(f, request, "Propose", input: RefundInputFor(950, evidence));
        Assert.Equal(409, retry.Status); Assert.Equal("refund-capacity-exceeded", retry.Code);
        var totals = await Totals(f, request);
        Assert.Equal(refundFirst ? 950m : 0m, totals.PendingRefund);
        Assert.Equal(refundFirst ? 0m : 100m, totals.ApprovedExpense);
        Assert.True(totals.OwnerOutstanding >= totals.PendingRefund);
    }

    [Fact]
    public async Task Confirmed_refund_also_blocks_final_expense_and_expense_only_negative_balance_survives()
    {
        await using var f = await Ready(postgres); var (request, round) = await Draft(f); await SeedPaid(f, request, 1000);
        await using var db = f.CreateContext(); var evidence = (await db.Set<AdvanceEvidence>().SingleAsync()).Id;
        var proposed = await RefundCall(f, request, "Propose", input: RefundInputFor(950, evidence)); Assert.Equal(201, proposed.Status);
        var id = proposed.Value!.EntityId!.Value;
        Assert.Equal(200, (await RefundCall(f, request, "Confirm", id)).Status);
        Assert.Equal(200, (await RefundCall(f, request, "Approve", id, actor: AliasAccountId)).Status);
        Assert.Equal(200, (await Submit(f, AuthorId, request, round, await ConfirmCommand(f, request, round))).Status);
        Assert.Equal(200, (await Review(f, ReviewerId, request, round)).Status);
        Assert.Equal(200, (await Review(f, RecipientId, request, round)).Status);
        Assert.Equal("refund-capacity-exceeded", (await Review(f, AliasAccountId, request, round)).Code);
        Assert.Equal(950m, (await Totals(f, request)).ConfirmedRefund);
        await using var other = await Ready(postgres); var (otherRequest, otherRound) = await Draft(other);
        Assert.Equal(200, (await Submit(other, AuthorId, otherRequest, otherRound, await ConfirmCommand(other, otherRequest, otherRound))).Status);
        foreach (var actor in new[] { ReviewerId, RecipientId, AliasAccountId }) Assert.Equal(200, (await Review(other, actor, otherRequest, otherRound)).Status);
        Assert.Equal(-100m, (await Totals(other, otherRequest)).OwnerOutstanding);
    }

    [Fact]
    public async Task Full_evidence_set_cash_and_audited_finance_attestation_are_preserved()
    {
        await using var f = await Ready(postgres); var (request, first) = await RefundReady(f);
        await FieldAdvanceSettlementDraftTests.Document(f, request);
        await using var db = f.CreateContext(); var second = await db.Set<AdvanceEvidence>().SingleAsync(x => x.Id != first);
        second.Sha256 = new('c', 64); await db.SaveChangesAsync();
        var input = RefundInputFor(200, first, second.Id) with { Channel = "Cash", CompanyRecipient = "ผู้รับเงินสดบริษัทจำลอง" };
        var command = await Meta(f, request);
        var created = await RefundCall(f, request, "Propose", input: input, command: command); Assert.Equal(201, created.Status);
        Assert.Equal(created, await RefundCall(f, request, "Propose", input: input, command: command));
        Assert.Equal(409, (await RefundCall(f, request, "Propose", input: input with { Amount = 300 }, command: command)).Status);
        var id = created.Value!.EntityId!.Value;
        Assert.Equal(200, (await RefundCall(f, request, "Confirm", id)).Status);
        var before = await Snapshot(f, id);
        Assert.Equal(200, (await RefundCall(f, request, "Approve", id, actor: ReviewerId)).Status);
        var after = await Snapshot(f, id); var row = await db.Set<Refund>().AsNoTracking().SingleAsync();
        Assert.Equal(first, row.EvidenceId); Assert.Equal(input.EvidenceIds, after.Evidence.Select(x => x.Id));
        Assert.Equal(new('c', 64), after.Evidence[1].Sha256);
        Assert.Equal(JsonSerializer.Serialize(before.History), JsonSerializer.Serialize(after.History.Take(before.History.Length)));
        var decision = after.History.Last(); Assert.True(decision.CompanyDestinationAndEvidenceVerified);
        Assert.Equal(ReviewerPersonId, decision.PersonId); Assert.Equal(ReviewerId, decision.ActorId);
        Assert.Equal(input.CompanyRecipient, decision.Input.CompanyRecipient); Assert.NotEmpty(decision.Reason);
        Assert.Equal(before.Timing, after.Timing);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("payer")]
    [InlineData("revoked")]
    [InlineData("closed")]
    public async Task Current_authority_and_person_history_precede_replay(string mode)
    {
        await using var f = await Ready(postgres); var (request, evidence) = await RefundReady(f);
        var created = await RefundCall(f, request, "Propose", input: RefundInputFor(200, evidence)); Assert.Equal(201, created.Status);
        var id = created.Value!.EntityId!.Value;
        Assert.Equal(200, (await RefundCall(f, request, "Confirm", id)).Status);
        var command = await RefundMeta(f, request, id);
        if (mode == "revoked") Assert.Equal(200, (await RefundCall(f, request, "Approve", id, actor: ReviewerId, command: command)).Status);
        await using (var db = f.CreateContext())
        {
            if (mode == "revoked")
            {
                var grant = await db.Set<GrantProposal>().SingleAsync(x => x.RecipientId == ReviewerId);
                var assignment = await db.Set<GrantAssignment>().SingleAsync(x => x.ProposalId == grant.Id);
                assignment.RevokedAtUtc = f.Clock.GetUtcNow(); assignment.RevokedBy = AuthorId; assignment.RevocationReason = "ถอนสิทธิ์จำลอง";
            }
            if (mode == "owner")
            {
                db.Add(new FinanceAccountLink
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = WorkspaceId,
                    UserId = AliasAccountId,
                    PersonId = AuthorPersonId,
                    CreatedBy = AuthorId,
                    CreatedAtUtc = f.Clock.GetUtcNow().AddMinutes(-1),
                    Reason = "บัญชีเจ้าของอีกบัญชี"
                });
            }
            if (mode == "payer")
            {
                db.Add(new AdvanceRequestParticipant
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = WorkspaceId,
                    ProjectId = ProjectId,
                    RequestId = request,
                    PersonId = ReviewerPersonId,
                    Role = "Payer",
                    CreatedBy = AuthorId,
                    CreatedAtUtc = f.Clock.GetUtcNow()
                });
            }
            if (mode == "closed") (await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request)).State = "Closed";
            await db.SaveChangesAsync();
        }
        if (mode == "owner") await Grant(f, AliasAccountId, AuthorPersonId, "finance.check");
        await using var verify = f.CreateContext(); var before = await verify.Set<FinanceCommandReceipt>().CountAsync();
        Assert.Equal(403, (await RefundCall(f, request, "Approve", id, actor: mode == "owner" ? AliasAccountId : ReviewerId, command: command)).Status);
        Assert.Equal(before, await verify.Set<FinanceCommandReceipt>().CountAsync());
        if (mode == "closed")
            foreach (var op in new[] { "Propose", "Confirm", "Update", "Cancel" })
                Assert.Equal(403, (await RefundCall(f, request, op, op == "Propose" ? null : id, RefundInputFor(100, evidence))).Status);
    }

    [Fact]
    public async Task Return_and_reconfirmation_keep_original_deadline_history_and_server_timestamp()
    {
        await using var f = await Ready(postgres);
        f.Clock.Advance(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero) - f.Clock.GetUtcNow());
        var (request, evidence) = await RefundReady(f); f.Clock.Advance(TimeSpan.FromHours(4) + TimeSpan.FromMinutes(59)); var originalEnd = await SeedEnd(f, request, new(2026, 11, 1));
        var created = await RefundCall(f, request, "Propose", input: RefundInputFor(200, evidence)); Assert.Equal(201, created.Status); var id = created.Value!.EntityId!.Value;
        Assert.Equal(200, (await RefundCall(f, request, "Confirm", id)).Status); var first = await Snapshot(f, id);
        Assert.True(first.Timing!.InitialSubmissionOnTime); Assert.True(first.Timing.SubmissionOnTime);
        f.Clock.Advance(TimeSpan.FromMinutes(1)); // Bangkok is now November 2.
        Assert.Equal(200, (await RefundCall(f, request, "Return", id, actor: ReviewerId)).Status);
        var returned = await Snapshot(f, id);
        Assert.Equal(200, (await RefundCall(f, request, "Update", id, RefundInputFor(150, evidence))).Status);
        Assert.Equal(200, (await RefundCall(f, request, "Confirm", id)).Status); var late = await Snapshot(f, id);
        Assert.False(late.Timing!.SubmissionOnTime); Assert.True(late.Timing.InitialSubmissionOnTime);
        Assert.Equal(first.Timing.InitialSubmittedAtUtc, late.Timing.InitialSubmittedAtUtc);
        Assert.Equal(JsonSerializer.Serialize(returned.History), JsonSerializer.Serialize(late.History.Take(returned.History.Length)));
        Assert.Equal(200, (await RefundCall(f, request, "Return", id, actor: ReviewerId)).Status);
        var correctedEnd = await SeedEnd(f, request, new(2026, 11, 3), originalEnd);
        f.Clock.Advance(TimeSpan.FromHours(-1)); // Clock reversal must not select a new initial submission.
        Assert.Equal(200, (await RefundCall(f, request, "Confirm", id)).Status); var revised = await Snapshot(f, id);
        Assert.Equal(originalEnd, revised.Timing!.OriginalWorkCompletionId); Assert.Equal(correctedEnd, revised.Timing.WorkCompletionId);
        Assert.Equal(new DateOnly(2026, 11, 1), revised.Timing.OriginalDueOn); Assert.Equal(new DateOnly(2026, 11, 3), revised.Timing.EffectiveDueOn);
        Assert.Equal(first.Timing.InitialSubmittedAtUtc, revised.Timing.InitialSubmittedAtUtc);
        f.Clock.Advance(TimeSpan.FromDays(5));
        Assert.Equal(200, (await RefundCall(f, request, "Approve", id, actor: ReviewerId)).Status);
        Assert.Equal(revised.Timing, (await Snapshot(f, id)).Timing);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Accepted_incident_replaces_paid_basis_and_wrong_recipient_recovery_is_not_owner_debt(bool corrupt)
    {
        await using var f = await Ready(postgres); var (request, evidence) = await RefundReady(f);
        var incident = await SeedIncident(f, request, evidence, 800, 1200);
        await SeedRecovery(f, request, evidence, incident, "Owner", AuthorPersonId, corrupt ? 801 : 100);
        await SeedRecovery(f, request, evidence, incident, "WrongRecipient", RecipientPersonId, 200);
        // Ordinary return is a distinct transfer, not reuse of incident recovery proof.
        await using (var proofDb = f.CreateContext())
        {
            var proof = await proofDb.Set<AdvanceEvidence>().AsNoTracking().SingleAsync(x => x.Id == evidence);
            proof.Id = Guid.NewGuid(); proof.Sha256 = new('b', 64); proofDb.Add(proof); await proofDb.SaveChangesAsync(); evidence = proof.Id;
        }
        if (corrupt)
        {
            Assert.Equal("settlement-balance-inconsistent", (await RefundCall(f, request, "Propose", input: RefundInputFor(1, evidence))).Code);
        }
        else
        {
            var totals = await Totals(f, request);
            Assert.Equal(800m, totals.OwnerReceived); Assert.Equal(700m, totals.OwnerOutstanding); Assert.Equal(100m, totals.ConfirmedRefund);
            Assert.Equal(1000m, totals.WrongRecipientOutstanding); Assert.True(totals.HasOpenCase);
            Assert.Equal(409, (await RefundCall(f, request, "Propose", input: RefundInputFor(701, evidence))).Status);
            Assert.Equal(201, (await RefundCall(f, request, "Propose", input: RefundInputFor(700, evidence))).Status);
        }
    }

    [Theory]
    [InlineData("Confirm")]
    [InlineData("Approve")]
    public async Task Current_capacity_is_rechecked_when_incident_replaces_payment(string operation)
    {
        await using var f = await Ready(postgres); var (request, evidence) = await RefundReady(f);
        var created = await RefundCall(f, request, "Propose", input: RefundInputFor(1500, evidence)); Assert.Equal(201, created.Status); var id = created.Value!.EntityId!.Value;
        if (operation == "Approve") Assert.Equal(200, (await RefundCall(f, request, "Confirm", id)).Status);
        await SeedIncident(f, request, evidence, 1000, 1000);
        Assert.Equal("refund-capacity-exceeded", (await RefundCall(f, request, operation, id, actor: operation == "Approve" ? ReviewerId : AuthorId)).Code);
        await using var db = f.CreateContext(); Assert.Null((await db.Set<Refund>().SingleAsync()).AcceptedAtUtc);
        Assert.Equal(200, (await RefundCall(f, request, "Update", id, RefundInputFor(1000, evidence))).Status);
        Assert.Equal(200, (await RefundCall(f, request, "Confirm", id)).Status);
        Assert.Equal(200, (await RefundCall(f, request, "Approve", id, actor: ReviewerId)).Status);
        Assert.Equal(0m, (await Totals(f, request)).OwnerOutstanding);
    }

    [Fact]
    public async Task Cancelling_returned_claim_does_not_clear_an_already_accepted_installment()
    {
        await using var f = await Ready(postgres); var (request, evidence) = await RefundReady(f);
        var first = await RefundCall(f, request, "Propose", input: RefundInputFor(500, evidence)); Assert.Equal(201, first.Status);
        var acceptedId = first.Value!.EntityId!.Value;
        Assert.Equal(200, (await RefundCall(f, request, "Confirm", acceptedId)).Status);
        Assert.Equal(200, (await RefundCall(f, request, "Approve", acceptedId, actor: ReviewerId)).Status);
        var accepted = JsonSerializer.Serialize(await Snapshot(f, acceptedId));
        var next = await RefundCall(f, request, "Propose", input: RefundInputFor(1500, evidence)); Assert.Equal(201, next.Status);
        var pendingId = next.Value!.EntityId!.Value;
        Assert.Equal(200, (await RefundCall(f, request, "Confirm", pendingId)).Status);
        Assert.Equal(200, (await RefundCall(f, request, "Return", pendingId, actor: ReviewerId)).Status);
        Assert.Equal(200, (await RefundCall(f, request, "Update", pendingId, RefundInputFor(1000, evidence))).Status);
        Assert.Equal(1000m, (await Totals(f, request)).PendingRefund);
        Assert.Equal(200, (await RefundCall(f, request, "Cancel", pendingId)).Status);
        var totals = await Totals(f, request); Assert.Equal(500m, totals.ConfirmedRefund); Assert.Equal(1500m, totals.OwnerOutstanding); Assert.Equal(0m, totals.PendingRefund);
        Assert.Equal(accepted, JsonSerializer.Serialize(await Snapshot(f, acceptedId)));
    }

    [Theory]
    [InlineData("Reject", "เหตุผล")]
    [InlineData("Approve", " ")]
    [InlineData("Return", " ")]
    public async Task Invalid_review_rolls_back_receipt_participation_and_entity(string decision, string reason)
    {
        await using var f = await Ready(postgres); var (request, evidence) = await RefundReady(f);
        var proposal = await RefundCall(f, request, "Propose", input: RefundInputFor(500, evidence)); Assert.Equal(201, proposal.Status);
        var id = proposal.Value!.EntityId!.Value; Assert.Equal(200, (await RefundCall(f, request, "Confirm", id)).Status);
        await using var db = f.CreateContext(); var before = JsonSerializer.Serialize(await db.Set<Refund>().AsNoTracking().SingleAsync());
        var receiptCount = await db.Set<FinanceCommandReceipt>().CountAsync();
        using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        var service = new RefundReviewService(db, await Access(f, db, ReviewerId), await Executor(f, db, ReviewerId), f.Clock, files.Options);
        var result = await service.ReviewAsync(ReviewerId, request, id, new(decision, reason), await RefundMeta(f, request, id), default);
        Assert.Equal(422, result.Status);
        await using var verify = f.CreateContext(); Assert.Equal(receiptCount, await verify.Set<FinanceCommandReceipt>().CountAsync());
        Assert.Empty(await verify.Set<AdvancePaymentParticipation>().ToArrayAsync());
        Assert.Equal(before, JsonSerializer.Serialize(await verify.Set<Refund>().AsNoTracking().SingleAsync()));
    }

    private static async Task<RefundSnapshot> Snapshot(FieldAdvanceFixture f, Guid id)
    { await using var db = f.CreateContext(); return JsonSerializer.Deserialize<RefundSnapshot>((await db.Set<Refund>().SingleAsync(x => x.Id == id)).SnapshotJson)!; }

    private static async Task<Guid> SeedEnd(FieldAdvanceFixture f, Guid request, DateOnly due, Guid? previous = null)
    {
        await using var db = f.CreateContext(); var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request);
        var end = await Row<WorkCompletion>(f, db, r, ReviewerId, ReviewerPersonId);
        end.CompletedOn = due.AddDays(-7); end.ClearingDays = 7; end.ClearingDueOn = due; end.RefundDueOn = due; end.SupersedesId = previous;
        Accept(end, db, f); db.Add(end); await db.SaveChangesAsync(); return end.Id;
    }
    private static void Accept<T>(T row, Tpr10DbContext db, FieldAdvanceFixture f) where T : class
    {
        var e = db.Entry(row); e.Property("State").CurrentValue = "Accepted";
        e.Property("AcceptedAtUtc").CurrentValue = f.Clock.GetUtcNow(); e.Property("ReviewedAtUtc").CurrentValue = f.Clock.GetUtcNow();
        e.Property("ReviewedBy").CurrentValue = ReviewerId; e.Property("ReviewedByPersonId").CurrentValue = ReviewerPersonId;
    }
    internal static async Task<Guid> SeedIncident(FieldAdvanceFixture f, Guid request, Guid evidence, decimal owner, decimal wrong)
    {
        await using var db = f.CreateContext(); var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request);
        var attempt = await db.Set<AdvancePaymentAttempt>().SingleAsync(x => x.RequestId == request);
        var hold = new AdvancePaymentHold
        {
            Id = Guid.NewGuid(),
            WorkspaceId = r.WorkspaceId,
            ProjectId = r.ProjectId,
            FamilyId = r.FamilyId,
            Code = "PaymentMismatch",
            Reason = "เหตุจำลอง",
            CreatedBy = AuthorId,
            CreatedByPersonId = AuthorPersonId,
            CreatedAtUtc = f.Clock.GetUtcNow()
        };
        db.Add(hold); await db.SaveChangesAsync();
        var incident = await Row<PaymentIncidentResolution>(f, db, r);
        incident.AttemptId = attempt.Id; incident.InstallmentId = attempt.InstallmentId; incident.HoldId = hold.Id; incident.EvidenceId = evidence;
        incident.OwnerReceived = owner; incident.WrongRecipientReceived = wrong; incident.WrongRecipientPersonId = RecipientPersonId;
        Accept(incident, db, f); db.Add(incident); await db.SaveChangesAsync(); return incident.Id;
    }
    private static async Task SeedRecovery(FieldAdvanceFixture f, Guid request, Guid evidence, Guid incident, string kind, Guid person, decimal amount)
    {
        await using var db = f.CreateContext(); var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request);
        var recovery = await Row<PaymentIncidentRecovery>(f, db, r); recovery.ResolutionId = incident; recovery.EvidenceId = evidence;
        recovery.ReturnKind = kind; recovery.ReturnedByPersonId = person; recovery.Amount = amount; recovery.ReturnedOn = new(2026, 10, 20);
        var proof = await db.Set<AdvanceEvidence>().SingleAsync(x => x.Id == evidence);
        recovery.SnapshotJson = JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            IncidentId = incident,
            Amount = amount,
            Source = kind,
            ReturnedByPersonId = person,
            EvidenceIds = new[] { evidence },
            Evidence = new[] { new RefundEvidenceSnapshot(evidence, proof.Sha256, proof.Length, proof.ContentType) }
        });
        Accept(recovery, db, f); db.Add(recovery); await db.SaveChangesAsync();
    }
    private static async Task<FinanceResult<SettlementCommandResult>> ExpenseFinal(FieldAdvanceFixture f, Guid request, Guid round,
        SettlementCommand command, FieldAdvancePaymentRaceTests.Barrier? barrier = null)
    {
        await using var db = f.CreateContext(); var actor = AliasAccountId; var session = await f.SessionAsync(actor);
        var policies = new PolicyResolver(db, FieldAdvanceAccessTests.Access(db, session, f.Clock), session, f.Clock);
        var service = new SettlementReviewService(db, await Access(f, db, actor), await Executor(f, db, actor, barrier?.Audit(f, true, db)),
            new(db, new(db, f.Clock), f.Clock), new(db, policies, f.Clock), f.Clock);
        return await service.ReviewAsync(actor, request, round, new("Approve", "รับรองค่าใช้จ่ายจำลอง"), command, default);
    }

    internal static RefundInput RefundInputFor(decimal amount, params Guid[] evidence) => new(amount, new(2026, 10, 20), "Transfer", "บัญชีบริษัทจำลอง", evidence, null);
    internal static async Task<(Guid Request, Guid Evidence)> RefundReady(FieldAdvanceFixture f, bool delegated = false)
    {
        var request = await FieldAdvanceSettlementDraftTests.Approved(f, delegated ? RecipientPersonId : null);
        await FieldAdvanceSettlementDraftTests.Document(f, request);
        await SeedPaid(f, request, 2000);
        await Grant(f, ReviewerId, ReviewerPersonId, "finance.check");
        if (delegated) await Grant(f, RecipientId, RecipientPersonId, "request.delegate");
        await using var db = f.CreateContext(); return (request, (await db.Set<AdvanceEvidence>().SingleAsync(x => x.RequestId == request)).Id);
    }
    internal static async Task<SettlementCommand> RefundMeta(FieldAdvanceFixture f, Guid request, Guid? id)
    {
        await using var db = f.CreateContext();
        var version = id is { } value ? (long?)(await db.Set<Refund>().SingleAsync(x => x.Id == value)).Version : null;
        return await Meta(f, request, version);
    }
    internal static async Task<FinanceResult<SettlementCommandResult>> RefundCall(FieldAdvanceFixture f, Guid request, string operation,
        Guid? id = null, RefundInput? input = null, Guid? actor = null, SettlementCommand? command = null, FieldAdvancePaymentRaceTests.Barrier? barrier = null)
    {
        var user = actor ?? AuthorId; await using var db = f.CreateContext();
        var executor = await Executor(f, db, user, barrier?.Audit(f, true, db)); var access = await Access(f, db, user);
        using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        var service = new RefundService(db, access, executor, f.Clock, files.Options); var meta = command ?? await RefundMeta(f, request, id);
        return operation switch
        {
            "Propose" => await service.ProposeAsync(user, request, input!, meta, default),
            "Update" => await service.UpdateAsync(user, request, id!.Value, input!, meta, default),
            "Confirm" => await service.ConfirmAsync(user, request, id!.Value, meta, default),
            "Cancel" => await service.CancelAsync(user, request, id!.Value, meta, default),
            _ => await new RefundReviewService(db, access, executor, f.Clock, files.Options).ReviewAsync(user, request, id!.Value, new(operation, "ตรวจผู้รับบริษัทและหลักฐานแล้ว"), meta, default)
        };
    }
    internal static async Task<SettlementTotals> Totals(FieldAdvanceFixture f, Guid request)
    {
        await using var db = f.CreateContext(); await using var tx = await db.Database.BeginTransactionAsync();
        Assert.True((await (await Access(f, db, AuthorId)).CheckAsync(AuthorId, request, SettlementActions.Read, null, default)).Allowed);
        var result = await SettlementBalanceReader.ReadLockedAsync(db, request, default); Assert.Equal(200, result.Status); return result.Value!;
    }

    internal static async Task SeedPaid(FieldAdvanceFixture f, Guid request, decimal amount)
    {
        await using var db = f.CreateContext();
        var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request);
        var receipt = FieldAdvancePaymentFixture.Receipt(); db.Add(receipt);
        var installment = new AdvanceInstallment
        {
            Id = Guid.NewGuid(),
            WorkspaceId = r.WorkspaceId,
            ProjectId = r.ProjectId,
            FamilyId = r.FamilyId,
            RequestId = request,
            CreatedBy = AuthorId,
            CreatedAtUtc = f.Clock.GetUtcNow()
        };
        db.Add(installment); await db.SaveChangesAsync();
        var revision = new AdvanceInstallmentRevision
        {
            Id = Guid.NewGuid(),
            WorkspaceId = r.WorkspaceId,
            ProjectId = r.ProjectId,
            FamilyId = r.FamilyId,
            RequestId = request,
            InstallmentId = installment.Id,
            RevisionNumber = 1,
            IsCurrent = true,
            Amount = amount,
            Channel = "Cash",
            ContentHash = new('a', 64),
            Reason = "ข้อมูลจำลอง",
            RecipientPersonId = AuthorPersonId,
            CreatedBy = AuthorId,
            CreatedAtUtc = f.Clock.GetUtcNow()
        };
        db.Add(revision); await db.SaveChangesAsync();
        var attempt = new AdvancePaymentAttempt
        {
            Id = Guid.NewGuid(),
            WorkspaceId = r.WorkspaceId,
            ProjectId = r.ProjectId,
            FamilyId = r.FamilyId,
            RequestId = request,
            InstallmentId = installment.Id,
            RevisionId = revision.Id,
            CommandId = receipt.CommandId,
            PayerId = AuthorId,
            PayerPersonId = AuthorPersonId
        };
        db.Add(attempt); await db.SaveChangesAsync();
        db.Add(new AdvancePaymentResult
        {
            Id = Guid.NewGuid(),
            WorkspaceId = r.WorkspaceId,
            ProjectId = r.ProjectId,
            FamilyId = r.FamilyId,
            RequestId = request,
            InstallmentId = installment.Id,
            AttemptId = attempt.Id,
            CommandId = receipt.CommandId,
            Amount = amount,
            RecipientPersonId = AuthorPersonId,
            PaidOn = new(2026, 10, 30),
            RecordedBy = AuthorId,
            RecordedByPersonId = AuthorPersonId,
            CreatedAtUtc = f.Clock.GetUtcNow()
        });
        await db.SaveChangesAsync();
    }
}
