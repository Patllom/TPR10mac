using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Payments;
using TPR10.Api.FieldAdvances.Requests;
using TPR10.Api.FieldAdvances.Evidence;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using L = TPR10.Api.IntegrationTests.FieldAdvanceRequestLifecycleTests;
using P = TPR10.Api.IntegrationTests.FieldAdvancePolicyTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvancePaymentTerminationTests(PostgresFixture postgres)
{
    private static readonly Guid Director = Guid.Parse("71000000-0000-0000-0000-000000000051");
    private static readonly Guid Payer = Guid.Parse("71000000-0000-0000-0000-000000000052");
    private static readonly Guid Checker = Guid.Parse("71000000-0000-0000-0000-000000000053");

    // Red if termination rejects a paid approved request, erases approval history, or lowers supplement authority.
    [Fact]
    public async Task TerminationPreservesApprovalBasis()
    {
        await using var f = await Ready(); using var files = new EvidenceFiles();
        var root = await PaidRoot(f, files);
        var frozen = await L.Frozen(f, root.RevisionId);
        Value(await Disposition(f, root.Id, "Propose", "TerminateRemaining"));
        var proposal = await Proposal(f, "PaymentDisposition");
        Value(await Disposition(f, root.Id, "Confirm", proposal));
        var view = Value(await Disposition(f, root.Id, "Finalize", proposal));
        var account = await Account(f);
        Assert.Equal(10000m, account.Approved); Assert.Equal(2000m, account.Spendable);
        Assert.Equal(2000m, account.Paid); Assert.Equal(0m, account.Reserved); Assert.Equal(0m, view.Available);
        Assert.Equal("Approved", (await View(f, root.Id)).State);
        Assert.Equal(frozen, await L.Frozen(f, root.RevisionId));
        Assert.Equal(409, (await Payment(f, root.Id, null, "Create")).Status);
        await using var db = f.CreateContext(); var session = await f.SessionAsync(AuthorId);
        var access = FieldAdvanceAccessTests.Access(db, session, f.Clock); var people = new FinancePersonResolver(db, f.Clock);
        var service = new SupplementService(db, new(db, access, people, f.Clock), L.Draft(f, db, session),
            new AuditEventWriter(db, new CorrelationContext(), f.Clock), f.Clock);
        var draft = await service.CreateAsync(AuthorId, root.Id, Content(30), L.Meta(0), default);
        Assert.Equal(201, draft.Status);
        var quote = await L.Submission(f, db, session).QuoteAsync(AuthorId, draft.Value!.Id, default);
        Assert.Equal(200, quote.Status); Assert.Equal(13000m, quote.Value!.Basis.Amount);
        Assert.Equal(10000m, quote.Value.Basis.Items.Single(x => x.RequestId == root.Id).ApprovedAmount);
    }

    // Confirmation must not release an in-flight reservation or bypass independent unpaid cancellation.
    [Fact]
    public async Task UnknownMustBeReconciledAndUnpaidCancellationConfirmed()
    {
        await using var f = await Ready(); using var files = new EvidenceFiles(); var root = await PaidRoot(f, files);
        var id = await Reviewed(f, root.Id); Value(await Payment(f, root.Id, id, "Start"));
        var proposal = await ConfirmTermination(f, root.Id);
        Assert.Equal(10000m, (await Account(f)).Spendable); Assert.Equal(2000m, (await Account(f)).Reserved);
        Assert.Equal(409, (await Disposition(f, root.Id, "Finalize", proposal)).Status);
        var evidence = await Evidence(f, files, root.Id);
        Value(await Recover(f, files, root.Id, id, evidence, "Unknown"));
        Value(await Recover(f, files, root.Id, id, evidence, "Confirm", await Proposal(f, "PaymentOutcome")));
        Assert.Equal(409, (await Disposition(f, root.Id, "Finalize", proposal)).Status);
        Value(await Recover(f, files, root.Id, id, evidence, "Unpaid"));
        Value(await Recover(f, files, root.Id, id, evidence, "Confirm", await Proposal(f, "PaymentOutcome")));
        Assert.Equal(409, (await Disposition(f, root.Id, "Finalize", proposal)).Status);
        Value(await Payment(f, root.Id, id, "CancelPropose"));
        Assert.Equal(409, (await Disposition(f, root.Id, "Finalize", proposal)).Status);
        Value(await Payment(f, root.Id, id, "CancelConfirm", proposal: await Proposal(f, "PaymentCancellation")));
        var view = Value(await Disposition(f, root.Id, "Finalize", proposal));
        Assert.Equal(2000m, view.Paid); Assert.Equal(2000m, view.Spendable); Assert.Equal(0m, view.Reserved);
        Assert.Equal(409, (await Payment(f, root.Id, id, "Start")).Status);
        await using var db = f.CreateContext();
        Assert.Equal("Cancelled", (await db.Set<AdvanceInstallment>().SingleAsync(x => x.Id == id)).State);
        Assert.Equal("Unpaid", (await db.Set<AdvancePaymentAttempt>().SingleAsync(x => x.InstallmentId == id)).State);
        Assert.Single(await db.Set<AdvancePaymentResult>().ToArrayAsync());
        Assert.Single(await db.Set<AdvanceReservationEntry>().Where(x => x.Action == "Release").ToArrayAsync());
    }

    [Fact]
    public async Task FinalizeRacesRecordAndUsesFreshPaidBalance()
    {
        await using var f = await Ready(); using var files = new EvidenceFiles(); var root = await PaidRoot(f, files);
        var id = await Reviewed(f, root.Id); Value(await Payment(f, root.Id, id, "Start"));
        var proposal = await ConfirmTermination(f, root.Id); var stale = await Meta(f, root.Id);
        var recordMeta = await Meta(f, root.Id, id); var evidence = await Evidence(f, files, root.Id);
        var results = await Race(f, () => Disposition(f, root.Id, "Finalize", proposal, meta: stale),
            () => Record(f, files, root.Id, id, recordMeta, evidence));
        Assert.Equal(409, results[0].Status); Assert.Equal(200, results[1].Status);
        Assert.Equal(409, (await Disposition(f, root.Id, "Finalize", proposal, meta: stale)).Status);
        var view = Value(await Disposition(f, root.Id, "Finalize", proposal));
        Assert.Equal(4000m, view.Paid); Assert.Equal(4000m, view.Spendable); Assert.Equal(0m, view.Available);
        await using var db = f.CreateContext(); Assert.Equal(2, await db.Set<AdvancePaymentResult>().CountAsync());
    }

    [Fact]
    public async Task FinalizeRacesCancellationAndDoesNotCancelOnItsOwn()
    {
        await using var f = await Ready(); using var files = new EvidenceFiles(); var root = await PaidRoot(f, files);
        var id = await Reviewed(f, root.Id); var proposal = await ConfirmTermination(f, root.Id);
        Value(await Payment(f, root.Id, id, "CancelPropose")); var cancel = await Proposal(f, "PaymentCancellation");
        var finalizeMeta = await Meta(f, root.Id); var cancelMeta = await Meta(f, root.Id, id);
        var results = await Race(f, () => Disposition(f, root.Id, "Finalize", proposal, meta: finalizeMeta),
            () => Payment(f, root.Id, id, "CancelConfirm", cancelMeta, cancel));
        Assert.Equal(409, results[0].Status); Assert.Equal(200, results[1].Status);
        Assert.Equal(2000m, Value(await Disposition(f, root.Id, "Finalize", proposal)).Spendable);
    }

    [Fact]
    public async Task ProposalBlocksOnlyThisRequestAndPreservesOtherHolds()
    {
        await using var f = await Ready(); using var files = new EvidenceFiles(); var root = await PaidRoot(f, files);
        var id = await Reviewed(f, root.Id); var stale = await Meta(f, root.Id, id);
        var supplement = await Supplement(f, root); var approved = await Approve(f, await L.Submit(f, supplement));
        var other = await Reviewed(f, approved.Id);
        Value(await Disposition(f, root.Id, "Propose", "TerminateRemaining"));
        Assert.Equal(409, (await Payment(f, root.Id, id, "Start", stale)).Status);
        Assert.Equal(409, (await Payment(f, root.Id, id, "Start")).Status);
        Value(await Payment(f, approved.Id, other, "Start"));
        // Start on the sibling changes family version: re-propose with fresh metadata before confirming.
        var proposal = await ConfirmTermination(f, root.Id);
        await using (var db = f.CreateContext())
        {
            var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == root.Id);
            db.Add(new AdvancePaymentHold
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                ProjectId = ProjectId,
                FamilyId = r.FamilyId,
                Code = "PaymentMismatch",
                Reason = "คงเหตุระงับเดิม",
                CreatedBy = Payer,
                CreatedByPersonId = Payer,
                CreatedAtUtc = f.Clock.GetUtcNow()
            }); await db.SaveChangesAsync();
        }
        await Cancel(f, root.Id, id);
        var result = Value(await Disposition(f, root.Id, "Finalize", proposal)); Assert.Contains("PaymentMismatch", result.HoldCodes);
        await using var check = f.CreateContext();
        Assert.All(await check.Set<AdvancePaymentHold>().ToArrayAsync(), x => Assert.Null(x.ReleasedAtUtc));
        Assert.Equal(2000m, (await check.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == approved.Id)).Reserved);
    }

    [Theory]
    [InlineData("Confirm", false)]
    [InlineData("Confirm", true)]
    [InlineData("Finalize", false)]
    [InlineData("Finalize", true)]
    public async Task ParticipantExpiryDuringPersistenceRollsBack(string method, bool checker)
    {
        await using var f = await Ready(); using var files = new EvidenceFiles(); var root = await PaidRoot(f, files);
        Value(await Disposition(f, root.Id, "Propose", "TerminateRemaining")); var proposal = await Proposal(f, "PaymentDisposition");
        if (method == "Finalize") Value(await Disposition(f, root.Id, "Confirm", proposal));
        await SetExpiry(f, checker ? Checker : Director, f.Clock.GetUtcNow().AddMinutes(1));
        var before = await FinancialSnapshot(f);
        Assert.Equal(403, (await Disposition(f, root.Id, method, proposal,
            auditAction: () => f.Clock.Advance(TimeSpan.FromMinutes(2)))).Status);
        Assert.Equal(before, await FinancialSnapshot(f)); Assert.Equal(10000m, (await Account(f)).Spendable);
    }

    [Theory]
    [InlineData("Propose")]
    [InlineData("Confirm")]
    [InlineData("Finalize")]
    public async Task DeferredCommitFailureRollsBackAndSameCommandCanRetry(string method)
    {
        await using var f = await Ready(); using var files = new EvidenceFiles(); var root = await PaidRoot(f, files);
        object input = "TerminateRemaining";
        if (method != "Propose")
        {
            Value(await Disposition(f, root.Id, "Propose", "TerminateRemaining")); input = await Proposal(f, "PaymentDisposition");
            if (method == "Finalize") Value(await Disposition(f, root.Id, "Confirm", input));
        }
        var meta = await Meta(f, root.Id); var before = await FinancialSnapshot(f);
        await using (var db = f.CreateContext()) await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION termination_fail() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'task11 deferred failure'; END; $$;
            CREATE CONSTRAINT TRIGGER termination_fail AFTER INSERT ON field_advance_payment_events DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION termination_fail();
            """);
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Disposition(f, root.Id, method, input, meta: meta));
        Assert.Equal("P0001", error.SqlState); Assert.Equal(before, await FinancialSnapshot(f));
        await using (var db = f.CreateContext()) await db.Database.ExecuteSqlRawAsync("DROP TRIGGER termination_fail ON field_advance_payment_events");
        Value(await Disposition(f, root.Id, method, input, meta: meta));
    }

    [Fact]
    public async Task ReplaysAreSingleEffectAndRecheckAuthority()
    {
        await using var f = await Ready(); using var files = new EvidenceFiles(); var root = await PaidRoot(f, files);
        object input = "TerminateRemaining"; PaymentCommand? finalMeta = null;
        foreach (var method in new[] { "Propose", "Confirm", "Finalize" })
        {
            var meta = await Meta(f, root.Id); var result = await Disposition(f, root.Id, method, input, meta: meta); Value(result);
            var before = await FinancialSnapshot(f);
            Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(await Disposition(f, root.Id, method, input, meta: meta)));
            Assert.Equal(before, await FinancialSnapshot(f));
            Assert.Equal(409, (await Disposition(f, root.Id, method, input, meta: meta with { Reason = "เปลี่ยนข้อมูล" })).Status);
            if (method == "Propose") input = await Proposal(f, "PaymentDisposition");
            finalMeta = meta;
        }
        Assert.Equal(409, (await Disposition(f, root.Id, "Finalize", input)).Status);
        await Revoke(f, Director);
        Assert.Equal(403, (await Disposition(f, root.Id, "Finalize", input, meta: finalMeta)).Status);
    }

    [Fact]
    public async Task ChangedFinalHolderMustConfirmAgainAndOldHolderCannotFinalize()
    {
        await using var f = await Ready(); using var files = new EvidenceFiles(); var root = await PaidRoot(f, files);
        var proposal = await ConfirmTermination(f, root.Id);
        await Revoke(f, Director); var replacement = Guid.Parse("71000000-0000-0000-0000-000000000061");
        await Actor(f, replacement, "request.approve", "director");
        Assert.Equal(403, (await Disposition(f, root.Id, "Finalize", proposal)).Status);
        Assert.Equal(403, (await Disposition(f, root.Id, "Finalize", proposal, actor: replacement)).Status);
        Assert.Equal(10000m, (await Account(f)).Spendable);
        Value(await Disposition(f, root.Id, "Propose", "TerminateRemaining")); var next = await Proposal(f, "PaymentDisposition");
        Value(await Disposition(f, root.Id, "Confirm", next, actor: replacement));
        Assert.Equal(409, (await Disposition(f, root.Id, "Finalize", proposal, actor: replacement)).Status);
        Assert.Equal(2000m, Value(await Disposition(f, root.Id, "Finalize", next, actor: replacement)).Spendable);
    }

    [Fact]
    public async Task StoredCumulativeBasisAndFamilySeparationApplyToTermination()
    {
        await using var f = await Ready(); using var files = new EvidenceFiles(); var root = await PaidRoot(f, files);
        var supplement = await Approve(f, await L.Submit(f, await Supplement(f, root)));
        var id = await Reviewed(f, supplement.Id); Value(await Payment(f, supplement.Id, id, "Start"));
        Value(await Record(f, files, supplement.Id, id));
        Assert.Equal(403, (await Disposition(f, supplement.Id, "Propose", "TerminateRemaining", actor: Payer)).Status);
        Assert.Equal(403, (await Disposition(f, supplement.Id, "Propose", "TerminateRemaining", actor: AuthorId)).Status);
        Value(await Disposition(f, supplement.Id, "Propose", "TerminateRemaining")); var proposal = await Proposal(f, "PaymentDisposition");
        Assert.Equal(403, (await Disposition(f, supplement.Id, "Confirm", proposal, actor: Checker)).Status);
        await using (var db = f.CreateContext()) await db.Set<GrantProposal>().Where(x => x.RecipientId == Director && x.ProjectId == ProjectId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.ApprovalLimit, 10000m));
        Assert.Equal(403, (await Disposition(f, supplement.Id, "Confirm", proposal)).Status);
        await using (var db = f.CreateContext()) await db.Set<GrantProposal>().Where(x => x.RecipientId == Director && x.ProjectId == ProjectId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.ApprovalLimit, 13000m));
        Value(await Disposition(f, supplement.Id, "Confirm", proposal));
        var result = Value(await Disposition(f, supplement.Id, "Finalize", proposal)); Assert.Equal(3000m, result.Approved);
        Assert.Equal(2000m, result.Spendable);
    }

    // A payment already started before the hold may consume the entire remainder; finalization still completes once.
    [Fact]
    public async Task FinalizeAfterInFlightPaymentsConsumeTheEntireRemainder()
    {
        await using var f = await Ready(); using var files = new EvidenceFiles(); var root = await PaidRoot(f, files);
        var inFlight = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            var id = await Reviewed(f, root.Id); Value(await Payment(f, root.Id, id, "Start")); inFlight.Add(id);
        }
        var proposal = await ConfirmTermination(f, root.Id);
        foreach (var id in inFlight) Value(await Record(f, files, root.Id, id));
        var result = Value(await Disposition(f, root.Id, "Finalize", proposal));
        Assert.Equal(10000m, result.Approved); Assert.Equal(10000m, result.Paid); Assert.Equal(10000m, result.Spendable);
        Assert.Equal(0m, result.Reserved); Assert.Equal(0m, result.Available);
        Assert.Equal(409, (await Disposition(f, root.Id, "Finalize", proposal)).Status);
    }

    [Fact]
    public async Task ProposalRacesStartWithoutLosingReservation()
    {
        await using var f = await Ready(); using var files = new EvidenceFiles(); var root = await PaidRoot(f, files);
        var id = await Reviewed(f, root.Id); var meta = await Meta(f, root.Id);
        var startMeta = await Meta(f, root.Id, id);
        var results = await Race(f, () => Disposition(f, root.Id, "Propose", "TerminateRemaining", meta: meta),
            () => Payment(f, root.Id, id, "Start", startMeta));
        Assert.Single(results, x => x.Status == 200); Assert.Single(results, x => x.Status == 409);
        if (results[0].Status == 409) Value(await Disposition(f, root.Id, "Propose", "TerminateRemaining"));
        Assert.Equal(409, (await Payment(f, root.Id, id, "Start")).Status);
        Assert.Equal(2000m, (await Account(f)).Reserved); Assert.Equal(2000m, (await Account(f)).Paid);
    }

    [Fact]
    public async Task AliasCannotTurnFamilyCheckerIntoFinalApprover()
    {
        await using var f = await Ready(); using var files = new EvidenceFiles(); var root = await PaidRoot(f, files);
        Value(await Disposition(f, root.Id, "Propose", "TerminateRemaining")); var proposal = await Proposal(f, "PaymentDisposition");
        var alias = Guid.Parse("71000000-0000-0000-0000-000000000062"); await Actor(f, alias, "request.approve", "director", Checker);
        await Revoke(f, Director);
        Assert.Equal(403, (await Disposition(f, root.Id, "Confirm", proposal, actor: alias)).Status);
        Assert.Equal(10000m, (await Account(f)).Spendable);
    }

    [Fact]
    public async Task UnreservedDraftStillNeedsConfirmedCancellation()
    {
        await using var f = await Ready(); using var files = new EvidenceFiles(); var root = await PaidRoot(f, files);
        var id = Value(await Payment(f, root.Id, null, "Create")).InstallmentId!.Value;
        var proposal = await ConfirmTermination(f, root.Id); Assert.Equal(0m, (await Account(f)).Reserved);
        Assert.Equal(409, (await Disposition(f, root.Id, "Finalize", proposal)).Status);
        await Cancel(f, root.Id, id);
        Assert.Equal(2000m, Value(await Disposition(f, root.Id, "Finalize", proposal)).Spendable);
    }

    [Fact]
    public async Task StaleConfirmationAndUnconfirmedFinalizationCannotChangeBalances()
    {
        await using var f = await Ready(); using var files = new EvidenceFiles(); var root = await PaidRoot(f, files);
        var stale = await Meta(f, root.Id); Value(await Disposition(f, root.Id, "Propose", "TerminateRemaining"));
        var proposal = await Proposal(f, "PaymentDisposition"); var before = await FinancialSnapshot(f);
        Assert.Equal(409, (await Disposition(f, root.Id, "Confirm", proposal, meta: stale)).Status);
        Assert.Equal(409, (await Disposition(f, root.Id, "Finalize", proposal)).Status);
        Assert.Equal(before, await FinancialSnapshot(f));
    }

    private static async Task<FinanceResult<PaymentView>[]> Race(FieldAdvanceFixture f,
        Func<Task<FinanceResult<PaymentView>>> first, Func<Task<FinanceResult<PaymentView>>> second)
    {
        await using var gate = f.CreateContext(); await using var tx = await gate.Database.BeginTransactionAsync();
        await gate.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        var a = first(); var b = second(); var overlapped = false;
        try
        {
            // Observe both real PostgreSQL connections waiting before releasing the service lock.
            await using var observer = f.CreateContext();
            for (var i = 0; i < 500; i++)
            {
                var waiting = await observer.Database.SqlQueryRaw<int>(
                    "SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname=current_database() AND wait_event='advisory'").SingleAsync();
                if (waiting >= 2) { overlapped = true; break; }
                if (a.IsCompleted || b.IsCompleted) break;
                await Task.Delay(20);
            }
        }
        finally { await tx.CommitAsync(); }
        var results = await Task.WhenAll(a, b);
        Assert.True(overlapped, "Both commands must overlap at the real PostgreSQL transaction lock");
        return results;
    }

    private static async Task<Guid> ConfirmTermination(FieldAdvanceFixture f, Guid request)
    {
        Value(await Disposition(f, request, "Propose", "TerminateRemaining")); var id = await Proposal(f, "PaymentDisposition");
        Value(await Disposition(f, request, "Confirm", id)); return id;
    }
    private static async Task<RequestView> Supplement(FieldAdvanceFixture f, RequestView root)
    {
        await using var db = f.CreateContext(); var session = await f.SessionAsync(AuthorId);
        var access = FieldAdvanceAccessTests.Access(db, session, f.Clock); var people = new FinancePersonResolver(db, f.Clock);
        var service = new SupplementService(db, new(db, access, people, f.Clock), L.Draft(f, db, session),
            new AuditEventWriter(db, new CorrelationContext(), f.Clock), f.Clock);
        var result = await service.CreateAsync(AuthorId, root.Id, Content(30), L.Meta(0), default);
        Assert.Equal(201, result.Status); return result.Value!;
    }
    private static async Task<string> FinancialSnapshot(FieldAdvanceFixture f)
    {
        await using var db = f.CreateContext(); var snapshots = new List<string>();
        foreach (var table in new[] { "field_advance_families", "field_advance_requests", "field_advance_payment_accounts",
            "field_advance_payment_proposals", "field_advance_payment_holds", "field_advance_payment_participations",
            "field_advance_payment_events", "field_advance_command_receipts", "field_advance_reservation_entries",
            "field_advance_payment_results", "audit_events", "audit_event_metadata" })
        {
            // Identifiers come only from the fixed test-owned table list above.
            var sql = "SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text), '[]')::text AS \"Value\" FROM " + table + " t";
            snapshots.Add(await db.Database.SqlQueryRaw<string>(sql).SingleAsync());
        }
        return string.Join("\n", snapshots);
    }

    private static async Task<RequestView> PaidRoot(FieldAdvanceFixture f, EvidenceFiles files)
    {
        var root = await Original(f); var id = await Reviewed(f, root.Id);
        Value(await Payment(f, root.Id, id, "Start"));
        Value(await Record(f, files, root.Id, id)); return root;
    }
    private static async Task<FinanceResult<PaymentView>> Record(FieldAdvanceFixture f, EvidenceFiles files, Guid request, Guid id,
        PaymentCommand? meta = null, Guid? evidenceId = null)
    {
        evidenceId ??= await Evidence(f, files, request);
        await using var db = f.CreateContext(); using var provider = await Provider(f, db, Payer);
        var evidence = new FinanceEvidenceService(db, provider.GetRequiredService<PaymentAccess>(), files.Store, new RejectPdf(), files.Options, f.Clock);
        var service = new PaymentExecutionService(db, provider.GetRequiredService<PaymentCommandExecutor>(), new(db, f.Clock),
            provider.GetRequiredService<FinancePersonResolver>(), evidence, files.Options, f.Clock);
        return await service.RecordAsync(Payer, id, new("2000", AuthorPersonId, new(2026, 10, 24), "จ่ายทดสอบ", [evidenceId.Value]),
            meta ?? await Meta(f, request, id), default);
    }

    private static async Task<Guid> Evidence(FieldAdvanceFixture f, EvidenceFiles files, Guid request)
    {
        var key = Guid.NewGuid().ToString("N"); var stored = await files.Store.WriteImmutableAsync(files.Options.ActiveStorageId, key, new MemoryStream([1, 2, 3]), default);
        await using var db = f.CreateContext(); var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request); var id = Guid.NewGuid();
        db.Add(new AdvanceEvidence
        {
            Id = id,
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            FamilyId = r.FamilyId,
            RequestId = request,
            Sha256 = stored.Sha256,
            Length = stored.Length,
            ContentType = "image/png",
            State = "Ready",
            CreatedBy = Payer,
            CreatedByPersonId = Payer,
            CreatedAtUtc = f.Clock.GetUtcNow()
        });
        db.Add(new AdvanceEvidenceLocation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            FamilyId = r.FamilyId,
            RequestId = request,
            EvidenceId = id,
            StorageKey = new EvidenceLocator(files.Options.ActiveStorageId, key).ToString(),
            IsCurrent = true,
            VerifiedAtUtc = f.Clock.GetUtcNow(),
            CreatedAtUtc = f.Clock.GetUtcNow()
        });
        await db.SaveChangesAsync(); return id;
    }
    private static async Task<FinanceResult<PaymentView>> Recover(FieldAdvanceFixture f, EvidenceFiles files, Guid request, Guid id, Guid evidenceId, string outcome, Guid? proposal = null)
    {
        var actor = outcome == "Confirm" ? Checker : Payer; await using var db = f.CreateContext(); using var provider = await Provider(f, db, actor);
        var evidence = new FinanceEvidenceService(db, provider.GetRequiredService<PaymentAccess>(), files.Store, new RejectPdf(), files.Options, f.Clock);
        var service = new PaymentOutcomeService(db, provider.GetRequiredService<PaymentCommandExecutor>(), new(db, f.Clock), provider.GetRequiredService<FinancePersonResolver>(), evidence, files.Options, f.Clock);
        var meta = await Meta(f, request, id);
        return outcome == "Confirm" ? await service.ConfirmAsync(actor, proposal!.Value, meta, default)
            : await service.ProposeAsync(actor, id, new(outcome, null, [evidenceId]), meta, default);
    }
    private sealed class RejectPdf : IFinancePdfValidator
    { public Task<bool> ValidateAsync(byte[] content, CancellationToken ct) => Task.FromResult(false); }
    private sealed class EvidenceFiles : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "task11-evidence-" + Guid.NewGuid().ToString("N"));
        public FinanceEvidenceOptions Options { get; }
        public FolderFinanceEvidenceStore Store { get; }
        public EvidenceFiles()
        {
            root = OperatingSystem.IsMacOS() && root.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + root : root;
            Directory.CreateDirectory(root); var id = Guid.NewGuid();
            Options = new() { ActiveStorageId = id, Roots = new() { [id] = root } }; Store = new(Options);
        }
        public void Dispose() => Directory.Delete(root, true);
    }

    private static async Task Revoke(FieldAdvanceFixture f, Guid actor)
    {
        await using var db = f.CreateContext();
        var proposal = await db.Set<GrantProposal>().SingleAsync(x => x.RecipientId == actor && x.ProjectId == ProjectId);
        var assignment = await db.Set<GrantAssignment>().SingleAsync(x => x.ProposalId == proposal.Id);
        assignment.RevokedAtUtc = f.Clock.GetUtcNow(); assignment.RevokedBy = AuthorId; assignment.RevocationReason = "ถอนสิทธิ์ทดสอบ";
        await db.SaveChangesAsync();
    }

    private static async Task SetExpiry(FieldAdvanceFixture f, Guid actor, DateTimeOffset at)
    {
        await using var db = f.CreateContext(); await db.Set<GrantProposal>().Where(x => x.RecipientId == actor && x.ProjectId == ProjectId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.EndsAtUtc, at));
    }

    private async Task<FieldAdvanceFixture> Ready()
    {
        var f = await L.Ready(postgres);
        await using (var db = f.CreateContext())
        {
            await db.Set<GrantProposal>().Where(x => x.ProjectId == ProjectId && (x.RecipientId == ReviewerId || x.RecipientId == RecipientId))
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.ApprovalLimit, 100000m));
        }
        // Publish a genuine current policy covering the larger ceiling.
        PolicyView company;
        await using (var db = f.CreateContext())
        {
            var review = await (from r in db.Set<PolicyReview>()
                                join a in db.Set<ActivePolicy>() on r.PolicyRevisionId equals a.PolicyRevisionId
                                where a.ProjectId == null
                                select r).SingleAsync();
            company = JsonSerializer.Deserialize<PolicyView>(review.ReviewedContentJson!)!;
        }
        await P.Review(f, await P.Submit(f, await P.Create(f, source: company, active: company,
            content: P.Content() with { Bands = [new(1, "director", 100000m)] })));
        await Actor(f, Director, "request.approve", "director");
        await Actor(f, Payer, "payment.record"); await Actor(f, Checker, "finance.check"); return f;
    }
    private static async Task Actor(FieldAdvanceFixture f, Guid actor, string capability, string? level = null, Guid? personId = null)
    {
        var person = personId ?? actor;
        await using var db = f.CreateContext();
        await db.Database.ExecuteSqlAsync($"INSERT INTO users(id,username,normalized_username) VALUES ({actor},{actor.ToString()},{actor.ToString()})");
        await db.Database.ExecuteSqlAsync($"INSERT INTO employee_memberships(id,user_id,workspace_id,department_id,valid_from_utc,created_by,reason) SELECT gen_random_uuid(),{actor},workspace_id,department_id,valid_from_utc,created_by,'task11' FROM employee_memberships WHERE user_id={AuthorId}");
        if (personId is null) db.Add(new FinancePerson { Id = actor, WorkspaceId = WorkspaceId, CreatedBy = AuthorId, Reason = "ทดสอบ" });
        db.Add(new FinanceAccountLink { Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, UserId = actor, PersonId = person, CreatedBy = AuthorId, CreatedAtUtc = f.Clock.GetUtcNow().AddMinutes(-1), Reason = "ทดสอบ" });
        await db.SaveChangesAsync();
        var grant = await FieldAdvanceGrantTests.AuthorityAsync(f, actor, person, limit: 100000m);
        var assignment = await db.Set<GrantAssignment>().SingleAsync(x => x.Id == grant);
        var proposal = await db.Set<GrantProposal>().SingleAsync(x => x.Id == assignment.ProposalId);
        proposal.Capabilities = [capability]; proposal.ApprovalLevels = level is null ? [] : [level];
        proposal.PrimaryApprovalLevels = level is null ? [] : [level]; await db.SaveChangesAsync();
    }
    private static RequestContent Content(int quantity) => new("Individual", "ปรับวงเงิน", new(2026, 10, 24), new(2026, 10, 25), new(2026, 10, 23), [],
        [new(Guid.NewGuid(), Guid.Parse("78000000-0000-0000-0000-000000000001"), "ค่าเดินทาง", "QuantityRate", quantity.ToString(), null, null)]);
    private static async Task<RequestView> Original(FieldAdvanceFixture f)
    {
        var session = await f.SessionAsync(AuthorId); await using var db = f.CreateContext();
        var draft = await L.Draft(f, db, session).CreateAsync(AuthorId, new(new(WorkspaceId, ProjectId), AuthorPersonId, Content(100)), L.Meta(0), default);
        Assert.Equal(201, draft.Status);
        return await Approve(f, await L.Submit(f, draft.Value!));
    }
    private static async Task<RequestView> Approve(FieldAdvanceFixture f, RequestView view)
    {
        foreach (var actor in new[] { ReviewerId, RecipientId, Director })
        { var result = await L.Decide(f, view, "Approve", actor); Assert.Equal(200, result.Status); view = result.Value!; }
        Assert.Equal("Approved", view.State); return view;
    }
    private static async Task<RequestView> View(FieldAdvanceFixture f, Guid id)
    {
        await using var db = f.CreateContext(); var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == id);
        var v = await db.Set<AdvanceRequestRevision>().SingleAsync(x => x.RequestId == id && x.IsCurrent);
        return new(id, v.Id, r.Version, r.State, new(r.WorkspaceId, r.ProjectId), r.OwnerPersonId, r.DelegatePersonId,
            JsonSerializer.Deserialize<RequestContent>(v.ContentJson)!, null, r.HoldCode);
    }
    private static async Task<AdvancePaymentAccount> Account(FieldAdvanceFixture f)
    { await using var db = f.CreateContext(); return await db.Set<AdvancePaymentAccount>().SingleAsync(); }
    private static async Task<PaymentCommand> Meta(FieldAdvanceFixture f, Guid id, Guid? installment = null)
    {
        await using var db = f.CreateContext(); var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == id);
        return new(Guid.NewGuid(), (await db.Set<AdvanceFamily>().SingleAsync(x => x.Id == r.FamilyId)).Version, r.Version,
            installment is null ? null : (await db.Set<AdvanceInstallment>().SingleAsync(x => x.Id == installment)).Version, "ทดสอบปรับวงเงิน");
    }
    private static PaymentView Value(FinanceResult<PaymentView> result)
    { Assert.Equal(200, result.Status); return Assert.IsType<PaymentView>(result.Value); }
    private static async Task<Guid> Proposal(FieldAdvanceFixture f, string kind)
    { await using var db = f.CreateContext(); return await db.Set<AdvancePaymentProposal>().Where(x => x.Kind == kind).OrderByDescending(x => x.RequestVersion).Select(x => x.Id).FirstAsync(); }
    private static async Task<Guid> Reviewed(FieldAdvanceFixture f, Guid request)
    {
        var id = Value(await Payment(f, request, null, "Create")).InstallmentId!.Value;
        Value(await Payment(f, request, id, "Submit")); Value(await Payment(f, request, id, "Review")); return id;
    }
    private static async Task Cancel(FieldAdvanceFixture f, Guid request, Guid id)
    {
        Value(await Payment(f, request, id, "CancelPropose"));
        Value(await Payment(f, request, id, "CancelConfirm", proposal: await Proposal(f, "PaymentCancellation")));
    }
    private static async Task<ServiceProvider> Provider(FieldAdvanceFixture f, Tpr10DbContext db, Guid actor, Action? auditAction = null)
    {
        var session = await f.SessionAsync(actor); var access = FieldAdvanceAccessTests.Access(db, session, f.Clock); var people = new FinancePersonResolver(db, f.Clock);
        var paymentAccess = new PaymentAccess(db, access, people, new(db, access, people, f.Clock), f.Clock);
        var services = new ServiceCollection().AddSingleton(db).AddSingleton(access).AddSingleton(people)
            .AddSingleton(new RequestAccess(db, access, people, f.Clock)).AddSingleton(L.Draft(f, db, session))
            .AddSingleton(new ApprovalRouteResolver(db, people, f.Clock)).AddSingleton<TimeProvider>(f.Clock)
            .AddSingleton<IAuditEventWriter>(new CallbackAudit(new AuditEventWriter(db, new CorrelationContext(), f.Clock), auditAction))
            .AddSingleton(paymentAccess).AddSingleton(new FamilyParticipation(db, session, access, people, f.Clock))
            .AddSingleton(new ReservationLedger(db, f.Clock)).AddTransient<PaymentCommandExecutor>();
        return services.BuildServiceProvider();
    }
    private static async Task<FinanceResult<PaymentView>> Disposition(FieldAdvanceFixture f, Guid request, string method, object input, Guid? actor = null, Action? auditAction = null, PaymentCommand? meta = null)
    {
        var who = actor ?? (method == "Propose" ? Checker : Director);
        await using var db = f.CreateContext(); using var provider = await Provider(f, db, who, auditAction);
        var service = ActivatorUtilities.CreateInstance<PaymentDispositionService>(provider); meta ??= await Meta(f, request);
        return method switch
        {
            "Propose" => await service.ProposeAsync(who, request, (string)input, meta, default),
            "Confirm" => await service.ConfirmAsync(who, (Guid)input, meta, default),
            "Finalize" => await service.FinalizeAsync(who, (Guid)input, meta, default),
            _ => throw new ArgumentOutOfRangeException(nameof(method))
        };
    }
    private static async Task<FinanceResult<PaymentView>> Payment(FieldAdvanceFixture f, Guid request, Guid? id, string operation, PaymentCommand? meta = null, Guid? proposal = null)
    {
        var actor = operation is "Review" or "CancelConfirm" ? Checker : Payer;
        await using var db = f.CreateContext(); using var provider = await Provider(f, db, actor);
        var executor = provider.GetRequiredService<PaymentCommandExecutor>(); var people = provider.GetRequiredService<FinancePersonResolver>(); var ledger = new ReservationLedger(db, f.Clock);
        var service = new InstallmentService(db, executor, ledger, f.Clock); meta ??= await Meta(f, request, id);
        var cancellation = new PaymentCancellationService(db, executor, ledger, people, null!, new(), f.Clock);
        return operation switch
        {
            "Create" => await service.CreateAsync(actor, request, new("2000", new("Cash", AuthorPersonId, null, null, null)), meta, default),
            "Submit" => await service.SubmitAsync(actor, id!.Value, meta, default),
            "Review" => await new InstallmentReviewService(db, executor, people, f.Clock).ReviewAsync(actor, id!.Value, "Approve", meta, default),
            "CancelPropose" => await cancellation.ProposeAsync(actor, id!.Value, meta, default),
            "CancelConfirm" => await cancellation.ConfirmAsync(actor, proposal!.Value, meta, default),
            _ => await new PaymentExecutionService(db, executor, ledger, people, null!, new(), f.Clock).StartAsync(actor, id!.Value, meta, default)
        };
    }
    private sealed class CallbackAudit(IAuditEventWriter inner, Action? action) : IAuditEventWriter
    {
        public Task WriteAsync(string eventType, Guid targetId, IReadOnlyDictionary<string, string> metadata, CancellationToken ct, Guid? actorId = null)
            => inner.WriteAsync(eventType, targetId, metadata, ct, actorId);
        public async Task WriteAsync(SecurityAuditRequest request, CancellationToken ct)
        { await inner.WriteAsync(request, ct); action?.Invoke(); }
    }

}
