using System.Text.Json;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Evidence;
using TPR10.Api.FieldAdvances.Payments;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvancePaymentRecoveryTests(PostgresFixture postgres)
{
    private static readonly Guid Checker = Guid.Parse("71000000-0000-0000-0000-000000000045");
    private static readonly Guid CheckerPerson = Guid.Parse("72000000-0000-0000-0000-000000000045");
    // Removing any outcome transition, or consuming twice, breaks these ledger assertions.
    [Theory]
    [InlineData("Matched", "Paid", 0, 3000)]
    [InlineData("Unpaid", "PendingReview", 3000, 0)]
    [InlineData("Unknown", "OutcomePending", 3000, 0)]
    public async Task ThreeOutcomesNeverAutoRetry(string outcome, string state, decimal reserved, decimal paid)
    {
        await using var f = await Ready(); using var files = new Files(); var (request, id) = await Reviewed(f);
        Value(await Call(f, request, id, "Start", files)); var actual = await Actual(f, files, request);
        Value(await Recover(f, request, id, "Outcome", "Propose", files, new PaymentOutcomeInput(outcome, outcome == "Matched" ? actual : null, actual.EvidenceIds)));
        var proposal = await Proposal(f, id, "PaymentOutcome"); var meta = await Meta(f, request, id);
        var view = Value(await Recover(f, request, id, "Outcome", "Confirm", files, proposal, meta));
        Assert.Equal(state, view.State); Assert.Equal(reserved, view.Reserved); Assert.Equal(paid, view.Paid);
        Assert.Equal(JsonSerializer.Serialize(view), JsonSerializer.Serialize(Value(await Recover(f, request, id, "Outcome", "Confirm", files, proposal, meta, forbidIO: true))));
        Assert.Equal(409, (await Call(f, request, id, "Start", files)).Status);
        await using var db = f.CreateContext();
        Assert.Equal(outcome == "Matched" ? 1 : 0, await db.Set<AdvancePaymentResult>().CountAsync());
        Assert.Equal(outcome == "Matched" ? 1 : 0, await db.Set<AdvanceReservationEntry>().CountAsync(x => x.Action == "Consume"));
        Assert.DoesNotContain(await db.Set<AdvancePaymentParticipation>().ToArrayAsync(), x => x.PersonId == CheckerPerson && x.Role == "Payer");
    }

    // An Unpaid confirmation must invalidate approval without erasing its immutable history.
    [Fact]
    public async Task UnpaidRequiresFreshReviewAndPreservesOldRevision()
    {
        await using var f = await Ready(); using var files = new Files(); var (request, id) = await Reviewed(f);
        Value(await Call(f, request, id, "Start", files)); var actual = await Actual(f, files, request);
        AdvanceInstallmentRevision old; int prepares;
        await using (var db = f.CreateContext()) { old = await db.Set<AdvanceInstallmentRevision>().SingleAsync(); prepares = await db.Set<AdvancePaymentEvent>().CountAsync(x => x.Kind == "prepare"); }
        Value(await Recover(f, request, id, "Outcome", "Propose", files, new PaymentOutcomeInput("Unpaid", null, actual.EvidenceIds)));
        Value(await Recover(f, request, id, "Outcome", "Confirm", files, await Proposal(f, id, "PaymentOutcome")));
        await using (var db = f.CreateContext())
        {
            var retained = await db.Set<AdvanceInstallmentRevision>().SingleAsync(x => x.Id == old.Id);
            Assert.False(retained.IsCurrent); Assert.Equal(old.ReviewedAtUtc, retained.ReviewedAtUtc); Assert.Equal(old.ReviewedBy, retained.ReviewedBy);
            var current = await db.Set<AdvanceInstallmentRevision>().SingleAsync(x => x.IsCurrent);
            Assert.NotEqual(old.Id, current.Id); Assert.Equal(old.ContentHash, current.ContentHash); Assert.Null(current.ReviewedAtUtc);
            Assert.Equal(prepares, await db.Set<AdvancePaymentEvent>().CountAsync(x => x.Kind == "prepare"));
        }
        Assert.Equal(409, (await Call(f, request, id, "Start", files)).Status);
        Value(await Prepare(f, request, id, "Approve")); Value(await Call(f, request, id, "Start", files));
        await using var final = f.CreateContext(); Assert.Equal(2, await final.Set<AdvancePaymentAttempt>().CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MismatchHoldsEntireFamily(bool paid)
    {
        await using var f = await Ready(); using var files = new Files(); var (request, id) = await Reviewed(f);
        var sibling = await Sibling(f, request); var siblingId = Value(await Prepare(f, sibling, null, "Create")).InstallmentId!.Value;
        Value(await Prepare(f, sibling, siblingId, "Submit")); Value(await Prepare(f, sibling, siblingId, "Approve"));
        var readyId = Value(await Prepare(f, sibling, null, "Create")).InstallmentId!.Value;
        Value(await Prepare(f, sibling, readyId, "Submit")); Value(await Prepare(f, sibling, readyId, "Approve"));
        Value(await Call(f, sibling, siblingId, "Start", files)); var siblingActual = await Actual(f, files, sibling);
        Value(await Call(f, request, id, "Start", files)); var actual = await Actual(f, files, request);
        if (paid) { Value(await Call(f, request, id, "Record", files, actual)); Value(await Call(f, sibling, siblingId, "Record", files, siblingActual)); }
        var observed = actual with { Amount = "3100", RecipientPersonId = RecipientPersonId };
        var view = Value(await Recover(f, request, id, "Hold", "ReportMismatch", files, observed));
        Assert.Contains("PaymentMismatch", view.HoldCodes); Assert.Equal(paid ? 3000 : 0, view.Paid); Assert.Equal(paid ? 0 : 3000, view.Reserved);
        Assert.Equal(409, (await Call(f, sibling, readyId, "Start", files)).Status);
        var siblingView = Value(await Recover(f, sibling, siblingId, "Hold", "ReportMismatch", files, siblingActual with { Amount = "3100" }));
        Assert.Contains("PaymentMismatch", siblingView.HoldCodes);
        // Reporting on an already-started sibling returns the same family hold; no request-local hold.
        await using var db = f.CreateContext(); var holds = await db.Set<AdvancePaymentHold>().ToArrayAsync(); Assert.Equal(2, holds.Length);
        Assert.All(holds, hold => { Assert.Null(hold.RequestId); Assert.Null(hold.InstallmentId); Assert.Null(hold.ReleasedAtUtc); });
        var incident = Assert.Single(await db.Set<AdvancePaymentProposal>().Where(x => x.Kind == "PaymentMismatch" && x.RequestId == request).ToArrayAsync());
        Assert.Contains("3100", incident.AfterJson); Assert.Contains(RecipientPersonId.ToString(), incident.AfterJson);
    }

    [Fact]
    public async Task TakeoverPreservesOriginalPayerAndRejectsBothNormalRecordsAndStaleProposal()
    {
        await using var f = await Ready(); using var files = new Files(); var (request, id) = await Reviewed(f);
        await OtherPayer(f, false); Value(await Call(f, request, id, "Start", files)); var actual = await Actual(f, files, request);
        Value(await Recover(f, request, id, "Outcome", "Propose", files, new PaymentOutcomeInput("Matched", actual, actual.EvidenceIds)));
        var stale = await Proposal(f, id, "PaymentOutcome");
        Value(await Recover(f, request, id, "Takeover", "Propose", files, actual.EvidenceIds, actor: AliasAccountId));
        Value(await Recover(f, request, id, "Takeover", "Confirm", files, await Proposal(f, id, "PaymentTakeover")));
        Assert.Equal(409, (await Call(f, request, id, "Record", files, actual)).Status);
        Assert.Equal(409, (await Call(f, request, id, "Record", files, actual, actor: AliasAccountId)).Status);
        Assert.Equal(409, (await Recover(f, request, id, "Outcome", "Confirm", files, stale)).Status);
        Assert.Equal(409, (await Recover(f, request, id, "Outcome", "Propose", files, new PaymentOutcomeInput("Unpaid", null, actual.EvidenceIds))).Status);
        Value(await Recover(f, request, id, "Outcome", "Propose", files, new PaymentOutcomeInput("Matched", actual, actual.EvidenceIds), actor: AliasAccountId));
        Value(await Recover(f, request, id, "Outcome", "Confirm", files, await Proposal(f, id, "PaymentOutcome")));
        await using var db = f.CreateContext(); var attempt = Assert.Single(await db.Set<AdvancePaymentAttempt>().ToArrayAsync());
        Assert.Equal(RecipientId, attempt.PayerId); Assert.Equal(RecipientPersonId, attempt.PayerPersonId);
        Assert.Equal(3000, (await db.Set<AdvancePaymentAccount>().SingleAsync()).Paid);
    }

    // A reason or scope change must invalidate the proposal digest, just like changed payment data.
    [Theory]
    [InlineData("Reason")]
    [InlineData("EvidenceIds")]
    public async Task ProposalSnapshotCannotBeRewrittenBeforeConfirmation(string field)
    {
        await using var f = await Ready(); using var files = new Files(); var (request, id) = await Reviewed(f);
        Value(await Call(f, request, id, "Start", files)); var actual = await Actual(f, files, request);
        Value(await Recover(f, request, id, "Outcome", "Propose", files, new PaymentOutcomeInput("Matched", actual, actual.EvidenceIds)));
        var proposal = await Proposal(f, id, "PaymentOutcome");
        await using (var db = f.CreateContext())
        {
            var row = await db.Set<AdvancePaymentProposal>().SingleAsync(x => x.Id == proposal);
            if (field == "Reason") row.Reason = "เปลี่ยนเหตุผลภายหลัง"; else row.EvidenceIdsJson = "[]";
            await db.SaveChangesAsync();
        }
        Assert.Equal(409, (await Recover(f, request, id, "Outcome", "Confirm", files, proposal)).Status);
        await Pending(f, request);
    }

    [Theory]
    [InlineData("Outcome", false)]
    [InlineData("Outcome", true)]
    [InlineData("Takeover", false)]
    [InlineData("Takeover", true)]
    public async Task CurrentRightsRequiredAtConfirmation(string service, bool revokeProposer)
    {
        await using var f = await Ready(); using var files = new Files(); var (request, id) = await Reviewed(f);
        await OtherPayer(f, false); Value(await Call(f, request, id, "Start", files)); var actual = await Actual(f, files, request);
        var proposer = service == "Takeover" ? AliasAccountId : RecipientId;
        object input = service == "Takeover" ? actual.EvidenceIds : new PaymentOutcomeInput("Matched", actual, actual.EvidenceIds);
        Value(await Recover(f, request, id, service, "Propose", files, input, actor: proposer));
        var proposal = await Proposal(f, id, "Payment" + service);
        await using (var db = f.CreateContext())
        {
            var user = revokeProposer ? proposer : Checker;
            foreach (var grant in await db.Set<GrantProposal>().Where(x => x.RecipientId == user).ToArrayAsync()) grant.EndsAtUtc = f.Clock.GetUtcNow();
            await db.SaveChangesAsync();
        }
        Assert.Equal(403, (await Recover(f, request, id, service, "Confirm", files, proposal)).Status);
        await Pending(f, request);
        Assert.Equal(403, (await Recover(f, request, id, service, "Confirm", files, proposal, actor: proposer)).Status);
    }

    [Fact]
    public async Task ConfirmationReplayRechecksCheckerRightsWithoutFileIO()
    {
        await using var f = await Ready(); using var files = new Files(); var (request, id) = await Reviewed(f);
        Value(await Call(f, request, id, "Start", files)); var actual = await Actual(f, files, request);
        Value(await Recover(f, request, id, "Outcome", "Propose", files, new PaymentOutcomeInput("Matched", actual, actual.EvidenceIds)));
        var proposal = await Proposal(f, id, "PaymentOutcome"); var meta = await Meta(f, request, id);
        Value(await Recover(f, request, id, "Outcome", "Confirm", files, proposal, meta)); files.Remove();
        // A committed receipt rechecks the caller, without re-running the additional participant validator.
        await Revoke(f);
        Value(await Recover(f, request, id, "Outcome", "Confirm", files, proposal, meta, forbidIO: true));
        await using (var db = f.CreateContext())
        {
            foreach (var grant in await db.Set<GrantProposal>().Where(x => x.RecipientId == Checker).ToArrayAsync()) grant.EndsAtUtc = f.Clock.GetUtcNow();
            await db.SaveChangesAsync();
        }
        Assert.Equal(403, (await Recover(f, request, id, "Outcome", "Confirm", files, proposal, meta, forbidIO: true)).Status);
    }

    [Fact]
    public async Task MissingEvidenceKeepsRealPaymentReservedAndMovedEvidenceStillConfirms()
    {
        await using var f = await Ready(); using var files = new Files(); var (request, id) = await Reviewed(f);
        Value(await Call(f, request, id, "Start", files)); var actual = await Actual(f, files, request);
        Value(await Recover(f, request, id, "Outcome", "Propose", files, new PaymentOutcomeInput("Matched", actual, actual.EvidenceIds)));
        var proposal = await Proposal(f, id, "PaymentOutcome"); files.Remove();
        Assert.Equal(409, (await Recover(f, request, id, "Outcome", "Confirm", files, proposal)).Status); await Pending(f, request);
        var key = Guid.NewGuid().ToString("N"); await files.Store.WriteImmutableAsync(files.Options.ActiveStorageId, key, new MemoryStream([1, 2, 3]), default);
        await using (var db = f.CreateContext())
        {
            var location = await db.Set<AdvanceEvidenceLocation>().SingleAsync(); location.IsCurrent = false; await db.SaveChangesAsync();
            db.Add(new AdvanceEvidenceLocation
            {
                Id = Guid.NewGuid(),
                WorkspaceId = location.WorkspaceId,
                ProjectId = location.ProjectId,
                FamilyId = location.FamilyId,
                RequestId = request,
                EvidenceId = location.EvidenceId,
                IsCurrent = true,
                StorageKey = new EvidenceLocator(files.Options.ActiveStorageId, key).ToString(),
                VerifiedAtUtc = f.Clock.GetUtcNow(),
                CreatedAtUtc = f.Clock.GetUtcNow()
            });
            await db.SaveChangesAsync();
        }
        Assert.Equal("Paid", Value(await Recover(f, request, id, "Outcome", "Confirm", files, proposal)).State);
    }

    [Fact]
    public async Task ThreeOutcomesRaceOnlyOneProposalAndConfirmationWins()
    {
        await using var f = await Ready(); using var files = new Files(); var (request, id) = await Reviewed(f);
        Value(await Call(f, request, id, "Start", files)); var actual = await Actual(f, files, request); var meta = await Meta(f, request, id);
        var results = await Task.WhenAll(new[] { "Matched", "Unpaid", "Unknown" }.Select(outcome => Recover(f, request, id, "Outcome", "Propose", files,
            new PaymentOutcomeInput(outcome, outcome == "Matched" ? actual : null, actual.EvidenceIds), meta with { CommandId = Guid.NewGuid() })));
        Assert.Single(results, x => x.Status == 200); Assert.Equal(2, results.Count(x => x.Status == 409));
        var proposal = await Proposal(f, id, "PaymentOutcome"); meta = await Meta(f, request, id);
        var confirms = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Recover(f, request, id, "Outcome", "Confirm", files, proposal, meta with { CommandId = Guid.NewGuid() })));
        Assert.Single(confirms, x => x.Status == 200); Assert.Equal(2, confirms.Count(x => x.Status == 409));
        await using var db = f.CreateContext(); Assert.InRange(await db.Set<AdvancePaymentResult>().CountAsync(), 0, 1);
        Assert.Equal(3000, (await db.Set<AdvancePaymentAccount>().SingleAsync()).Reserved + (await db.Set<AdvancePaymentAccount>().SingleAsync()).Paid);
    }

    [Theory]
    [InlineData("Matched")]
    [InlineData("Unknown")]
    public async Task ObservedMismatchInOutcomeMetadataHoldsImmediatelyAndNeverConsumes(string outcome)
    {
        await using var f = await Ready(); using var files = new Files(); var (request, id) = await Reviewed(f);
        Value(await Call(f, request, id, "Start", files)); var actual = (await Actual(f, files, request)) with { Amount = "2999.99" };
        var view = Value(await Recover(f, request, id, "Outcome", "Propose", files, new PaymentOutcomeInput(outcome, actual, actual.EvidenceIds)));
        Assert.Contains("PaymentMismatch", view.HoldCodes); await Pending(f, request);
        view = Value(await Recover(f, request, id, "Outcome", "Confirm", files, await Proposal(f, id, "PaymentOutcome")));
        Assert.Equal("OutcomePending", view.State); Assert.Contains("PaymentMismatch", view.HoldCodes); await Pending(f, request);
    }

    [Fact]
    public async Task StackedHoldsSurviveTakeoverAndMatchedReconciliation()
    {
        await using var f = await Ready(); using var files = new Files(); var (request, id) = await Reviewed(f);
        await OtherPayer(f, false); Value(await Call(f, request, id, "Start", files)); var actual = await Actual(f, files, request);
        await using (var db = f.CreateContext())
        {
            var row = await db.Set<AdvanceInstallment>().SingleAsync();
            db.Add(new AdvancePaymentHold
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                ProjectId = ProjectId,
                FamilyId = row.FamilyId,
                Code = "OtherHold",
                Reason = "พักอีกเหตุ",
                CreatedBy = Checker,
                CreatedByPersonId = CheckerPerson,
                CreatedAtUtc = f.Clock.GetUtcNow()
            }); await db.SaveChangesAsync();
        }
        Value(await Recover(f, request, id, "Hold", "ReportMismatch", files, actual with { Amount = "3100" }));
        Value(await Recover(f, request, id, "Takeover", "Propose", files, actual.EvidenceIds, actor: AliasAccountId));
        var view = Value(await Recover(f, request, id, "Takeover", "Confirm", files, await Proposal(f, id, "PaymentTakeover")));
        Assert.Contains("PaymentMismatch", view.HoldCodes); Assert.Contains("OtherHold", view.HoldCodes);
        Value(await Recover(f, request, id, "Outcome", "Propose", files, new PaymentOutcomeInput("Matched", actual, actual.EvidenceIds), actor: AliasAccountId));
        view = Value(await Recover(f, request, id, "Outcome", "Confirm", files, await Proposal(f, id, "PaymentOutcome")));
        Assert.Equal("Paid", view.State); Assert.Contains("PaymentMismatch", view.HoldCodes); Assert.Contains("OtherHold", view.HoldCodes);
    }

    [Theory]
    [InlineData("empty-evidence")]
    [InlineData("duplicate-evidence")]
    [InlineData("foreign-evidence")]
    [InlineData("missing-file")]
    [InlineData("zero")]
    [InlineData("precision")]
    [InlineData("overflow")]
    [InlineData("date")]
    [InlineData("unknown-outcome")]
    [InlineData("reason")]
    public async Task InvalidRecoveryInputsDoNotChangeMoneyOrCreateProposal(string defect)
    {
        await using var f = await Ready(); using var files = new Files(); var (request, id) = await Reviewed(f);
        Value(await Call(f, request, id, "Start", files)); var actual = await Actual(f, files, request); var meta = await Meta(f, request, id);
        if (defect == "zero") actual = actual with { Amount = "0" };
        if (defect == "precision") actual = actual with { Amount = "3000.001" };
        if (defect == "overflow") actual = actual with { Amount = "999999999999999999999999999999999" };
        if (defect == "date") actual = actual with { PaidOn = DateOnly.MaxValue };
        if (defect == "foreign-evidence") actual = await Actual(f, files, await new FieldAdvancePaymentFixture(f).SeedApprovedAsync(10000, default));
        if (defect == "missing-file") files.Remove();
        if (defect == "reason") meta = meta with { Reason = " " };
        var ids = defect == "empty-evidence" ? [] : defect == "duplicate-evidence" ? new[] { actual.EvidenceIds[0], actual.EvidenceIds[0] } : actual.EvidenceIds;
        var input = new PaymentOutcomeInput(defect == "unknown-outcome" ? "Retry" : "Matched", actual, ids);
        Assert.Equal(400, (await Recover(f, request, id, "Outcome", "Propose", files, input, meta)).Status);
        await Pending(f, request); await using var db = f.CreateContext();
        Assert.False(await db.Set<AdvancePaymentProposal>().AnyAsync(x => x.Kind == "PaymentOutcome"));
        Assert.False(await db.Set<FinanceCommandReceipt>().AnyAsync(x => x.CommandId == meta.CommandId));
    }

    [Theory]
    [InlineData("State")]
    [InlineData("Digest")]
    [InlineData("Length")]
    [InlineData("Installment")]
    public async Task EvidenceMetadataRaceBeforeConfirmationCommitIsRejected(string mutation)
    {
        await using var f = await Ready(); using var files = new Files(); var (request, id) = await Reviewed(f);
        Value(await Call(f, request, id, "Start", files)); var actual = await Actual(f, files, request);
        var other = Value(await Prepare(f, request, null, "Create")).InstallmentId!.Value;
        Value(await Recover(f, request, id, "Outcome", "Propose", files, new PaymentOutcomeInput("Matched", actual, actual.EvidenceIds)));
        var result = await Recover(f, request, id, "Outcome", "Confirm", files, await Proposal(f, id, "PaymentOutcome"), beforeBody: async () =>
        {
            await using var db = f.CreateContext(); var row = await db.Set<AdvanceEvidence>().SingleAsync();
            if (mutation == "State") row.State = "Missing";
            if (mutation == "Digest") row.Sha256 = new('f', 64);
            if (mutation == "Length") row.Length++;
            if (mutation == "Installment") row.InstallmentId = other;
            await db.SaveChangesAsync();
        });
        Assert.Equal(409, result.Status); await Pending(f, request);
    }

    [Fact]
    public async Task OriginalPayerCanLoseRightsBeforeTakeoverAndNewOwnerMustReviewBeforeFreshStart()
    {
        await using var f = await Ready(); using var files = new Files(); var (request, id) = await Reviewed(f);
        await OtherPayer(f, false); Value(await Call(f, request, id, "Start", files)); var actual = await Actual(f, files, request); await Revoke(f);
        Value(await Recover(f, request, id, "Takeover", "Propose", files, actual.EvidenceIds, actor: AliasAccountId));
        var proposal = await Proposal(f, id, "PaymentTakeover"); var meta = await Meta(f, request, id);
        Value(await Recover(f, request, id, "Takeover", "Confirm", files, proposal, meta));
        Value(await Recover(f, request, id, "Takeover", "Confirm", files, proposal, meta, forbidIO: true));
        Assert.Equal(409, (await Call(f, request, id, "Start", files, actor: AliasAccountId)).Status);
        Value(await Recover(f, request, id, "Outcome", "Propose", files, new PaymentOutcomeInput("Unpaid", null, actual.EvidenceIds), actor: AliasAccountId));
        Value(await Recover(f, request, id, "Outcome", "Confirm", files, await Proposal(f, id, "PaymentOutcome")));
        Assert.Equal(409, (await Call(f, request, id, "Start", files, actor: AliasAccountId)).Status);
        Value(await Prepare(f, request, id, "Approve")); Value(await Call(f, request, id, "Start", files, actor: AliasAccountId));
        Value(await Call(f, request, id, "Record", files, actual, actor: AliasAccountId));
        await using var db = f.CreateContext(); var attempts = await db.Set<AdvancePaymentAttempt>().ToArrayAsync();
        Assert.Equal(2, attempts.Length); Assert.Contains(attempts, x => x.PayerId == RecipientId && x.State == "Unpaid");
        Assert.Contains(attempts, x => x.PayerId == AliasAccountId && x.State == "Matched");
    }

    [Theory]
    [InlineData("Outcome", "Matched")]
    [InlineData("Outcome", "Unpaid")]
    [InlineData("Takeover", "Unknown")]
    public async Task ConfirmationCommitFailureRollsBackAndSameCommandCanRetry(string service, string outcome)
    {
        await using var f = await Ready(); using var files = new Files(); var (request, id) = await Reviewed(f);
        await OtherPayer(f, false); Value(await Call(f, request, id, "Start", files)); var actual = await Actual(f, files, request);
        object input = service == "Outcome" ? new PaymentOutcomeInput(outcome, outcome == "Matched" ? actual : null, actual.EvidenceIds) : actual.EvidenceIds;
        Value(await Recover(f, request, id, service, "Propose", files, input, actor: service == "Outcome" ? RecipientId : AliasAccountId));
        var proposal = await Proposal(f, id, "Payment" + service); var meta = await Meta(f, request, id);
        await using (var db = f.CreateContext()) await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION recovery_fail() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'recovery test commit failure'; END; $$;
            CREATE CONSTRAINT TRIGGER recovery_fail AFTER INSERT ON field_advance_payment_events DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION recovery_fail();
            """);
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Recover(f, request, id, service, "Confirm", files, proposal, meta));
        Assert.Equal("P0001", error.SqlState); await Pending(f, request);
        await using (var db = f.CreateContext())
        {
            Assert.Equal("Pending", (await db.Set<AdvancePaymentProposal>().SingleAsync(x => x.Id == proposal)).State);
            Assert.Single(await db.Set<AdvanceInstallmentRevision>().ToArrayAsync());
            Assert.True((await db.Set<AdvanceInstallmentRevision>().SingleAsync()).IsCurrent);
            Assert.False(await db.Set<FinanceCommandReceipt>().AnyAsync(x => x.CommandId == meta.CommandId));
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER recovery_fail ON field_advance_payment_events");
        }
        Value(await Recover(f, request, id, service, "Confirm", files, proposal, meta));
    }

    [Fact]
    public async Task InvalidApprovedTakeoverCannotRestoreOriginalPayerNormalRecord()
    {
        await using var f = await Ready(); using var files = new Files(); var (request, id) = await Reviewed(f);
        await OtherPayer(f, false); Value(await Call(f, request, id, "Start", files)); var actual = await Actual(f, files, request);
        Value(await Recover(f, request, id, "Takeover", "Propose", files, actual.EvidenceIds, actor: AliasAccountId));
        var proposal = await Proposal(f, id, "PaymentTakeover");
        Value(await Recover(f, request, id, "Takeover", "Confirm", files, proposal));
        await using (var db = f.CreateContext())
        {
            (await db.Set<AdvancePaymentProposal>().SingleAsync(x => x.Id == proposal)).Reason = "เหตุผลที่ไม่ตรง snapshot";
            await db.SaveChangesAsync();
        }
        Assert.Equal(409, (await Call(f, request, id, "Record", files, actual)).Status);
        await Pending(f, request);
    }

    [Fact]
    public async Task TakeoverConfirmationAndOriginalRecordRaceSerializeToOneWinner()
    {
        await using var f = await Ready(); using var files = new Files(); var (request, id) = await Reviewed(f);
        await OtherPayer(f, false); Value(await Call(f, request, id, "Start", files)); var actual = await Actual(f, files, request);
        Value(await Recover(f, request, id, "Takeover", "Propose", files, actual.EvidenceIds, actor: AliasAccountId));
        var proposal = await Proposal(f, id, "PaymentTakeover"); var meta = await Meta(f, request, id);
        var results = await Task.WhenAll(Recover(f, request, id, "Takeover", "Confirm", files, proposal, meta),
            Call(f, request, id, "Record", files, actual, meta with { CommandId = Guid.NewGuid() }));
        Assert.Single(results, x => x.Status == 200); Assert.Single(results, x => x.Status == 409);
        await using var db = f.CreateContext(); var attempt = Assert.Single(await db.Set<AdvancePaymentAttempt>().ToArrayAsync());
        Assert.Equal(RecipientId, attempt.PayerId); Assert.Equal(RecipientPersonId, attempt.PayerPersonId);
        if (results[0].Status == 200)
        {
            await Pending(f, request);
            Assert.Equal(409, (await Call(f, request, id, "Record", files, actual)).Status);
        }
        else Assert.Single(await db.Set<AdvancePaymentResult>().ToArrayAsync());
    }

    // Both hooks occur after CurrentPayerAsync: the latter also proves a body-end check is insufficient.
    [Theory]
    [InlineData("Outcome", "Matched", false)]
    [InlineData("Outcome", "Unpaid", false)]
    [InlineData("Outcome", "Unknown", false)]
    [InlineData("Takeover", "Unknown", false)]
    [InlineData("Outcome", "Matched", true)]
    [InlineData("Outcome", "Unpaid", true)]
    [InlineData("Outcome", "Unknown", true)]
    [InlineData("Takeover", "Unknown", true)]
    public async Task ProposerExpiryDuringConfirmationReturns403AndRollsBackEverything(string service, string outcome, bool afterPersistence)
    {
        await using var f = await Ready(); using var files = new Files(); var (request, id) = await Reviewed(f);
        await OtherPayer(f, false); Value(await Call(f, request, id, "Start", files)); var actual = await Actual(f, files, request);
        var proposer = service == "Takeover" ? AliasAccountId : RecipientId;
        object input = service == "Takeover" ? actual.EvidenceIds : new PaymentOutcomeInput(outcome, outcome == "Matched" ? actual : null, actual.EvidenceIds);
        Value(await Recover(f, request, id, service, "Propose", files, input, actor: proposer));
        var proposal = await Proposal(f, id, "Payment" + service); var meta = await Meta(f, request, id);
        await using (var db = f.CreateContext())
        {
            foreach (var grant in await db.Set<GrantProposal>().Where(x => x.RecipientId == proposer).ToArrayAsync())
                grant.EndsAtUtc = f.Clock.GetUtcNow().AddSeconds(1);
            await db.SaveChangesAsync();
        }
        var before = await RecoveryState(f); var advanced = false;
        void Expire() { advanced = true; f.Clock.Advance(TimeSpan.FromSeconds(1)); }
        var result = await Recover(f, request, id, service, "Confirm", files, proposal, meta,
            beforeEvidenceLock: afterPersistence ? null : Expire, afterPersistence: afterPersistence ? Expire : null);
        Assert.True(advanced, "The clock must advance after the initial proposer authorization succeeds");
        Assert.Equal(403, result.Status);
        Assert.Equal(before, await RecoveryState(f));
        await Pending(f, request);
    }

    private static async Task<string> RecoveryState(FieldAdvanceFixture f)
    {
        await using var db = f.CreateContext();
        var audit = db.AuditEvents.Where(x => x.EventType.StartsWith("field-advance.payment."));
        return JsonSerializer.Serialize(new
        {
            Families = await db.Set<AdvanceFamily>().OrderBy(x => x.Id).ToArrayAsync(),
            Requests = await db.Set<AdvanceRequest>().OrderBy(x => x.Id).ToArrayAsync(),
            Accounts = await db.Set<AdvancePaymentAccount>().OrderBy(x => x.Id).ToArrayAsync(),
            Installments = await db.Set<AdvanceInstallment>().OrderBy(x => x.Id).ToArrayAsync(),
            Revisions = await db.Set<AdvanceInstallmentRevision>().OrderBy(x => x.Id).ToArrayAsync(),
            Attempts = await db.Set<AdvancePaymentAttempt>().OrderBy(x => x.Id).ToArrayAsync(),
            Results = await db.Set<AdvancePaymentResult>().OrderBy(x => x.Id).ToArrayAsync(),
            Proposals = await db.Set<AdvancePaymentProposal>().OrderBy(x => x.Id).ToArrayAsync(),
            Entries = await db.Set<AdvanceReservationEntry>().OrderBy(x => x.Id).ToArrayAsync(),
            Participation = await db.Set<AdvancePaymentParticipation>().OrderBy(x => x.Id).ToArrayAsync(),
            Holds = await db.Set<AdvancePaymentHold>().OrderBy(x => x.Id).ToArrayAsync(),
            Evidence = await db.Set<AdvanceEvidence>().OrderBy(x => x.Id).ToArrayAsync(),
            Locations = await db.Set<AdvanceEvidenceLocation>().OrderBy(x => x.Id).ToArrayAsync(),
            Receipts = await db.Set<FinanceCommandReceipt>().OrderBy(x => x.CommandId).ToArrayAsync(),
            Events = await db.Set<AdvancePaymentEvent>().OrderBy(x => x.Id).ToArrayAsync(),
            Audit = await audit.OrderBy(x => x.Id).ToArrayAsync(),
            AuditMetadata = await db.AuditMetadata.Where(x => audit.Select(a => a.Id).Contains(x.AuditEventId)).OrderBy(x => x.Id).ToArrayAsync()
        });
    }

    private static async Task Pending(FieldAdvanceFixture f, Guid request)
    {
        await using var db = f.CreateContext(); var account = await db.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == request);
        Assert.Equal(3000, account.Reserved); Assert.Equal(0, account.Paid); Assert.False(account.EverPaid);
        Assert.Empty(await db.Set<AdvancePaymentResult>().Where(x => x.RequestId == request).ToArrayAsync());
    }

    private static async Task<Guid> Sibling(FieldAdvanceFixture f, Guid root)
    {
        await using var db = f.CreateContext();
        var family = (await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == root)).FamilyId;
        var request = new AdvanceRequest { Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, ProjectId = ProjectId, OwnerPersonId = AuthorPersonId, CreatedBy = AuthorId, State = "Approved", FamilyId = family, RelationKind = "Supplement", SupplementOfRequestId = root };
        var revision = new AdvanceRequestRevision
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            RequestId = request.Id,
            RevisionNumber = 1,
            IsCurrent = true,
            ContentJson = "{}",
            ContentHash = new('a', 64),
            Total = 10000m,
            ApprovalBasisAmount = 20000m,
            ApprovalBasisJson = "{\"Amount\":20000,\"Items\":[]}",
            CreatedBy = AuthorId,
            CreatedByPersonId = AuthorPersonId,
            CompanyPolicyRevisionId = CompanyRevisionId,
            PolicySnapshotJson = "{}",
            PolicyHash = new('b', 64)
        };
        db.AddRange(request, revision);
        var step = new AdvanceApprovalStep
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            RequestId = request.Id,
            RevisionId = revision.Id,
            StepOrder = 1,
            LevelCode = "Supervisor",
            AssigneePersonId = ReviewerPersonId
        };
        db.Add(step); await db.SaveChangesAsync();
        revision.SubmittedAtUtc = DateTimeOffset.UtcNow; revision.ConfirmedByPersonId = AuthorPersonId;
        await db.SaveChangesAsync();
        step.Outcome = "Approved"; step.DecidedBy = ReviewerId; step.DecidedByPersonId = ReviewerPersonId;
        step.DecidedAtUtc = DateTimeOffset.UtcNow; step.DecisionReason = "Test approval";
        var account = await db.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == request.Id);
        account.ApprovedRevisionId = revision.Id; account.Approved = 10000m; account.Spendable = 10000m;
        await db.SaveChangesAsync();
        return request.Id;
    }
    private static async Task<Guid> Proposal(FieldAdvanceFixture f, Guid id, string kind)
    {
        await using var db = f.CreateContext(); return (await db.Set<AdvancePaymentProposal>().Where(x => x.InstallmentId == id && x.Kind == kind)
            .OrderByDescending(x => x.InstallmentVersion).FirstAsync()).Id;
    }
    private static async Task<FinanceResult<PaymentView>> Recover(FieldAdvanceFixture f, Guid request, Guid id, string serviceName, string operation,
        Files files, object input, PaymentCommand? meta = null, Guid? actor = null, bool forbidIO = false, Func<Task>? beforeBody = null,
        Action? beforeEvidenceLock = null, Action? afterPersistence = null)
    {
        var user = actor ?? (operation == "Confirm" ? Checker : RecipientId); var session = await f.SessionAsync(user);
        meta ??= await Meta(f, request, id);
        await using var db = new Tpr10DbContext(new DbContextOptionsBuilder<Tpr10DbContext>().UseNpgsql(f.Database.ConnectionString)
            .AddInterceptors(new BeforeBody(beforeBody), new BeforeEvidenceLock(beforeEvidenceLock), new AfterConfirmationPersistence(meta.CommandId, afterPersistence)).Options); var access = FieldAdvanceAccessTests.Access(db, session, f.Clock); var people = new FinancePersonResolver(db, f.Clock);
        var paymentAccess = new PaymentAccess(db, access, people, new(db, access, people, f.Clock), f.Clock);
        var executor = new PaymentCommandExecutor(db, paymentAccess, new(db, session, access, people, f.Clock), new AuditEventWriter(db, new CorrelationContext(), f.Clock), f.Clock);
        var evidence = new FinanceEvidenceService(db, paymentAccess, new ObservedStore(files.Store, db, forbidIO), new RejectPdf(), files.Options, f.Clock);
        meta ??= await Meta(f, request, id);
        var outcome = new PaymentOutcomeService(db, executor, new(db, f.Clock), people, evidence, files.Options, f.Clock);
        var takeover = new PaymentTakeoverService(db, executor, people, evidence, files.Options, f.Clock);
        return (serviceName, operation) switch
        {
            ("Outcome", "Propose") => await outcome.ProposeAsync(user, id, (PaymentOutcomeInput)input, meta, default),
            ("Outcome", "Confirm") => await outcome.ConfirmAsync(user, (Guid)input, meta, default),
            ("Takeover", "Propose") => await takeover.ProposeAsync(user, id, (Guid[])input, meta, default),
            ("Takeover", "Confirm") => await takeover.ConfirmAsync(user, (Guid)input, meta, default),
            ("Hold", "ReportMismatch") => await new PaymentHoldService(db, executor, people, evidence, files.Options, f.Clock)
                .ReportMismatchAsync(user, id, (ActualPaymentInput)input, meta, default),
            _ => throw new InvalidOperationException("Unknown test operation")
        };
    }
    private static PaymentView Value(FinanceResult<PaymentView> result)
    { Assert.Equal(200, result.Status); return Assert.IsType<PaymentView>(result.Value); }
    private async Task<FieldAdvanceFixture> Ready()
    {
        var f = await CreateAsync(postgres); await f.SeedPoliciesAsync(); await Grant(f, RecipientId, RecipientPersonId, "payment.record");
        await using (var db = f.CreateContext())
        {
            await db.Database.ExecuteSqlAsync($"INSERT INTO users(id,username,normalized_username) VALUES ({Checker},'execution-checker','EXECUTION-CHECKER')");
            await db.Database.ExecuteSqlAsync($"INSERT INTO employee_memberships(id,user_id,workspace_id,department_id,valid_from_utc,created_by,reason) SELECT gen_random_uuid(),{Checker},workspace_id,department_id,valid_from_utc,created_by,'test checker' FROM employee_memberships WHERE user_id={RecipientId}");
            db.Add(new FinancePerson { Id = CheckerPerson, WorkspaceId = WorkspaceId, CreatedBy = AuthorId, Reason = "test checker" });
            db.Add(new FinanceAccountLink { Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, UserId = Checker, PersonId = CheckerPerson, CreatedBy = AuthorId, CreatedAtUtc = f.Clock.GetUtcNow().AddMinutes(-1), Reason = "test checker" });
            await db.SaveChangesAsync();
        }
        await Grant(f, Checker, CheckerPerson, "finance.check"); return f;
    }
    private static async Task Grant(FieldAdvanceFixture f, Guid actor, Guid person, string capability)
    {
        var id = await FieldAdvanceGrantTests.AuthorityAsync(f, actor, person); await using var db = f.CreateContext();
        var assignment = await db.Set<GrantAssignment>().SingleAsync(x => x.Id == id);
        (await db.Set<GrantProposal>().SingleAsync(x => x.Id == assignment.ProposalId)).Capabilities = [capability]; await db.SaveChangesAsync();
    }
    private static async Task OtherPayer(FieldAdvanceFixture f, bool samePerson)
    {
        var person = samePerson ? RecipientPersonId : Guid.NewGuid(); await using var db = f.CreateContext();
        if (!samePerson) db.Add(new FinancePerson { Id = person, WorkspaceId = WorkspaceId, CreatedBy = AuthorId, Reason = "test other payer" });
        db.Add(new FinanceAccountLink { Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, UserId = AliasAccountId, PersonId = person, CreatedBy = AuthorId, CreatedAtUtc = f.Clock.GetUtcNow().AddMinutes(-1), Reason = "test other account" });
        await db.SaveChangesAsync(); await Grant(f, AliasAccountId, person, "payment.record");
    }
    private static async Task Revoke(FieldAdvanceFixture f)
    { await using var db = f.CreateContext(); (await db.Set<GrantProposal>().SingleAsync(x => x.RecipientId == RecipientId)).EndsAtUtc = f.Clock.GetUtcNow(); await db.SaveChangesAsync(); }
    private static async Task<PaymentCommand> Meta(FieldAdvanceFixture f, Guid request, Guid? id)
    {
        await using var db = f.CreateContext(); var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request);
        return new(Guid.NewGuid(), (await db.Set<AdvanceFamily>().SingleAsync(x => x.Id == r.FamilyId)).Version, r.Version,
            id is null ? null : (await db.Set<AdvanceInstallment>().SingleAsync(x => x.Id == id)).Version, "ตรวจสอบผลการจ่าย");
    }
    private static async Task<(Guid, Guid)> Reviewed(FieldAdvanceFixture f)
    {
        var request = await new FieldAdvancePaymentFixture(f).SeedApprovedAsync(10000m, default);
        var id = Value(await Prepare(f, request, null, "Create")).InstallmentId!.Value;
        Value(await Prepare(f, request, id, "Submit")); Value(await Prepare(f, request, id, "Approve")); return (request, id);
    }
    private static async Task<FinanceResult<PaymentView>> Prepare(FieldAdvanceFixture f, Guid request, Guid? id, string operation)
    {
        var actor = operation == "Approve" ? Checker : RecipientId; var session = await f.SessionAsync(actor);
        await using var db = f.CreateContext(); var access = FieldAdvanceAccessTests.Access(db, session, f.Clock); var people = new FinancePersonResolver(db, f.Clock);
        var paymentAccess = new PaymentAccess(db, access, people, new(db, access, people, f.Clock), f.Clock);
        var executor = new PaymentCommandExecutor(db, paymentAccess, new(db, session, access, people, f.Clock), new AuditEventWriter(db, new CorrelationContext(), f.Clock), f.Clock);
        var service = new InstallmentService(db, executor, new(db, f.Clock), f.Clock); var meta = await Meta(f, request, id);
        return operation switch
        {
            "Create" => await service.CreateAsync(actor, request, new("3000", new("Transfer", AuthorPersonId, "test-bank", "owner-account", "owner")), meta, default),
            "Submit" => await service.SubmitAsync(actor, id!.Value, meta, default),
            _ => await new InstallmentReviewService(db, executor, people, f.Clock).ReviewAsync(actor, id!.Value, "Approve", meta, default)
        };
    }
    private static async Task<FinanceResult<PaymentView>> Call(FieldAdvanceFixture f, Guid request, Guid id, string operation, Files files,
        ActualPaymentInput? input = null, PaymentCommand? meta = null, Guid? actor = null, bool forbidIO = false, Func<Task>? beforeBody = null)
    {
        var user = actor ?? RecipientId; var session = await f.SessionAsync(user);
        await using var db = new Tpr10DbContext(new DbContextOptionsBuilder<Tpr10DbContext>().UseNpgsql(f.Database.ConnectionString)
            .AddInterceptors(new BeforeBody(beforeBody)).Options);
        var access = FieldAdvanceAccessTests.Access(db, session, f.Clock); var people = new FinancePersonResolver(db, f.Clock);
        var paymentAccess = new PaymentAccess(db, access, people, new(db, access, people, f.Clock), f.Clock);
        var executor = new PaymentCommandExecutor(db, paymentAccess, new(db, session, access, people, f.Clock), new AuditEventWriter(db, new CorrelationContext(), f.Clock), f.Clock);
        var evidence = new FinanceEvidenceService(db, paymentAccess, new ObservedStore(files.Store, db, forbidIO), new RejectPdf(), files.Options, f.Clock);
        var service = new PaymentExecutionService(db, executor, new(db, f.Clock), people, evidence, files.Options, f.Clock);
        meta ??= await Meta(f, request, id);
        return operation == "Start" ? await service.StartAsync(user, id, meta, default)
            : await service.RecordAsync(user, id, input!, meta, default);
    }
    private static async Task<ActualPaymentInput> Actual(FieldAdvanceFixture f, Files files, Guid request)
    {
        var key = Guid.NewGuid().ToString("N"); var stored = await files.Store.WriteImmutableAsync(files.Options.ActiveStorageId, key, new MemoryStream([1, 2, 3]), default);
        await using var db = f.CreateContext(); var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request); var id = Guid.NewGuid();
        db.Add(new AdvanceEvidence
        {
            Id = id,
            WorkspaceId = r.WorkspaceId,
            ProjectId = r.ProjectId,
            FamilyId = r.FamilyId,
            RequestId = request,
            Sha256 = stored.Sha256,
            Length = stored.Length,
            ContentType = "image/png",
            State = "Ready",
            CreatedBy = RecipientId,
            CreatedByPersonId = RecipientPersonId,
            CreatedAtUtc = f.Clock.GetUtcNow()
        });
        db.Add(new AdvanceEvidenceLocation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = r.WorkspaceId,
            ProjectId = r.ProjectId,
            FamilyId = r.FamilyId,
            RequestId = request,
            EvidenceId = id,
            StorageKey = new EvidenceLocator(files.Options.ActiveStorageId, key).ToString(),
            IsCurrent = true,
            VerifiedAtUtc = f.Clock.GetUtcNow(),
            CreatedAtUtc = f.Clock.GetUtcNow()
        });
        await db.SaveChangesAsync(); return new("3000", AuthorPersonId, DateOnly.FromDateTime(f.Clock.GetUtcNow().UtcDateTime), "transfer-reference", [id]);
    }
    private sealed class Files : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "tpr10-execution-" + Guid.NewGuid().ToString("N"));
        public FinanceEvidenceOptions Options { get; }
        public FolderFinanceEvidenceStore Store { get; }
        public Files()
        {
            // The real store rejects symlink ancestors; canonicalize the macOS temporary directory.
            root = OperatingSystem.IsMacOS() && root.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + root : root;
            Directory.CreateDirectory(root); var id = Guid.NewGuid();
            Options = new() { ActiveStorageId = id, Roots = new() { [id] = root } }; Store = new(Options);
        }
        public void Remove() { foreach (var path in Directory.GetFiles(root)) File.Delete(path); }
        public void Dispose() => Directory.Delete(root, true);
    }
    private sealed class BeforeBody(Func<Task>? change) : DbCommandInterceptor
    {
        private bool changed;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (!changed && change != null && command.CommandText.Contains("SELECT * FROM field_advance_installments WHERE id="))
            { changed = true; await change(); }
            return result;
        }
    }
    private sealed class BeforeEvidenceLock(Action? change) : DbCommandInterceptor
    {
        private bool changed;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (!changed && change != null && command.CommandText.Contains("SELECT * FROM field_advance_evidence WHERE id = ANY")
                && command.CommandText.Contains("FOR UPDATE"))
            { changed = true; change(); }
            return new(result);
        }
    }
    private sealed class AfterConfirmationPersistence(Guid commandId, Action? change) : SaveChangesInterceptor
    {
        private bool changed;
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
        {
            if (!changed && change != null && eventData.Context is { } db
                && db.Set<AdvancePaymentEvent>().Local.Any(x => x.CommandId == commandId && x.Kind == "reconcile"))
            {
                Assert.NotNull(db.Database.CurrentTransaction);
                changed = true; change();
            }
            return new(result);
        }
    }
    private sealed class RejectPdf : IFinancePdfValidator { public Task<bool> ValidateAsync(byte[] content, CancellationToken ct) => Task.FromResult(false); }
    private sealed class ObservedStore(IFinanceEvidenceStore inner, Tpr10DbContext db, bool forbidIO) : IFinanceEvidenceStore
    {
        public Task<EvidenceTicket> WriteImmutableAsync(Guid storage, string key, Stream stream, CancellationToken ct) => inner.WriteImmutableAsync(storage, key, stream, ct);
        public Task<byte[]> ReadVerifiedAsync(Guid storage, string key, string hash, long length, CancellationToken ct)
        { Assert.False(forbidIO, "Receipt replay must not read files"); Assert.Null(db.Database.CurrentTransaction); return inner.ReadVerifiedAsync(storage, key, hash, length, ct); }
        public IEnumerable<EvidenceLocator> Enumerate() => inner.Enumerate();
    }
}
