using System.Text.Json;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
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
public sealed class FieldAdvancePaymentAmendmentTests(PostgresFixture postgres)
{
    private static readonly Guid Checker = Guid.Parse("71000000-0000-0000-0000-000000000045");
    private static readonly Guid CheckerPerson = Guid.Parse("72000000-0000-0000-0000-000000000045");

    // Releasing at proposal, releasing twice, or admitting a start/edit under CancelPending breaks this.
    [Fact]
    public async Task CancelReleasesOnlyAfterConfirmation()
    {
        await using var f = await Ready(); using var files = new Files(); var (request, id) = await Reviewed(f);
        var proposed = Value(await Amend(f, request, id, "Cancellation", "Propose", files));
        Assert.Equal("CancelPending", proposed.State); Assert.Equal(3000, proposed.Reserved);
        Assert.Equal(409, (await Call(f, request, id, "Start", files)).Status);
        Assert.Equal(409, (await Prepare(f, request, id, "Submit")).Status);
        Assert.Equal(409, (await Prepare(f, request, id, "Update")).Status);
        Assert.Equal(409, (await Prepare(f, request, id, "Approve")).Status);
        var proposal = await Proposal(f, id, "PaymentCancellation"); var meta = await Meta(f, request, id);
        var after = Value(await Amend(f, request, id, "Cancellation", "Confirm", files, proposal, meta));
        Assert.Equal("Cancelled", after.State); Assert.Equal(0, after.Reserved); Assert.Equal(0, after.Paid);
        Assert.Equal(10000, after.Available); Assert.Equal(10000, after.Approved);
        Value(await Amend(f, request, id, "Cancellation", "Confirm", files, proposal, meta));
        Assert.Equal(409, (await Amend(f, request, id, "Cancellation", "Confirm", files, proposal)).Status);
        await using var db = f.CreateContext(); Assert.Single(await db.Set<AdvanceReservationEntry>().Where(x => x.Action == "Release").ToArrayAsync());
        Assert.Single(await db.Set<AdvanceInstallmentRevision>().ToArrayAsync());
        Assert.Equal("Approved", (await db.Set<AdvanceRequest>().SingleAsync()).State);
    }

    [Fact]
    public async Task DraftCancellationRetainsRowsWithoutLedgerRelease()
    {
        await using var f = await Ready(); using var files = new Files(); var request = await new FieldAdvancePaymentFixture(f).SeedApprovedAsync(10000, default);
        var id = Value(await Prepare(f, request, null, "Create")).InstallmentId!.Value;
        Assert.Equal(0, Value(await Amend(f, request, id, "Cancellation", "Propose", files)).Reserved);
        Assert.Equal("Cancelled", Value(await Amend(f, request, id, "Cancellation", "Confirm", files, await Proposal(f, id, "PaymentCancellation"))).State);
        await using var db = f.CreateContext(); Assert.Single(await db.Set<AdvanceInstallment>().ToArrayAsync());
        Assert.Single(await db.Set<AdvanceInstallmentRevision>().ToArrayAsync()); Assert.Empty(await db.Set<AdvanceReservationEntry>().ToArrayAsync());
    }

    [Theory]
    [InlineData("Paying")]
    [InlineData("Unknown")]
    [InlineData("Paid")]
    public async Task CancellationCannotErasePaymentOrUnresolvedAttempt(string state)
    {
        await using var f = await Ready(); using var files = new Files(); var (request, id) = await Reviewed(f);
        Value(await Call(f, request, id, "Start", files)); var actual = await Actual(f, files, request);
        if (state == "Paid") Value(await Call(f, request, id, "Record", files, actual));
        if (state == "Unknown")
        {
            Value(await Recover(f, request, id, "Outcome", "Propose", files, new PaymentOutcomeInput("Unknown", null, actual.EvidenceIds)));
            Value(await Recover(f, request, id, "Outcome", "Confirm", files, await Proposal(f, id, "PaymentOutcome")));
        }
        var before = await RecoveryState(f);
        Assert.Equal(409, (await Amend(f, request, id, "Cancellation", "Propose", files)).Status);
        Assert.Equal(before, await RecoveryState(f));
        if (state != "Paid")
        {
            Value(await Recover(f, request, id, "Outcome", "Propose", files, new PaymentOutcomeInput("Unpaid", null, actual.EvidenceIds)));
            Value(await Recover(f, request, id, "Outcome", "Confirm", files, await Proposal(f, id, "PaymentOutcome")));
            Value(await Amend(f, request, id, "Cancellation", "Propose", files));
            Assert.Equal(0, Value(await Amend(f, request, id, "Cancellation", "Confirm", files, await Proposal(f, id, "PaymentCancellation"))).Reserved);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelRacesStartOrRecordWithoutReturningPaidMoney(bool recording)
    {
        await using var f = await Ready(); using var files = new Files(); var (request, id) = await Reviewed(f);
        var actual = await Actual(f, files, request); if (recording) Value(await Call(f, request, id, "Start", files));
        var meta = await Meta(f, request, id);
        var results = await Task.WhenAll(Amend(f, request, id, "Cancellation", "Propose", files, meta: meta),
            Call(f, request, id, recording ? "Record" : "Start", files, actual, meta with { CommandId = Guid.NewGuid() }));
        Assert.Single(results, x => x.Status == 200); Assert.Single(results, x => x.Status == 409);
        await using var db = f.CreateContext(); var account = await db.Set<AdvancePaymentAccount>().SingleAsync();
        Assert.Equal(3000, account.Reserved + account.Paid); Assert.Empty(await db.Set<AdvanceReservationEntry>().Where(x => x.Action == "Release").ToArrayAsync());
        if (recording) Assert.Equal(3000, account.Paid);
    }

    // Updating the immutable result, money, recipient, destination or dropping old evidence breaks this.
    [Fact]
    public async Task MetadataNeverChangesMoney()
    {
        await using var f = await Ready(); using var files = new Files(); var (request, id) = await Reviewed(f);
        Value(await Call(f, request, id, "Start", files)); var original = await Actual(f, files, request);
        var before = Value(await Call(f, request, id, "Record", files, original));
        string resultBefore, revisionBefore;
        await using (var db = f.CreateContext()) { resultBefore = JsonSerializer.Serialize(await db.Set<AdvancePaymentResult>().SingleAsync()); revisionBefore = JsonSerializer.Serialize(await db.Set<AdvanceInstallmentRevision>().SingleAsync()); }
        var replacement = await Actual(f, files, request); var input = new PaymentMetadataInput(original.PaidOn.AddDays(-1), "corrected", replacement.EvidenceIds);
        Value(await Amend(f, request, id, "Metadata", "Propose", files, input));
        var after = Value(await Amend(f, request, id, "Metadata", "Confirm", files, await Proposal(f, id, "PaymentMetadata")));
        Assert.Equal(before.Paid, after.Paid); Assert.Equal("Paid", after.State); Assert.Equal(before.Reserved, after.Reserved);
        Assert.Equal(before.Approved, after.Approved); Assert.Equal(before.Spendable, after.Spendable); Assert.Equal(before.Available, after.Available);
        await using (var db = f.CreateContext())
        {
            Assert.Equal(resultBefore, JsonSerializer.Serialize(await db.Set<AdvancePaymentResult>().SingleAsync()));
            Assert.Equal(revisionBefore, JsonSerializer.Serialize(await db.Set<AdvanceInstallmentRevision>().SingleAsync()));
            var correction = await db.Set<AdvancePaymentProposal>().SingleAsync(x => x.Kind == "PaymentMetadata"); Assert.Equal("Approved", correction.State);
            Assert.Contains(original.EvidenceIds[0].ToString(), correction.BeforeJson); Assert.Contains("corrected", correction.AfterJson);
            Assert.Contains(replacement.EvidenceIds[0].ToString(), correction.AfterJson);
            Assert.Single(await db.Set<AdvanceReservationEntry>().Where(x => x.Action == "Consume").ToArrayAsync());
        }
        foreach (var evidence in original.EvidenceIds.Concat(replacement.EvidenceIds))
        {
            Assert.Equal(200, (await ReadEvidence(f, files, evidence, RecipientId)).Status);
            Assert.Equal(403, (await ReadEvidence(f, files, evidence, AliasAccountId)).Status);
        }
        Value(await Amend(f, request, id, "Metadata", "Propose", files, input with { Reference = "second" }));
        var latest = await Proposal(f, id, "PaymentMetadata");
        Value(await Amend(f, request, id, "Metadata", "Confirm", files, latest));
        await using var final = f.CreateContext(); Assert.Contains("corrected", (await final.Set<AdvancePaymentProposal>().SingleAsync(x => x.Id == latest)).BeforeJson);
    }

    [Fact]
    public async Task ExplicitMismatchBeforeCorrectionRejectsStaleVersionsAndHoldsRootAndSupplement()
    {
        await using var f = await Ready(); using var files = new Files(); var (request, id) = await Reviewed(f);
        var sibling = await Sibling(f, request);
        var rootReady = Value(await Prepare(f, request, null, "Create")).InstallmentId!.Value;
        var siblingReady = Value(await Prepare(f, sibling, null, "Create")).InstallmentId!.Value;
        foreach (var pair in new[] { (request, rootReady), (sibling, siblingReady) }) { Value(await Prepare(f, pair.Item1, pair.Item2, "Submit")); Value(await Prepare(f, pair.Item1, pair.Item2, "Approve")); }
        Value(await Call(f, request, id, "Start", files)); var original = await Actual(f, files, request); Value(await Call(f, request, id, "Record", files, original));
        var observed = (await Actual(f, files, request)) with { Amount = "3100", RecipientPersonId = RecipientPersonId };
        var input = new PaymentMetadataInput(observed.PaidOn, "incident-linked", observed.EvidenceIds); var stale = await Meta(f, request, id);
        Value(await Recover(f, request, id, "Hold", "ReportMismatch", files, observed));
        Assert.Equal(409, (await Amend(f, request, id, "Metadata", "Propose", files, input, stale)).Status);
        Value(await Amend(f, request, id, "Metadata", "Propose", files, input));
        var after = Value(await Amend(f, request, id, "Metadata", "Confirm", files, await Proposal(f, id, "PaymentMetadata")));
        Assert.Equal(3000, after.Paid); Assert.Equal("Paid", after.State); Assert.Contains("PaymentMismatch", after.HoldCodes);
        Assert.Equal(409, (await Call(f, request, rootReady, "Start", files)).Status); Assert.Equal(409, (await Call(f, sibling, siblingReady, "Start", files)).Status);
        Assert.Equal(200, (await ReadEvidence(f, files, original.EvidenceIds[0], RecipientId)).Status);
        await using var db = f.CreateContext(); var result = await db.Set<AdvancePaymentResult>().SingleAsync();
        Assert.Equal(AuthorPersonId, result.RecipientPersonId); Assert.Equal(3000, result.Amount); Assert.Contains(original.EvidenceIds[0].ToString(), result.EvidenceIdsJson);
        Assert.Null((await db.Set<AdvancePaymentHold>().SingleAsync()).ReleasedAtUtc);
    }

    [Theory]
    [InlineData("Cancellation", false)]
    [InlineData("Metadata", false)]
    [InlineData("Cancellation", true)]
    [InlineData("Metadata", true)]
    public async Task ConfirmationRechecksCurrentProposerInitiallyAndAfterPersistence(string service, bool atCommit)
    {
        await using var f = await Ready(); using var files = new Files(); var (request, id) = await Reviewed(f);
        var actual = await Actual(f, files, request);
        if (service == "Metadata") { Value(await Call(f, request, id, "Start", files)); Value(await Call(f, request, id, "Record", files, actual)); }
        Value(await Amend(f, request, id, service, "Propose", files, new PaymentMetadataInput(actual.PaidOn, "fixed", actual.EvidenceIds)));
        var proposal = await Proposal(f, id, "Payment" + service);
        Assert.Equal(403, (await Amend(f, request, id, service, "Confirm", files, proposal, actor: RecipientId)).Status);
        await using (var db = f.CreateContext()) { (await db.Set<GrantProposal>().SingleAsync(x => x.RecipientId == RecipientId)).EndsAtUtc = f.Clock.GetUtcNow().AddSeconds(atCommit ? 1 : 0); await db.SaveChangesAsync(); }
        var before = await RecoveryState(f); var advanced = false;
        Action? expire = atCommit ? () => { advanced = true; f.Clock.Advance(TimeSpan.FromSeconds(1)); } : null;
        Assert.Equal(403, (await Amend(f, request, id, service, "Confirm", files, proposal, afterPersistence: expire)).Status);
        Assert.Equal(atCommit, advanced); Assert.Equal(before, await RecoveryState(f));
    }

    [Theory]
    [InlineData("Cancellation")]
    [InlineData("Metadata")]
    public async Task ConfirmationRaceReplayAndCommitFailureAreAtomic(string service)
    {
        await using var f = await Ready(); using var files = new Files(); var (request, id) = await Reviewed(f); var actual = await Actual(f, files, request);
        if (service == "Metadata") { Value(await Call(f, request, id, "Start", files)); Value(await Call(f, request, id, "Record", files, actual)); }
        Value(await Amend(f, request, id, service, "Propose", files, new PaymentMetadataInput(actual.PaidOn, "fixed", actual.EvidenceIds)));
        var proposal = await Proposal(f, id, "Payment" + service); var meta = await Meta(f, request, id); var before = await RecoveryState(f);
        await using (var db = f.CreateContext()) await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION amendment_fail() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'amendment test commit failure'; END; $$;
            CREATE CONSTRAINT TRIGGER amendment_fail AFTER INSERT ON field_advance_payment_events DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION amendment_fail();
            """);
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Amend(f, request, id, service, "Confirm", files, proposal, meta)); Assert.Equal("P0001", error.SqlState);
        Assert.Equal(before, await RecoveryState(f));
        await using (var db = f.CreateContext()) await db.Database.ExecuteSqlRawAsync("DROP TRIGGER amendment_fail ON field_advance_payment_events");
        var competing = meta with { CommandId = Guid.NewGuid() };
        var results = await Task.WhenAll(Amend(f, request, id, service, "Confirm", files, proposal, meta), Amend(f, request, id, service, "Confirm", files, proposal, competing));
        Assert.Single(results, x => x.Status == 200); Assert.Single(results, x => x.Status == 409);
        var winning = results[0].Status == 200 ? meta : competing; files.Remove(); await Revoke(f);
        Value(await Amend(f, request, id, service, "Confirm", files, proposal, winning, forbidIO: true));
        await using (var db = f.CreateContext()) { (await db.Set<GrantProposal>().SingleAsync(x => x.RecipientId == Checker)).EndsAtUtc = f.Clock.GetUtcNow(); await db.SaveChangesAsync(); }
        Assert.Equal(403, (await Amend(f, request, id, service, "Confirm", files, proposal, winning, forbidIO: true)).Status);
    }


    [Theory]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("foreign")]
    [InlineData("date")]
    [InlineData("missing")]
    public async Task InvalidMetadataEvidenceCannotCreateCorrection(string defect)
    {
        await using var f = await Ready(); using var files = new Files(); var (request, id) = await Reviewed(f);
        Value(await Call(f, request, id, "Start", files)); var actual = await Actual(f, files, request); Value(await Call(f, request, id, "Record", files, actual));
        var input = new PaymentMetadataInput(actual.PaidOn, "fixed", actual.EvidenceIds);
        if (defect == "empty") input = input with { EvidenceIds = [] };
        if (defect == "duplicate") input = input with { EvidenceIds = [actual.EvidenceIds[0], actual.EvidenceIds[0]] };
        if (defect == "foreign") input = input with { EvidenceIds = (await Actual(f, files, await Sibling(f, request))).EvidenceIds };
        if (defect == "date") input = input with { PaidOn = default };
        if (defect == "missing") files.Remove();
        var before = await RecoveryState(f);
        Assert.Equal(400, (await Amend(f, request, id, "Metadata", "Propose", files, input)).Status);
        Assert.Equal(before, await RecoveryState(f));
    }

    [Theory]
    [InlineData("Cancellation")]
    [InlineData("Metadata")]
    public async Task TamperedProposalCannotConfirm(string service)
    {
        await using var f = await Ready(); using var files = new Files(); var (request, id) = await Reviewed(f); var actual = await Actual(f, files, request);
        if (service == "Metadata") { Value(await Call(f, request, id, "Start", files)); Value(await Call(f, request, id, "Record", files, actual)); }
        Value(await Amend(f, request, id, service, "Propose", files, new PaymentMetadataInput(actual.PaidOn, "fixed", actual.EvidenceIds)));
        var proposal = await Proposal(f, id, "Payment" + service);
        await using (var db = f.CreateContext()) { (await db.Set<AdvancePaymentProposal>().SingleAsync(x => x.Id == proposal)).BeforeJson = "{}"; await db.SaveChangesAsync(); }
        var before = await RecoveryState(f); Assert.Equal(409, (await Amend(f, request, id, service, "Confirm", files, proposal)).Status);
        Assert.Equal(before, await RecoveryState(f));
    }

    [Fact]
    public async Task CancellationCanBeReproposedAfterFamilyVersionChanges()
    {
        await using var f = await Ready(); using var files = new Files(); var (request, id) = await Reviewed(f);
        Value(await Amend(f, request, id, "Cancellation", "Propose", files)); var stale = await Proposal(f, id, "PaymentCancellation");
        Value(await Prepare(f, request, null, "Create"));
        Assert.Equal(409, (await Amend(f, request, id, "Cancellation", "Confirm", files, stale)).Status);
        Assert.Equal(3000, Value(await Amend(f, request, id, "Cancellation", "Propose", files)).Reserved);
        Assert.Equal(0, Value(await Amend(f, request, id, "Cancellation", "Confirm", files, await Proposal(f, id, "PaymentCancellation"))).Reserved);
    }


    // These guards already exist in the implementation; exercise their real evidence/transaction boundary.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedEvidenceAfterPreparationCannotBeLinked(bool confirming)
    {
        await using var f = await Ready(); using var files = new Files(); var (request, id) = await Reviewed(f);
        Value(await Call(f, request, id, "Start", files)); var actual = await Actual(f, files, request); Value(await Call(f, request, id, "Record", files, actual));
        var input = new PaymentMetadataInput(actual.PaidOn, "fixed", actual.EvidenceIds); Guid proposal = default;
        if (confirming) { Value(await Amend(f, request, id, "Metadata", "Propose", files, input)); proposal = await Proposal(f, id, "PaymentMetadata"); }
        var changed = false;
        async Task Change()
        {
            await using var db = f.CreateContext(); var row = await db.Set<AdvanceEvidence>().SingleAsync(); row.ContentType = "image/jpeg"; await db.SaveChangesAsync(); changed = true;
        }
        var result = await Amend(f, request, id, "Metadata", confirming ? "Confirm" : "Propose", files,
            confirming ? proposal : input, beforeBody: Change);
        Assert.True(changed); Assert.Equal(confirming ? 409 : 400, result.Status);
        await using var final = f.CreateContext(); Assert.Equal(3000, (await final.Set<AdvancePaymentAccount>().SingleAsync()).Paid);
        Assert.Empty(await final.Set<AdvancePaymentProposal>().Where(x => x.Kind == "PaymentMetadata" && x.State == "Approved").ToArrayAsync());
    }

    [Fact]
    public async Task MissingCorrectionFilePreventsConfirmationWithoutChangingMoney()
    {
        await using var f = await Ready(); using var files = new Files(); var (request, id) = await Reviewed(f);
        Value(await Call(f, request, id, "Start", files)); var actual = await Actual(f, files, request); Value(await Call(f, request, id, "Record", files, actual));
        Value(await Amend(f, request, id, "Metadata", "Propose", files, new PaymentMetadataInput(actual.PaidOn, "fixed", actual.EvidenceIds)));
        var proposal = await Proposal(f, id, "PaymentMetadata"); var before = await RecoveryState(f); files.Remove();
        Assert.Equal(409, (await Amend(f, request, id, "Metadata", "Confirm", files, proposal)).Status); Assert.Equal(before, await RecoveryState(f));
    }

    [Theory]
    [InlineData("Cancellation")]
    [InlineData("Metadata")]
    public async Task ProposalReplayDoesNotDuplicateHistoryAndRejectsChangedReason(string service)
    {
        await using var f = await Ready(); using var files = new Files(); var (request, id) = await Reviewed(f); var actual = await Actual(f, files, request);
        if (service == "Metadata") { Value(await Call(f, request, id, "Start", files)); Value(await Call(f, request, id, "Record", files, actual)); }
        var input = new PaymentMetadataInput(actual.PaidOn, "fixed", actual.EvidenceIds); var meta = await Meta(f, request, id);
        var first = Value(await Amend(f, request, id, service, "Propose", files, input, meta)); var before = await RecoveryState(f); files.Remove();
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(Value(await Amend(f, request, id, service, "Propose", files, input, meta, forbidIO: true))));
        Assert.Equal(409, (await Amend(f, request, id, service, "Propose", files, input, meta with { Reason = "เปลี่ยนเหตุผล" }, forbidIO: true)).Status);
        Assert.Equal(before, await RecoveryState(f));
    }

    [Fact]
    public async Task MetadataCannotTurnUnpaidInstallmentIntoPaid()
    {
        await using var f = await Ready(); using var files = new Files(); var (request, id) = await Reviewed(f); var actual = await Actual(f, files, request);
        var before = await RecoveryState(f);
        Assert.Equal(409, (await Amend(f, request, id, "Metadata", "Propose", files, new PaymentMetadataInput(actual.PaidOn, "fixed", actual.EvidenceIds))).Status);
        Assert.Equal(before, await RecoveryState(f));
    }

    private static async Task<FinanceResult<PaymentView>> Amend(FieldAdvanceFixture f, Guid request, Guid id, string service, string operation,
        Files files, object? input = null, PaymentCommand? meta = null, Guid? actor = null, bool forbidIO = false, Action? afterPersistence = null, Func<Task>? beforeBody = null)
    {
        var user = actor ?? (operation == "Confirm" ? Checker : RecipientId); var session = await f.SessionAsync(user); meta ??= await Meta(f, request, id);
        await using var db = new Tpr10DbContext(new DbContextOptionsBuilder<Tpr10DbContext>().UseNpgsql(f.Database.ConnectionString)
            .AddInterceptors(new BeforeBody(beforeBody), new AfterConfirmationPersistence(meta.CommandId, afterPersistence)).Options);
        var access = FieldAdvanceAccessTests.Access(db, session, f.Clock); var people = new FinancePersonResolver(db, f.Clock);
        var paymentAccess = new PaymentAccess(db, access, people, new(db, access, people, f.Clock), f.Clock);
        var executor = new PaymentCommandExecutor(db, paymentAccess, new(db, session, access, people, f.Clock), new AuditEventWriter(db, new CorrelationContext(), f.Clock), f.Clock);
        var evidence = new FinanceEvidenceService(db, paymentAccess, new ObservedStore(files.Store, db, forbidIO), new RejectPdf(), files.Options, f.Clock);
        if (service == "Cancellation")
        {
            var cancellation = new PaymentCancellationService(db, executor, new(db, f.Clock), people, evidence, files.Options, f.Clock);
            return operation == "Confirm" ? await cancellation.ConfirmAsync(user, (Guid)input!, meta, default)
                : await cancellation.ProposeAsync(user, id, meta, default);
        }
        var metadata = new PaymentMetadataService(db, executor, people, evidence, files.Options, f.Clock);
        return operation == "Confirm" ? await metadata.ConfirmAsync(user, (Guid)input!, meta, default)
            : await metadata.ProposeAsync(user, id, (PaymentMetadataInput)input!, meta, default);
    }
    private static async Task<FinanceResult<byte[]>> ReadEvidence(FieldAdvanceFixture f, Files files, Guid id, Guid actor)
    {
        var session = await f.SessionAsync(actor); await using var db = f.CreateContext(); var access = FieldAdvanceAccessTests.Access(db, session, f.Clock); var people = new FinancePersonResolver(db, f.Clock);
        var payment = new PaymentAccess(db, access, people, new(db, access, people, f.Clock), f.Clock);
        return await new FinanceEvidenceService(db, payment, files.Store, new RejectPdf(), files.Options, f.Clock).ReadAsync(actor, id, default);
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
            "Update" => await service.UpdateAsync(actor, id!.Value, new("2500", new("Cash", AuthorPersonId, null, null, null)), meta, default),
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
