using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using System.Text.Json;
using TPR10.Api.Data;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.FieldAdvances;
using static TPR10.Api.IntegrationTests.FieldAdvanceRefundTests;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Settlements;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementAccessTests;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementCommandTests;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementReviewTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceRefundCorrectionTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Approved_refund_correction_checker_can_upload_fresh_proof(bool revokeDuringIo)
    {
        await using var f = await Ready(postgres);
        var (request, evidence) = await RefundReady(f);
        var created = await RefundCall(f, request, "Propose", input: RefundInputFor(2000, evidence));
        Assert.Equal(201, created.Status);
        var refund = created.Value!.EntityId!.Value;
        Assert.Equal(200, (await RefundCall(f, request, "Confirm", refund)).Status);
        Assert.Equal(200, (await RefundCall(f, request, "Approve", refund, actor: ReviewerId)).Status);
        using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        await using var db = f.CreateContext();
        Assert.Equal("Approved", (await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request)).State);
        var checker = await FieldAdvanceSettlementEvidenceTests.Service(f, db, files, ReviewerId);
        var bytes = FieldAdvanceSettlementEvidenceTests.Image();
        Assert.Equal(403, (await checker.UploadAsync(ReviewerId, request, new MemoryStream(bytes), "image/png", default)).Status);
        Assert.Equal(403, (await checker.UploadAdjustmentAsync(ReviewerId, request, new CorrectionEvidenceTarget(Guid.NewGuid()), new MemoryStream(bytes), "image/png", default)).Status);
        var owner = await FieldAdvanceSettlementEvidenceTests.Service(f, db, files, AuthorId);
        Assert.Equal(403, (await owner.UploadAdjustmentAsync(AuthorId, request, new CorrectionEvidenceTarget(refund), new MemoryStream(bytes), "image/png", default)).Status);
        var beforeEvidence = await db.Set<AdvanceEvidence>().CountAsync();
        if (revokeDuringIo)
            checker = await FieldAdvanceSettlementEvidenceTests.Service(f, db, files, ReviewerId,
                new FieldAdvanceSettlementEvidenceTests.ObservedStore(files.Store,
                    () => FieldAdvanceSettlementDeliverySupport.Revoke(f, ReviewerId), () => Assert.Null(db.Database.CurrentTransaction)));
        var uploaded = await checker.UploadAdjustmentAsync(ReviewerId, request, new CorrectionEvidenceTarget(refund), new MemoryStream(bytes), "image/png", default);
        if (revokeDuringIo)
        {
            Assert.Equal(403, uploaded.Status);
            Assert.Equal(beforeEvidence, await db.Set<AdvanceEvidence>().CountAsync());
            return;
        }
        Assert.Equal(201, uploaded.Status);
        Assert.Equal(201, (await Correction(f, request, refund, uploaded.Value, 1500)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_correction_confirmation_rolls_back_case_acceptance_money_receipt_and_audit(bool revoke)
    {
        await using var f = await Ready(postgres); var (request, refund, evidence) = await Closed(f);
        var proposed = await Correction(f, request, refund, evidence, 1500); Assert.Equal(201, proposed.Status);
        var id = proposed.Value!.EntityId!.Value;
        var before = await FieldAdvancePaymentRaceTests.Snapshot(f);
        await using var db = f.CreateContext(); using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        var audit = new FieldAdvanceRequestRaceTests.CallbackAudit(new AuditEventWriter(db, new CorrelationContext(), f.Clock), async () =>
        {
            if (!revoke) throw new InvalidOperationException("task9 synthetic audit failure");
            await db.Set<GrantAssignment>().Where(x => db.Set<GrantProposal>().Any(p => p.Id == x.ProposalId && p.RecipientId == RecipientId)
                && x.RevokedAtUtc == null).ExecuteUpdateAsync(set => set.SetProperty(x => x.RevokedAtUtc, f.Clock.GetUtcNow())
                    .SetProperty(x => x.RevokedBy, AuthorId).SetProperty(x => x.RevocationReason, "ถอนสิทธิ์ในธุรกรรมจำลอง"));
        });
        var service = new RefundCorrectionService(db, await Access(f, db, RecipientId), await Executor(f, db, RecipientId, audit), f.Clock, files.Options);
        var command = await Meta(f, request, 1);
        if (revoke) Assert.Equal(403, (await service.ConfirmAsync(RecipientId, request, id, command, default)).Status);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfirmAsync(RecipientId, request, id, command, default));
        Assert.Equal(before, await FieldAdvancePaymentRaceTests.Snapshot(f));
        Assert.Null((await db.Set<RefundCorrection>().AsNoTracking().SingleAsync()).AcceptedAtUtc);
        Assert.Null((await db.Set<SettlementAdjustmentCase>().AsNoTracking().SingleAsync()).AcceptedAtUtc);
        Assert.Equal(0m, (await Totals(f, request)).OwnerOutstanding);
    }

    [Fact]
    public async Task Task9_services_resolve_scoped_through_actual_application_registration()
    {
        await using var f = await Ready(postgres); using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        await using var first = factory.Services.CreateAsyncScope();
        await using var second = factory.Services.CreateAsyncScope();
        var correction = first.ServiceProvider.GetRequiredService<RefundCorrectionService>();
        var adjustment = first.ServiceProvider.GetRequiredService<AdjustmentCaseService>();
        Assert.Same(correction, first.ServiceProvider.GetRequiredService<RefundCorrectionService>());
        Assert.Same(adjustment, first.ServiceProvider.GetRequiredService<AdjustmentCaseService>());
        Assert.NotSame(correction, second.ServiceProvider.GetRequiredService<RefundCorrectionService>());
        Assert.NotSame(adjustment, second.ServiceProvider.GetRequiredService<AdjustmentCaseService>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Identical_payment_bytes_across_requests_are_reserved_and_accepted_only_once(bool confirmLegacyProposal)
    {
        await using var f = await Ready(postgres); await Payer(f);
        var a = await NegativeCase(f); var b = await NegativeCase(f);
        await AssertDistinctSourcesWithIdenticalBytes(f, a.Request, a.Evidence, b.Request, b.Evidence);
        var proposed = await CaseCall(f, a.Request, "Propose", a.Case, evidence: a.Evidence); Assert.Equal(201, proposed.Status);
        Assert.Equal(200, (await CaseCall(f, a.Request, "Confirm", proposed.Value!.EntityId!.Value)).Status);
        FinanceResult<SettlementCommandResult> duplicate;
        if (confirmLegacyProposal)
        {
            // A pre-fix pending row can already share bytes with an accepted fact elsewhere.
            await using var db = f.CreateContext(); var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == b.Request);
            var evidence = await db.Set<AdvanceEvidence>().SingleAsync(x => x.Id == b.Evidence);
            var row = await Row<SettlementCasePayment>(f, db, request, AliasAccountId, FieldAdvancePaymentFixture.PayerPersonId);
            row.CaseId = b.Case; row.Amount = 500; row.PaidOn = new(2026, 10, 20); row.EvidenceId = b.Evidence; row.State = "Pending";
            row.SnapshotJson = JsonSerializer.Serialize(new CasePaymentSnapshot(1, new(b.Case, 500, row.PaidOn, [b.Evidence]),
                [new(b.Evidence, evidence.Sha256, evidence.Length, evidence.ContentType)], []));
            db.Add(row); await db.SaveChangesAsync();
            duplicate = await CaseCall(f, b.Request, "Confirm", row.Id);
        }
        else duplicate = await CaseCall(f, b.Request, "Propose", b.Case, evidence: b.Evidence);
        Assert.Equal(409, duplicate.Status); Assert.Equal("case-payment-proof-duplicate", duplicate.Code); Assert.Null(duplicate.Value);
        var response = JsonSerializer.Serialize(duplicate);
        foreach (var id in new[] { a.Request, a.Case, a.Evidence, proposed.Value!.EntityId!.Value }) Assert.DoesNotContain(id.ToString(), response);
        Assert.Equal(0m, (await Totals(f, a.Request)).OwnerOutstanding); Assert.Equal(-500m, (await Totals(f, b.Request)).OwnerOutstanding);
        await using var verify = f.CreateContext(); Assert.Single(await verify.Set<SettlementCasePayment>().Where(x => x.AcceptedAtUtc != null).ToArrayAsync());
    }

    [Theory]
    [InlineData("Cancel")]
    [InlineData("Reject")]
    public async Task Released_payment_proof_can_move_requests_but_an_accepted_fact_cannot(string release)
    {
        await using var f = await Ready(postgres); await Payer(f);
        var a = await NegativeCase(f); var b = await NegativeCase(f);
        var proposed = await CaseCall(f, a.Request, "Propose", a.Case, evidence: a.Evidence); Assert.Equal(201, proposed.Status);
        Assert.Equal(409, (await CaseCall(f, b.Request, "Propose", b.Case, evidence: b.Evidence)).Status);
        Assert.Equal(200, (await CaseCall(f, a.Request, release, proposed.Value!.EntityId!.Value)).Status);
        var reused = await CaseCall(f, b.Request, "Propose", b.Case, evidence: b.Evidence); Assert.Equal(201, reused.Status);
        Assert.Equal(200, (await CaseCall(f, b.Request, "Confirm", reused.Value!.EntityId!.Value)).Status);
        Assert.Equal(409, (await CaseCall(f, b.Request, release, reused.Value.EntityId.Value)).Status);
        Assert.Equal(409, (await CaseCall(f, a.Request, "Propose", a.Case, evidence: a.Evidence)).Status);
    }

    [Fact]
    public async Task Identical_payment_proof_cross_family_race_has_one_claim_without_leaking_the_other_family()
    {
        await using var f = await Ready(postgres); await Payer(f);
        var a = await NegativeCase(f); var b = await NegativeCase(f);
        await AssertDistinctSourcesWithIdenticalBytes(f, a.Request, a.Evidence, b.Request, b.Evidence);
        var gate = new FieldAdvancePaymentRaceTests.Barrier();
        var winner = CaseCall(f, a.Request, "Propose", a.Case, evidence: a.Evidence, barrier: gate);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var loser = CaseCall(f, b.Request, "Propose", b.Case, evidence: b.Evidence);
        try { await WaitingForProofOrIdentity(f, loser); } finally { gate.Release.TrySetResult(); }
        Assert.Equal(201, (await winner).Status); var denied = await loser;
        Assert.Equal(409, denied.Status); Assert.Equal("case-payment-proof-duplicate", denied.Code); Assert.Null(denied.Value);
        await using var db = f.CreateContext(); Assert.Single(await db.Set<SettlementCasePayment>().ToArrayAsync());
    }

    [Fact]
    public async Task Payment_proof_lock_is_acquired_before_participation_family_and_request_locks()
    {
        await using var f = await Ready(postgres); await Payer(f); var a = await NegativeCase(f);
        await using var holder = f.CreateContext(); await using var held = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241006)");
        var pending = CaseCall(f, a.Request, "Propose", a.Case, evidence: a.Evidence);
        try
        {
            await using var observer = f.CreateContext(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!await observer.Database.SqlQueryRaw<bool>("SELECT EXISTS(SELECT 1 FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid WHERE a.datname=current_database() AND l.locktype='advisory' AND l.objid=7241006 AND NOT l.granted) AS \"Value\"").SingleAsync(deadline.Token))
            { Assert.False(pending.IsCompleted, "Payment must wait for proof lock before entering family locking"); await Task.Yield(); }
            await using var probe = f.CreateContext(); await using var tx = await probe.Database.BeginTransactionAsync();
            Assert.True(await probe.Database.SqlQueryRaw<bool>("SELECT pg_try_advisory_xact_lock(7241002) AS \"Value\"").SingleAsync());
        }
        finally { await held.RollbackAsync(); }
        Assert.Equal(201, (await pending).Status);
    }

    private static async Task WaitingForProofOrIdentity(FieldAdvanceFixture f, Task command)
    {
        await using var observer = f.CreateContext(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!await observer.Database.SqlQueryRaw<bool>("SELECT EXISTS(SELECT 1 FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid WHERE a.datname=current_database() AND l.locktype='advisory' AND l.objid IN (7241002,7241006) AND NOT l.granted) AS \"Value\"").SingleAsync(deadline.Token))
        { Assert.False(command.IsCompleted, "Expected a real PostgreSQL advisory lock wait"); await Task.Yield(); }
    }
    internal static async Task Payer(FieldAdvanceFixture f)
    {
        await using var db = f.CreateContext(); var person = FieldAdvancePaymentFixture.PayerPersonId;
        await db.Database.ExecuteSqlAsync($"INSERT INTO finance_persons(id,workspace_id,created_by,reason) VALUES ({person},{WorkspaceId},{AuthorId},'task9 payer')");
        await db.Database.ExecuteSqlAsync($"INSERT INTO finance_account_links(id,workspace_id,user_id,person_id,created_by,created_at_utc,reason) VALUES (gen_random_uuid(),{WorkspaceId},{AliasAccountId},{person},{AuthorId},{f.Clock.GetUtcNow()},'task9 payer')");
        await Grant(f, AliasAccountId, person, "payment.record");
    }
    internal static async Task<(Guid Request, Guid Case, Guid Evidence)> NegativeCase(FieldAdvanceFixture f)
    {
        var (request, refund, evidence) = await Closed(f);
        await using (var db = f.CreateContext())
        {
            var bytes = FieldAdvanceSettlementEvidenceTests.Image(); var row = await db.Set<AdvanceEvidence>().SingleAsync(x => x.Id == evidence);
            row.Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)); row.Length = bytes.Length; await db.SaveChangesAsync();
        }
        var correction = await Correction(f, request, refund, evidence, 2500); Assert.Equal(201, correction.Status);
        Assert.Equal(200, (await ConfirmCorrection(f, request, correction.Value!.EntityId!.Value, RecipientId)).Status);
        await using var read = f.CreateContext(); return (request, (await read.Set<SettlementAdjustmentCase>().SingleAsync(x => x.RequestId == request)).Id, evidence);
    }
    private static async Task AssertDistinctSourcesWithIdenticalBytes(FieldAdvanceFixture f, Guid a, Guid ea, Guid b, Guid eb)
    {
        await using var db = f.CreateContext(); Assert.NotEqual(a, b); Assert.NotEqual(ea, eb);
        var ra = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == a); var rb = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == b);
        Assert.Equal(ra.WorkspaceId, rb.WorkspaceId); Assert.NotEqual(ra.FamilyId, rb.FamilyId);
        Assert.Equal((await db.Set<AdvanceEvidence>().SingleAsync(x => x.Id == ea)).Sha256, (await db.Set<AdvanceEvidence>().SingleAsync(x => x.Id == eb)).Sha256);
    }

    [Fact]
    public async Task Rooted_case_requires_complete_baseline_even_before_acceptance()
    {
        await using var f = await Ready(postgres); var (request, refund, evidence) = await Closed(f);
        var correction = await Correction(f, request, refund, evidence, 1500); Assert.Equal(201, correction.Status);
        await using var db = f.CreateContext();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE field_advance_settlement_adjustment_cases SET baseline_version=NULL WHERE accepted_at_utc IS NULL"));
        Assert.Equal("23514", error.SqlState);
        Assert.NotNull((await db.Set<SettlementAdjustmentCase>().AsNoTracking().SingleAsync()).BaselineVersion);
    }

    [Fact]
    public async Task Corrected_net_keeps_incident_replacement_and_case_refund_cannot_exceed_actual_owner_liability()
    {
        await using var f = await Ready(postgres); var (request, refund, evidence) = await Closed(f);
        var correction = await Correction(f, request, refund, evidence, 1500); Assert.Equal(201, correction.Status);
        Assert.Equal(200, (await ConfirmCorrection(f, request, correction.Value!.EntityId!.Value, RecipientId)).Status);
        await SeedIncident(f, request, evidence, 1500, 500);
        var totals = await Totals(f, request);
        Assert.Equal(1500m, totals.OwnerReceived); Assert.Equal(1500m, totals.ConfirmedRefund);
        Assert.Equal(0m, totals.OwnerOutstanding); Assert.Equal(500m, totals.WrongRecipientOutstanding);
        await using var db = f.CreateContext(); var caseId = (await db.Set<SettlementAdjustmentCase>().SingleAsync()).Id;
        Assert.Equal(409, (await RefundCall(f, request, "Propose", input: RefundInputFor(500, evidence) with { AdjustmentCaseId = caseId })).Status);
    }

    [Fact]
    public async Task Closed_case_evidence_upload_rechecks_current_authority_after_storage_IO()
    {
        await using var f = await Ready(postgres); var (request, refund, evidence) = await Closed(f);
        var correction = await Correction(f, request, refund, evidence, 1500); Assert.Equal(201, correction.Status);
        await using var db = f.CreateContext(); var caseId = (await db.Set<SettlementAdjustmentCase>().SingleAsync()).Id;
        var before = await db.Set<AdvanceEvidence>().CountAsync(); using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        var store = new FieldAdvanceSettlementEvidenceTests.ObservedStore(files.Store, async () =>
        {
            await using var other = f.CreateContext();
            await other.Set<GrantAssignment>().Where(x => other.Set<GrantProposal>().Any(p => p.Id == x.ProposalId && p.RecipientId == AuthorId)
                && x.RevokedAtUtc == null).ExecuteUpdateAsync(set => set.SetProperty(x => x.RevokedAtUtc, f.Clock.GetUtcNow())
                    .SetProperty(x => x.RevokedBy, ReviewerId).SetProperty(x => x.RevocationReason, "ถอนระหว่างอัปโหลดจำลอง"));
        }, () => Assert.Null(db.Database.CurrentTransaction));
        var service = await FieldAdvanceSettlementEvidenceTests.Service(f, db, files, AuthorId, store);
        var result = await service.UploadAdjustmentAsync(AuthorId, request, new CaseRefundEvidenceTarget(caseId),
            new MemoryStream(FieldAdvanceSettlementEvidenceTests.Image()), "image/png", default);
        Assert.Equal(403, result.Status); Assert.Equal(before, await db.Set<AdvanceEvidence>().CountAsync());
    }

    [Fact]
    public async Task Pending_correction_before_close_blocks_settlement_without_changing_money()
    {
        await using var f = await Ready(postgres); var (request, evidence) = await RefundReady(f);
        var refund = await RefundCall(f, request, "Propose", input: RefundInputFor(2000, evidence)); Assert.Equal(201, refund.Status);
        var id = refund.Value!.EntityId!.Value;
        Assert.Equal(200, (await RefundCall(f, request, "Confirm", id)).Status);
        Assert.Equal(200, (await RefundCall(f, request, "Approve", id, actor: ReviewerId)).Status);
        Assert.Equal(201, (await Correction(f, request, id, evidence, 1500)).Status);
        var totals = await Totals(f, request); Assert.Equal(0m, totals.OwnerOutstanding); Assert.True(totals.HasOpenCase);
        await using var db = f.CreateContext(); Assert.Empty(await db.Set<SettlementAdjustmentCase>().ToArrayAsync());
    }

    [Fact]
    public async Task Closed_source_without_closure_is_conflict_and_rolls_back_command()
    {
        await using var f = await Ready(postgres); var (request, evidence) = await RefundReady(f);
        var refund = await RefundCall(f, request, "Propose", input: RefundInputFor(2000, evidence)); Assert.Equal(201, refund.Status);
        var id = refund.Value!.EntityId!.Value;
        Assert.Equal(200, (await RefundCall(f, request, "Confirm", id)).Status);
        Assert.Equal(200, (await RefundCall(f, request, "Approve", id, actor: ReviewerId)).Status);
        await using var db = f.CreateContext(); (await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request)).State = "Closed"; await db.SaveChangesAsync();
        var count = await db.Set<FinanceCommandReceipt>().CountAsync();
        Assert.Equal(409, (await Correction(f, request, id, evidence, 1500)).Status);
        Assert.Equal(count, await db.Set<FinanceCommandReceipt>().CountAsync());
        Assert.Empty(await db.Set<RefundCorrection>().ToArrayAsync());
    }

    [Fact]
    public async Task Correction_replay_is_exact_and_revocation_precedes_replay()
    {
        await using var f = await Ready(postgres); var (request, refund, evidence) = await Closed(f);
        var command = await Meta(f, request);
        var first = await Correction(f, request, refund, evidence, 1500, command); Assert.Equal(201, first.Status);
        Assert.Equal(first, await Correction(f, request, refund, evidence, 1500, command));
        Assert.Equal(409, (await Correction(f, request, refund, evidence, 1400, command)).Status);
        await using var db = f.CreateContext();
        await db.Set<GrantAssignment>().Where(x => db.Set<GrantProposal>().Any(p => p.Id == x.ProposalId && p.RecipientId == ReviewerId) && x.RevokedAtUtc == null).ExecuteUpdateAsync(set => set
            .SetProperty(x => x.RevokedAtUtc, f.Clock.GetUtcNow()).SetProperty(x => x.RevokedBy, AuthorId).SetProperty(x => x.RevocationReason, "ถอนสิทธิ์จำลอง"));
        Assert.Equal(403, (await Correction(f, request, refund, evidence, 1500, command)).Status);
        Assert.Single(await db.Set<RefundCorrection>().ToArrayAsync());
    }

    [Fact]
    public async Task Correction_alias_of_proposer_is_not_a_different_checker()
    {
        await using var f = await Ready(postgres); var (request, refund, evidence) = await Closed(f);
        await using var db = f.CreateContext();
        await db.Database.ExecuteSqlAsync($"INSERT INTO finance_account_links(id,workspace_id,user_id,person_id,created_by,created_at_utc,reason) VALUES (gen_random_uuid(),{WorkspaceId},{AliasAccountId},{ReviewerPersonId},{AuthorId},{f.Clock.GetUtcNow()},'task9 alias')");
        await Grant(f, AliasAccountId, ReviewerPersonId, "finance.check");
        var first = await Correction(f, request, refund, evidence, 1500); Assert.Equal(201, first.Status);
        Assert.Equal(403, (await ConfirmCorrection(f, request, first.Value!.EntityId!.Value, AliasAccountId)).Status);
        Assert.Equal(200, (await ConfirmCorrection(f, request, first.Value.EntityId.Value, RecipientId)).Status);
    }

    [Theory]
    [InlineData("refund")]
    [InlineData("evidence")]
    [InlineData("missing")]
    [InlineData("not-ready")]
    public async Task Correction_rejects_foreign_or_changed_evidence_and_source_without_effects(string mode)
    {
        await using var f = await Ready(postgres); var (request, refund, evidence) = await Closed(f);
        await using var db = f.CreateContext();
        if (mode is "refund" or "evidence")
        {
            var other = await FieldAdvanceSettlementDraftTests.Approved(f);
            await FieldAdvanceSettlementDraftTests.Document(f, other);
            if (mode == "refund") request = other;
            else evidence = (await db.Set<AdvanceEvidence>().SingleAsync(x => x.RequestId == other)).Id;
        }
        else if (mode == "missing") evidence = Guid.NewGuid();
        else { (await db.Set<AdvanceEvidence>().SingleAsync(x => x.Id == evidence)).State = "Pending"; await db.SaveChangesAsync(); }
        var before = await db.Set<FinanceCommandReceipt>().CountAsync();
        var result = await Correction(f, request, refund, evidence, 1500);
        Assert.Contains(result.Status, new[] { 409, 422 });
        Assert.Equal(before, await db.Set<FinanceCommandReceipt>().CountAsync()); Assert.Empty(await db.Set<RefundCorrection>().ToArrayAsync());
    }

    [Fact]
    public async Task Competing_correction_confirmations_apply_one_delta_under_family_lock()
    {
        await using var f = await Ready(postgres); var (request, refund, evidence) = await Closed(f);
        var first = await Correction(f, request, refund, evidence, 1500); var second = await Correction(f, request, refund, evidence, 1000);
        Assert.Equal(201, first.Status); Assert.Equal(201, second.Status);
        var barrier = new FieldAdvancePaymentRaceTests.Barrier();
        var firstCommand = await Meta(f, request, 1); var secondCommand = await Meta(f, request, 1);
        var winner = ConfirmCorrection(f, request, first.Value!.EntityId!.Value, RecipientId, firstCommand, barrier);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var loser = ConfirmCorrection(f, request, second.Value!.EntityId!.Value, RecipientId, secondCommand);
        try { await WaitingForProofOrIdentity(f, loser); } finally { barrier.Release.TrySetResult(); }
        Assert.Equal(200, (await winner).Status); Assert.Equal(409, (await loser).Status);
        Assert.Equal(500m, (await Totals(f, request)).OwnerOutstanding);
        await using var db = f.CreateContext(); Assert.Single(await db.Set<RefundCorrection>().Where(x => x.AcceptedAtUtc != null).ToArrayAsync());
    }

    [Fact]
    public async Task Closed_adjustment_evidence_upload_is_typed_scoped_and_does_not_grant_owner_payer_role()
    {
        await using var f = await Ready(postgres); var (request, refund, evidence) = await Closed(f);
        using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        await using var db = f.CreateContext();
        var checker = await FieldAdvanceSettlementEvidenceTests.Service(f, db, files, ReviewerId);
        var bytes = FieldAdvanceSettlementEvidenceTests.Image();
        Assert.Equal(403, (await checker.UploadAsync(ReviewerId, request, new MemoryStream(bytes), "image/png", default)).Status);
        Assert.Equal(403, (await checker.UploadAdjustmentAsync(ReviewerId, request, new CorrectionEvidenceTarget(Guid.NewGuid()), new MemoryStream(bytes), "image/png", default)).Status);
        Assert.Equal(201, (await checker.UploadAdjustmentAsync(ReviewerId, request, new CorrectionEvidenceTarget(refund), new MemoryStream(bytes), "image/png", default)).Status);
        var correction = await Correction(f, request, refund, evidence, 1500); Assert.Equal(201, correction.Status);
        var caseId = (await db.Set<SettlementAdjustmentCase>().AsNoTracking().SingleAsync()).Id;
        var owner = await FieldAdvanceSettlementEvidenceTests.Service(f, db, files, AuthorId);
        Assert.Equal(201, (await owner.UploadAdjustmentAsync(AuthorId, request, new CaseRefundEvidenceTarget(caseId), new MemoryStream(bytes), "image/png", default)).Status);
        Assert.Equal(403, (await owner.UploadAdjustmentAsync(AuthorId, request, new CasePaymentEvidenceTarget(caseId), new MemoryStream(bytes), "image/png", default)).Status);
        Assert.DoesNotContain(await db.Set<AdvancePaymentParticipation>().AsNoTracking().ToArrayAsync(), x => x.PersonId == AuthorPersonId && x.Role == "Payer");
    }

    [Fact]
    public async Task Task9_migration_down_up_is_reversible_on_disposable_database_without_case_history()
    {
        await using var f = await Ready(postgres); await using var db = f.CreateContext();
        const string previous = "20261031221353_AllowMultipleDocumentDuplicateCases";
        const string current = "20261101125531_AddRefundCorrectionCases";
        await db.GetService<IMigrator>().MigrateAsync(previous);
        var tables = await db.Database.SqlQueryRaw<string>("""SELECT tablename AS "Value" FROM pg_tables WHERE schemaname='public'""").ToArrayAsync();
        Assert.DoesNotContain("field_advance_settlement_case_payments", tables);
        Assert.DoesNotContain("field_advance_settlement_case_closures", tables);
        await db.GetService<IMigrator>().MigrateAsync(current);
        await db.GetService<IMigrator>().MigrateAsync(current);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(0, await db.Set<SettlementCasePayment>().CountAsync());
        Assert.Equal(0, await db.Set<SettlementCaseClosure>().CountAsync());
    }

    [Fact]
    public async Task Closed_request_correction_uses_latest_net_version_once()
    {
        await using var f = await Ready(postgres);
        var (requestId, refundId, evidence) = await Closed(f);
        var before = await Preserved(f, requestId, refundId);
        var proposed = await Correction(f, requestId, refundId, evidence, 1500);
        Assert.Equal(201, proposed.Status);
        Assert.Equal(0m, (await Totals(f, requestId)).OwnerOutstanding);
        Assert.True((await Totals(f, requestId)).HasOpenCase);
        var id = proposed.Value!.EntityId!.Value;
        var stale = await Correction(f, requestId, refundId, evidence, 1000);
        Assert.Equal(201, stale.Status);
        var staleId = stale.Value!.EntityId!.Value;
        Assert.Equal(403, (await ConfirmCorrection(f, requestId, id, ReviewerId)).Status);
        Assert.Equal(200, (await ConfirmCorrection(f, requestId, id, RecipientId)).Status);
        var staleSecondCorrection = await ConfirmCorrection(f, requestId, staleId, RecipientId);
        Assert.Equal(409, staleSecondCorrection.Status);
        var adjustment = new { Outstanding = (await Totals(f, requestId)).OwnerOutstanding };
        Assert.Equal(500m, adjustment.Outstanding);
        await using var db = f.CreateContext();
        var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == requestId);
        var closures = await db.Set<ClosureRecord>().Where(x => x.RequestId == requestId).ToArrayAsync();
        Assert.Equal("Closed", request.State);
        Assert.Single(closures);
        Assert.Single(await db.Set<SettlementAdjustmentCase>().ToArrayAsync());
        Assert.Equal(before, await Preserved(f, requestId, refundId));
    }

    [Fact]
    public async Task Case_refund_requires_owner_and_checker_then_settles_only_its_case()
    {
        await using var f = await Ready(postgres); var (request, refund, evidence) = await Closed(f);
        var proposed = await Correction(f, request, refund, evidence, 1500); Assert.Equal(201, proposed.Status);
        Assert.Equal(200, (await ConfirmCorrection(f, request, proposed.Value!.EntityId!.Value, RecipientId)).Status);
        await using var db = f.CreateContext(); var caseId = (await db.Set<SettlementAdjustmentCase>().SingleAsync()).Id;
        var input = RefundInputFor(500, evidence) with { AdjustmentCaseId = caseId };
        var extra = await RefundCall(f, request, "Propose", input: input);
        Assert.Equal(201, extra.Status); var extraId = extra.Value!.EntityId!.Value;
        Assert.Equal(500m, (await Totals(f, request)).OwnerOutstanding);
        Assert.Equal(409, (await RefundCall(f, request, "Propose", input: input with { Amount = 1 })).Status);
        Assert.Equal(200, (await RefundCall(f, request, "Confirm", extraId)).Status);
        Assert.Equal(200, (await RefundCall(f, request, "Approve", extraId, actor: ReviewerId)).Status);
        Assert.Equal(0m, (await Totals(f, request)).OwnerOutstanding);
        Assert.Equal(2000m, (await Totals(f, request)).ConfirmedRefund);
        Assert.True((await Totals(f, request)).HasOpenCase);
        Assert.Equal(200, (await CaseCall(f, request, "Close", caseId)).Status);
        var closure = JsonSerializer.Serialize(await db.Set<SettlementCaseClosure>().AsNoTracking().SingleAsync());
        var correctionOfExtra = await Correction(f, request, extraId, evidence, 450);
        Assert.Equal(201, correctionOfExtra.Status);
        Assert.Equal(200, (await ConfirmCorrection(f, request, correctionOfExtra.Value!.EntityId!.Value, RecipientId)).Status);
        Assert.Equal(50m, (await Totals(f, request)).OwnerOutstanding);
        Assert.Equal(500m, (await db.Set<Refund>().AsNoTracking().SingleAsync(x => x.Id == extraId)).Amount);
        Assert.Equal(closure, JsonSerializer.Serialize(await db.Set<SettlementCaseClosure>().AsNoTracking().SingleAsync()));
    }

    [Fact]
    public async Task Negative_case_requires_payer_proof_and_checker_confirmation_then_new_case_excludes_closed_cashflows()
    {
        await using var f = await Ready(postgres); var (request, refund, evidence) = await Closed(f);
        await using (var setup = f.CreateContext())
        {
            var payer = FieldAdvancePaymentFixture.PayerPersonId;
            await setup.Database.ExecuteSqlAsync($"INSERT INTO finance_persons(id,workspace_id,created_by,reason) VALUES ({payer},{WorkspaceId},{AuthorId},'task9 payer')");
            await setup.Database.ExecuteSqlAsync($"INSERT INTO finance_account_links(id,workspace_id,user_id,person_id,created_by,created_at_utc,reason) VALUES (gen_random_uuid(),{WorkspaceId},{AliasAccountId},{payer},{AuthorId},{f.Clock.GetUtcNow()},'task9 payer')");
        }
        await Grant(f, AliasAccountId, FieldAdvancePaymentFixture.PayerPersonId, "payment.record");
        var preserved = await Preserved(f, request, refund);
        var correction = await Correction(f, request, refund, evidence, 2500); Assert.Equal(201, correction.Status);
        Assert.Equal(200, (await ConfirmCorrection(f, request, correction.Value!.EntityId!.Value, RecipientId)).Status);
        Assert.Equal(-500m, (await Totals(f, request)).OwnerOutstanding);
        await using (var check = f.CreateContext()) Assert.NotNull((await check.Set<SettlementAdjustmentCase>().SingleAsync()).AcceptedAtUtc);
        await using var db = f.CreateContext(); var caseId = (await db.Set<SettlementAdjustmentCase>().SingleAsync()).Id;
        Assert.Equal(409, (await CaseCall(f, request, "Close", caseId)).Status);
        var partialProof = await CaseCall(f, request, "Propose", caseId, evidence: evidence, amount: 250);
        Assert.Equal(201, partialProof.Status);
        Assert.Equal(409, (await CaseCall(f, request, "Propose", caseId, evidence: evidence, amount: 250)).Status);
        Assert.Equal(200, (await CaseCall(f, request, "Cancel", partialProof.Value!.EntityId!.Value)).Status);
        var proposed = await CaseCall(f, request, "Propose", caseId, evidence: evidence);
        Assert.Equal(201, proposed.Status); var paymentId = proposed.Value!.EntityId!.Value;
        Assert.Equal(-500m, (await Totals(f, request)).OwnerOutstanding);
        Assert.Equal(409, (await CaseCall(f, request, "Propose", caseId, evidence: evidence, amount: 1)).Status);
        Assert.Equal(200, (await CaseCall(f, request, "Return", paymentId)).Status);
        Assert.Equal(409, (await CaseCall(f, request, "Close", caseId)).Status);
        Assert.Equal(200, (await CaseCall(f, request, "Cancel", paymentId)).Status);
        proposed = await CaseCall(f, request, "Propose", caseId, evidence: evidence);
        Assert.Equal(201, proposed.Status); paymentId = proposed.Value!.EntityId!.Value;
        Assert.Equal(403, (await CaseCall(f, request, "Confirm", paymentId, actor: AliasAccountId)).Status);
        Assert.Equal(200, (await CaseCall(f, request, "Confirm", paymentId)).Status);
        Assert.Equal(0m, (await Totals(f, request)).OwnerOutstanding);
        Assert.Equal(200, (await CaseCall(f, request, "Close", caseId)).Status);
        Assert.False((await Totals(f, request)).HasOpenCase);
        foreach (var table in new[] { "field_advance_settlement_adjustment_cases", "field_advance_settlement_case_payments", "field_advance_settlement_case_closures", "field_advance_refunds", "field_advance_refund_corrections", "field_advance_closure_records" })
        {
            foreach (var sql in new[] { "UPDATE " + table + " SET version=version+1 WHERE accepted_at_utc IS NOT NULL", "DELETE FROM " + table + " WHERE accepted_at_utc IS NOT NULL", "TRUNCATE " + table + " CASCADE" })
            {
                var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
                Assert.Equal("23514", error.SqlState);
            }
        }
        var downgrade = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync("20261031221353_AllowMultipleDocumentDuplicateCases"));
        Assert.Equal("23514", downgrade.SqlState);
        // A rejected downgrade must leave the current Task10 schema usable, too.
        Assert.Equal(0, await db.Set<SettlementExcessCase>().CountAsync());
        Assert.NotNull((await db.Set<SettlementAdjustmentCase>().AsNoTracking().SingleAsync(x => x.Id == caseId)).RootRefundId);
        var next = await Correction(f, request, refund, evidence, 2300); Assert.Equal(201, next.Status);
        Assert.Equal(200, (await ConfirmCorrection(f, request, next.Value!.EntityId!.Value, RecipientId)).Status);
        var cases = await db.Set<SettlementAdjustmentCase>().AsNoTracking().ToArrayAsync(); Assert.Equal(2, cases.Length);
        var fresh = Assert.Single(cases, x => x.Id != caseId);
        Assert.Equal(2500m, fresh.BaselineAmount);
        Assert.Equal(200m, (await Totals(f, request)).OwnerOutstanding);
        var extra = await RefundCall(f, request, "Propose", input: RefundInputFor(200, evidence) with { AdjustmentCaseId = fresh.Id });
        Assert.Equal(201, extra.Status);
        Assert.Equal(200, (await RefundCall(f, request, "Confirm", extra.Value!.EntityId!.Value)).Status);
        Assert.Equal(200, (await RefundCall(f, request, "Approve", extra.Value.EntityId.Value, actor: ReviewerId)).Status);
        Assert.Equal(200, (await CaseCall(f, request, "Close", fresh.Id)).Status);
        Assert.Equal(0m, (await Totals(f, request)).OwnerOutstanding);
        Assert.False((await Totals(f, request)).HasOpenCase);
        Assert.Equal(preserved, await Preserved(f, request, refund));
    }

    private static async Task<FinanceResult<SettlementCommandResult>> CaseCall(FieldAdvanceFixture f, Guid request, string operation,
        Guid id, Guid? evidence = null, decimal amount = 500, Guid? actor = null, SettlementCommand? command = null, FieldAdvancePaymentRaceTests.Barrier? barrier = null)
    {
        var user = actor ?? (operation is "Propose" or "Cancel" ? AliasAccountId : ReviewerId);
        await using var db = f.CreateContext();
        using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        var service = new AdjustmentCaseService(db, await Access(f, db, user), await Executor(f, db, user, barrier?.Audit(f, true, db)), f.Clock, files.Options);
        var version = operation is "Confirm" or "Return" or "Reject" or "Cancel" ? (long?)(await db.Set<SettlementCasePayment>().AsNoTracking().SingleAsync(x => x.Id == id)).Version : null;
        var meta = command ?? await Meta(f, request, version);
        return operation switch
        {
            "Propose" => await service.ProposeCasePaymentAsync(user, request, new(id, amount, new(2026, 10, 20), [evidence!.Value]), meta, default),
            "Return" => await service.ReturnCasePaymentAsync(user, request, id, meta, default),
            "Reject" => await service.RejectCasePaymentAsync(user, request, id, meta, default),
            "Cancel" => await service.CancelCasePaymentAsync(user, request, id, meta, default),
            "Confirm" => await service.ConfirmCasePaymentAsync(user, request, id, meta, default),
            _ => await service.CloseCaseAsync(user, request, id, meta, default)
        };
    }

    [Fact]
    public async Task Returned_correction_can_be_superseded_and_cancelled_without_permanent_close_block()
    {
        await using var f = await Ready(postgres); var (request, refund, evidence) = await Closed(f);
        var proposed = await Correction(f, request, refund, evidence, 1500); Assert.Equal(201, proposed.Status);
        var id = proposed.Value!.EntityId!.Value;
        Assert.Equal(200, (await TransitionCorrection(f, request, id, "Return")).Status);
        await using var db = f.CreateContext(); var caseId = (await db.Set<SettlementAdjustmentCase>().SingleAsync()).Id;
        Assert.Equal(409, (await CaseCall(f, request, "Close", caseId)).Status);
        using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        var service = new RefundCorrectionService(db, await Access(f, db, ReviewerId), await Executor(f, db, ReviewerId), f.Clock, files.Options);
        var next = await service.ProposeAsync(ReviewerId, request, new(refund, 1600, [evidence]) { SupersedesId = id }, await Meta(f, request), default);
        Assert.Equal(201, next.Status);
        Assert.Equal("Superseded", (await db.Set<RefundCorrection>().AsNoTracking().SingleAsync(x => x.Id == id)).State);
        Assert.Equal(409, (await ConfirmCorrection(f, request, id, RecipientId)).Status);
        Assert.Equal(200, (await TransitionCorrection(f, request, next.Value!.EntityId!.Value, "Cancel")).Status);
        Assert.Equal(200, (await CaseCall(f, request, "Close", caseId)).Status);
        Assert.False((await Totals(f, request)).HasOpenCase);
        Assert.Equal(2000m, (await Totals(f, request)).ConfirmedRefund);
    }

    private static async Task<FinanceResult<SettlementCommandResult>> TransitionCorrection(FieldAdvanceFixture f, Guid request, Guid id, string operation)
    {
        var actor = operation == "Cancel" ? ReviewerId : RecipientId;
        await using var db = f.CreateContext(); using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        var service = new RefundCorrectionService(db, await Access(f, db, actor), await Executor(f, db, actor), f.Clock, files.Options);
        var meta = await Meta(f, request, (await db.Set<RefundCorrection>().AsNoTracking().SingleAsync(x => x.Id == id)).Version);
        return operation switch
        {
            "Return" => await service.ReturnAsync(actor, request, id, meta, default),
            "Reject" => await service.RejectAsync(actor, request, id, meta, default),
            _ => await service.CancelAsync(actor, request, id, meta, default)
        };
    }

    private static async Task<(Guid Request, Guid Refund, Guid Evidence)> Closed(FieldAdvanceFixture f)
    {
        var (request, evidence) = await RefundReady(f);
        var created = await RefundCall(f, request, "Propose", input: RefundInputFor(2000, evidence));
        Assert.Equal(201, created.Status); var refund = created.Value!.EntityId!.Value;
        Assert.Equal(200, (await RefundCall(f, request, "Confirm", refund)).Status);
        Assert.Equal(200, (await RefundCall(f, request, "Approve", refund, actor: ReviewerId)).Status);
        await Grant(f, RecipientId, RecipientPersonId, "finance.check");
        await using var db = f.CreateContext(); var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request);
        var completion = await Row<WorkCompletion>(f, db, r); completion.CompletedOn = new(2026, 10, 20);
        completion.ClearingDueOn = completion.RefundDueOn = completion.CompletedOn;
        Accept(completion, db, f); db.Add(completion); await db.SaveChangesAsync();
        var declaration = await Row<SubmissionDeclaration>(f, db, r); declaration.WorkCompletionId = completion.Id;
        Accept(declaration, db, f); db.Add(declaration); await db.SaveChangesAsync();
        var closure = await Row<ClosureRecord>(f, db, r); closure.WorkCompletionId = completion.Id;
        closure.DeclarationId = declaration.Id; closure.RequestVersion = r.Version;
        closure.FamilyVersion = (await db.Set<AdvanceFamily>().SingleAsync(x => x.Id == r.FamilyId)).Version;
        Accept(closure, db, f); db.Add(closure); r.State = "Closed"; await db.SaveChangesAsync();
        return (request, refund, evidence);
    }

    private static void Accept<T>(T row, Tpr10DbContext db, FieldAdvanceFixture f) where T : class
    {
        var e = db.Entry(row); e.Property("State").CurrentValue = "Accepted";
        e.Property("AcceptedAtUtc").CurrentValue = e.Property("ReviewedAtUtc").CurrentValue = f.Clock.GetUtcNow();
        e.Property("ReviewedBy").CurrentValue = ReviewerId; e.Property("ReviewedByPersonId").CurrentValue = ReviewerPersonId;
    }
    private static async Task<string> Preserved(FieldAdvanceFixture f, Guid request, Guid refund)
    {
        await using var db = f.CreateContext();
        return JsonSerializer.Serialize(new
        {
            Request = await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == request),
            Revisions = await db.Set<AdvanceRequestRevision>().AsNoTracking().Where(x => x.RequestId == request).OrderBy(x => x.Id).ToArrayAsync(),
            Refund = await db.Set<Refund>().AsNoTracking().SingleAsync(x => x.Id == refund),
            Accounts = await db.Set<AdvancePaymentAccount>().AsNoTracking().Where(x => x.RequestId == request).OrderBy(x => x.Id).ToArrayAsync(),
            Closure = await db.Set<ClosureRecord>().AsNoTracking().SingleAsync(x => x.RequestId == request)
        });
    }
    private static async Task<FinanceResult<SettlementCommandResult>> Correction(FieldAdvanceFixture f, Guid request, Guid refund,
        Guid evidence, decimal amount, SettlementCommand? command = null)
    {
        await using var db = f.CreateContext(); using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        var service = new RefundCorrectionService(db, await Access(f, db, ReviewerId), await Executor(f, db, ReviewerId), f.Clock, files.Options);
        return await service.ProposeAsync(ReviewerId, request, new(refund, amount, [evidence]), command ?? await Meta(f, request), default);
    }
    private static async Task<FinanceResult<SettlementCommandResult>> ConfirmCorrection(FieldAdvanceFixture f, Guid request, Guid id, Guid actor,
        SettlementCommand? command = null, FieldAdvancePaymentRaceTests.Barrier? barrier = null)
    {
        await using var db = f.CreateContext(); using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        var version = (await db.Set<RefundCorrection>().AsNoTracking().SingleAsync(x => x.Id == id)).Version;
        var service = new RefundCorrectionService(db, await Access(f, db, actor), await Executor(f, db, actor, barrier?.Audit(f, true, db)), f.Clock, files.Options);
        return await service.ConfirmAsync(actor, request, id, command ?? await Meta(f, request, version), default);
    }

    [Fact]
    public async Task Closed_correction_create_is_authorized_but_does_not_reopen_request()
    {
        await using var f = await Ready(postgres);
        await Grant(f, ReviewerId, ReviewerPersonId, "finance.check");
        var request = await FieldAdvanceSettlementDraftTests.Approved(f);
        await using var db = f.CreateContext();
        var row = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request);
        row.State = "Closed"; await db.SaveChangesAsync();
        var executor = await Executor(f, db, ReviewerId);
        var meta = await Meta(f, request);
        var result = await executor.ExecuteAsync(ReviewerId, request, "correction-create", meta,
            SettlementCommandExecutor.Hash(new { operation = "closed-correction-authorization" }),
            _ => Task.FromResult(new TPR10.Api.FieldAdvances.FinanceResult<SettlementCommandResult>(200, "authorized",
                new(request, null, "Closed", 0, 0, null))), default);
        Assert.Equal(200, result.Status);
        Assert.Equal("Closed", (await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == request)).State);
        Assert.Equal(meta.RequestVersion, result.Value!.RequestVersion);
        Assert.Equal(meta.FamilyVersion + 1, result.Value.FamilyVersion);
    }

    [Fact]
    public async Task Case_payment_and_case_closure_have_separate_persistent_tables()
    {
        await using var f = await Ready(postgres);
        await using var db = f.CreateContext();
        var tables = await db.Database.SqlQueryRaw<string>("""SELECT tablename AS "Value" FROM pg_tables WHERE schemaname='public'""").ToArrayAsync();
        Assert.Contains("field_advance_settlement_case_payments", tables);
        Assert.Contains("field_advance_settlement_case_closures", tables);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData(1500, 500)]
    [InlineData(2500, -500)]
    public async Task Accepted_correction_changes_net_once_and_preserves_signed_outstanding(decimal after, decimal outstanding)
    {
        await using var f = await Ready(postgres);
        var request = await FieldAdvanceSettlementDraftTests.Approved(f);
        await FieldAdvanceSettlementDraftTests.Document(f, request);
        await FieldAdvanceRefundTests.SeedPaid(f, request, 2000);
        await using var db = f.CreateContext();
        var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request);
        var evidence = (await db.Set<AdvanceEvidence>().SingleAsync()).Id;
        var refund = await Row<Refund>(f, db, r);
        refund.Amount = 2000; refund.Channel = "Transfer"; refund.RefundedOn = new(2026, 10, 20); refund.EvidenceId = evidence;
        refund.State = "Accepted"; refund.AcceptedAtUtc = refund.ReviewedAtUtc = f.Clock.GetUtcNow();
        refund.ReviewedBy = ReviewerId; refund.ReviewedByPersonId = ReviewerPersonId;
        db.Add(refund); await db.SaveChangesAsync();
        var correction = await Row<RefundCorrection>(f, db, r);
        correction.RefundId = refund.Id; correction.BaseVersion = refund.Version;
        correction.BeforeAmount = 2000; correction.AfterAmount = after; correction.Delta = after - 2000; correction.EvidenceId = evidence;
        correction.State = "Accepted"; correction.AcceptedAtUtc = correction.ReviewedAtUtc = f.Clock.GetUtcNow();
        correction.ReviewedBy = ReviewerId; correction.ReviewedByPersonId = ReviewerPersonId;
        db.Add(correction); await db.SaveChangesAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        Assert.True((await (await Access(f, db, AuthorId)).CheckAsync(AuthorId, request, SettlementActions.Read, null, default)).Allowed);
        var result = await SettlementBalanceReader.ReadLockedAsync(db, request, default);
        Assert.Equal(200, result.Status);
        Assert.Equal(after, result.Value!.ConfirmedRefund);
        Assert.Equal(outstanding, result.Value.OwnerOutstanding);
        Assert.Equal(2000m, (await db.Set<Refund>().AsNoTracking().SingleAsync()).Amount);
        var replay = await SettlementBalanceReader.ReadLockedAsync(db, request, default);
        Assert.Equal(result, replay);
    }
}
