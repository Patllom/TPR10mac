using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Settlements;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Payments;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvancePaymentRecoveryTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvancePaymentIncidentTests(PostgresFixture postgres)
{
    // A missing atomic source link must fail on real persisted 7C output, not service activation.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_mismatch_producers_persist_the_exact_source(bool outcome)
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready();
        using var files = new Files();
        var (request, installment, actual) = await Started(f, files);
        var observed = actual with { Amount = "8000" };
        var result = outcome
            ? await Recover(f, request, installment, "Outcome", "Propose", files, new PaymentOutcomeInput("Unknown", observed, observed.EvidenceIds))
            : await Recover(f, request, installment, "Hold", "ReportMismatch", files, observed);
        Assert.Equal(200, result.Status);
        await using var db = f.CreateContext();
        var proposal = Assert.Single(await db.Set<AdvancePaymentProposal>().Where(x => x.Kind == (outcome ? "PaymentOutcome" : "PaymentMismatch")).ToArrayAsync());
        var json = await db.Database.SqlQueryRaw<string>("SELECT row_to_json(h)::text AS \"Value\" FROM field_advance_payment_holds h").SingleAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("source_proposal_id", out var source) && source.ValueKind == System.Text.Json.JsonValueKind.String,
            "Mismatch hold must persist its source proposal in the same transaction");
        Assert.Equal(proposal.Id, source.GetGuid());
        Assert.Empty(await db.Set<AdvancePaymentResult>().ToArrayAsync());
        Assert.Null((await db.Set<AdvancePaymentHold>().SingleAsync()).ReleasedAtUtc);
    }

    [Fact]
    public async Task Before_and_after_paid_have_equal_actual_owner_liability()
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        var before = await Incident(f, files, false, 8000, 0);
        var after = await Incident(f, files, true, 8000, 0);
        var wrong = await Incident(f, files, true, 0, 10000);
        var beforePaid = await Totals(f, before.Request); var afterPaid = await Totals(f, after.Request);
        var whollyWrongRecipient = await Totals(f, wrong.Request);
        Assert.Equal(8000m, beforePaid.OwnerOutstanding);
        Assert.Equal(beforePaid.OwnerOutstanding, afterPaid.OwnerOutstanding);
        Assert.Equal(0m, whollyWrongRecipient.OwnerOutstanding);
        Assert.True(whollyWrongRecipient.HasOpenCase);
        Assert.Equal(10000m, whollyWrongRecipient.WrongRecipientOutstanding);
        await using var db = f.CreateContext();
        Assert.Equal(2, await db.Set<AdvancePaymentResult>().CountAsync());
        Assert.All(await db.Set<AdvancePaymentResult>().ToArrayAsync(), x => { Assert.Equal(10000m, x.Amount); Assert.Equal(AuthorPersonId, x.RecipientPersonId); });
        Assert.All(await db.Set<AdvancePaymentAccount>().ToArrayAsync(), x => Assert.Equal(10000m, x.Spendable));
        Assert.All(await db.Set<AdvancePaymentHold>().ToArrayAsync(), x => Assert.Null(x.ReleasedAtUtc));
    }

    [Fact]
    public async Task Proposal_is_not_liability_and_acceptance_is_once_immutable_and_replayable()
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        var s = await Source(f, files, true, 8000, 0);
        var input = new IncidentInput(s.Attempt, 8000, 0, s.Evidence);
        var cmd = await Command(f, s.Request);
        var proposal = await Invoke(f, files, s.Request, "Propose", input: input, command: cmd);
        Assert.Equal(201, proposal.Status); var id = proposal.Value!.EntityId!.Value;
        Assert.Equal(proposal, await Invoke(f, files, s.Request, "Propose", input: input, command: cmd));
        Assert.Equal(409, (await Invoke(f, files, s.Request, "Propose", input: input with { OwnerReceived = 7999 }, command: cmd)).Status);
        Assert.Equal(10000m, (await Totals(f, s.Request)).OwnerOutstanding);
        cmd = await Command(f, s.Request, id);
        var accepted = await Invoke(f, files, s.Request, "Confirm", id, command: cmd);
        Assert.Equal(200, accepted.Status);
        Assert.Equal(accepted, await Invoke(f, files, s.Request, "Confirm", id, command: cmd));
        Assert.Equal(409, (await Invoke(f, files, s.Request, "Confirm", id)).Status);
        Assert.Equal(409, (await Invoke(f, files, s.Request, "Propose", input: input)).Status);
        await using var db = f.CreateContext();
        Assert.Single(await db.Set<PaymentIncidentResolution>().ToArrayAsync());
        Assert.Single(await db.Set<AdvancePaymentEvent>().Where(x => x.Kind == "incident-accepted").ToArrayAsync());
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE field_advance_payment_incident_resolutions SET owner_received=1 WHERE id={id}"));
        Assert.Equal("23514", ex.SqlState);
    }

    [Theory]
    [InlineData("Owner", 12000, 0, 2000, 10000, 0, true)]
    [InlineData("WrongRecipient", 0, 10000, 4000, 0, 6000, true)]
    [InlineData("Owner", 12000, 0, 2000, 10000, 0, false)]
    [InlineData("WrongRecipient", 0, 10000, 4000, 0, 6000, false)]
    public async Task Recovery_is_separate_proposal_until_checker_confirmation_and_never_restores_capacity(
        string source, decimal owner, decimal wrong, decimal amount, decimal ownerNet, decimal wrongNet, bool paid)
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        var s = await Incident(f, files, paid, owner, wrong);
        Assert.Equal(owner, (await Totals(f, s.Request)).OwnerOutstanding);
        var evidence = (await Actual(f, files, s.Request, [4, 5, 6])).EvidenceIds;
        var cmd = await Command(f, s.Request);
        var proposal = await Invoke(f, files, s.Request, "Recovery", s.Id, amount: amount, source: source, evidence: evidence, command: cmd);
        Assert.Equal(201, proposal.Status); var recovery = proposal.Value!.EntityId!.Value;
        Assert.Equal(proposal, await Invoke(f, files, s.Request, "Recovery", s.Id, amount: amount, source: source, evidence: evidence, command: cmd));
        var pending = await Totals(f, s.Request); Assert.Equal(owner, pending.OwnerOutstanding); Assert.Equal(wrong, pending.WrongRecipientOutstanding);
        Assert.Equal(403, (await Invoke(f, files, s.Request, "ConfirmRecovery", recovery, actor: RecipientId)).Status);
        cmd = await Command(f, s.Request, recovery, true);
        var accepted = await Invoke(f, files, s.Request, "ConfirmRecovery", recovery, command: cmd);
        Assert.Equal(200, accepted.Status); Assert.Equal(accepted, await Invoke(f, files, s.Request, "ConfirmRecovery", recovery, command: cmd));
        var totals = await Totals(f, s.Request); Assert.Equal(ownerNet, totals.OwnerOutstanding); Assert.Equal(wrongNet, totals.WrongRecipientOutstanding); Assert.True(totals.HasOpenCase);
        Assert.Equal(source == "Owner" ? amount : 0, totals.ConfirmedRefund);
        Assert.Equal(409, (await Invoke(f, files, s.Request, "ConfirmRecovery", recovery)).Status);
        await using var db = f.CreateContext();
        Assert.Empty(await db.Set<Refund>().ToArrayAsync());
        var account = await db.Set<AdvancePaymentAccount>().SingleAsync(); Assert.Equal(paid ? 10000 : 0, account.Paid); Assert.Equal(10000, account.Spendable); Assert.Equal(paid ? 0 : 10000, account.Reserved);
        Assert.Single(await db.Set<AdvancePaymentEvent>().Where(x => x.Kind == "incident-recovery-accepted").ToArrayAsync());
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM field_advance_payment_incident_recoveries WHERE id={recovery}")); Assert.Equal("23514", ex.SqlState);
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("precision")]
    [InlineData("overflow")]
    [InlineData("empty-evidence")]
    [InlineData("duplicate-evidence")]
    [InlineData("missing-evidence")]
    [InlineData("not-ready")]
    [InlineData("wrong-recipient")]
    public async Task Invalid_actual_projection_never_creates_incident(string invalid)
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        var s = await Source(f, files, false, 8000, 0); var input = new IncidentInput(s.Attempt, 8000, 0, s.Evidence);
        input = invalid switch
        {
            "negative" => input with { OwnerReceived = -1 },
            "precision" => input with { OwnerReceived = 1.001m },
            "overflow" => input with { OwnerReceived = decimal.MaxValue },
            "empty-evidence" => input with { EvidenceIds = [] },
            "duplicate-evidence" => input with { EvidenceIds = [s.Evidence[0], s.Evidence[0]] },
            "missing-evidence" => input with { EvidenceIds = [Guid.NewGuid()] },
            "wrong-recipient" => input with { WrongRecipientReceived = 1 },
            _ => input
        };
        if (invalid == "not-ready") { await using var db = f.CreateContext(); (await db.Set<AdvanceEvidence>().SingleAsync()).State = "Missing"; await db.SaveChangesAsync(); }
        Assert.Equal(invalid == "not-ready" ? 409 : 422, (await Invoke(f, files, s.Request, "Propose", input: input)).Status);
        await using var verify = f.CreateContext(); Assert.Empty(await verify.Set<PaymentIncidentResolution>().ToArrayAsync());
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("ambiguous")]
    [InlineData("corrupt")]
    [InlineData("missing")]
    [InlineData("stale-revision")]
    public async Task Unprovable_source_fails_closed(string kind)
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        var s = await Source(f, files, false, 8000, 0);
        if (kind == "ambiguous") Assert.Equal(200, (await Recover(f, s.Request, s.Installment, "Hold", "ReportMismatch", files, s.Actual with { Amount = "9000" })).Status);
        await using (var db = f.CreateContext())
        {
            if (kind is "legacy" or "missing")
            {
                if (kind == "legacy") await db.Database.ExecuteSqlRawAsync("UPDATE field_advance_payment_holds SET source_proposal_id=NULL");
                else await db.Database.ExecuteSqlRawAsync("DELETE FROM field_advance_payment_holds");
            }
            if (kind == "corrupt") { var p = await db.Set<AdvancePaymentProposal>().SingleAsync(x => x.Kind == "PaymentMismatch"); p.Reason = "เปลี่ยนเนื้อหา"; await db.SaveChangesAsync(); }
            if (kind == "stale-revision") { (await db.Set<AdvanceInstallmentRevision>().SingleAsync()).IsCurrent = false; await db.SaveChangesAsync(); }
        }
        Assert.Equal(409, (await Invoke(f, files, s.Request, "Propose", input: new(s.Attempt, 8000, 0, s.Evidence))).Status);
        await using var verify = f.CreateContext(); Assert.Empty(await verify.Set<PaymentIncidentResolution>().ToArrayAsync());
    }

    [Fact]
    public async Task Confirmation_rechecks_source_and_all_evidence()
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        var s = await Source(f, files, false, 8000, 0); var extra = await Actual(f, files, s.Request, [7]);
        var p = await Invoke(f, files, s.Request, "Propose", input: new(s.Attempt, 8000, 0, [.. s.Evidence, .. extra.EvidenceIds])); Assert.Equal(201, p.Status);
        await using (var db = f.CreateContext()) { (await db.Set<AdvanceEvidence>().SingleAsync(x => x.Id == extra.EvidenceIds[0])).State = "Missing"; await db.SaveChangesAsync(); }
        Assert.Equal(409, (await Invoke(f, files, s.Request, "Confirm", p.Value!.EntityId)).Status);
        await using (var db = f.CreateContext()) { (await db.Set<AdvanceEvidence>().SingleAsync(x => x.Id == extra.EvidenceIds[0])).State = "Ready"; await db.SaveChangesAsync(); }
        Assert.Equal(200, (await Recover(f, s.Request, s.Installment, "Hold", "ReportMismatch", files, s.Actual with { Amount = "9000" })).Status);
        Assert.Equal(409, (await Invoke(f, files, s.Request, "Confirm", p.Value!.EntityId)).Status);
        Assert.Equal(0, (await Totals(f, s.Request)).OwnerOutstanding);
    }

    [Fact]
    public async Task Owner_alias_payer_self_review_foreign_request_and_revoked_replay_are_denied()
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        var s = await Source(f, files, false, 8000, 0); var input = new IncidentInput(s.Attempt, 8000, 0, s.Evidence);
        Assert.Equal(403, (await Invoke(f, files, s.Request, "Propose", input: input, actor: AuthorId)).Status);
        var p = await Invoke(f, files, s.Request, "Propose", input: input); Assert.Equal(201, p.Status);
        Assert.Equal(403, (await Invoke(f, files, s.Request, "Confirm", p.Value!.EntityId, actor: RecipientId)).Status);
        Assert.Equal(403, (await Invoke(f, files, Guid.NewGuid(), "Confirm", p.Value!.EntityId, command: new(Guid.NewGuid(), 1, 1, 1, "ตรวจขอบเขต"))).Status);
        var cmd = await Command(f, s.Request, p.Value.EntityId);
        Assert.Equal(200, (await Invoke(f, files, s.Request, "Confirm", p.Value.EntityId, command: cmd)).Status);
        await using (var db = f.CreateContext()) { foreach (var grant in await db.Set<GrantProposal>().Where(x => x.RecipientId == Checker).ToArrayAsync()) grant.EndsAtUtc = f.Clock.GetUtcNow(); await db.SaveChangesAsync(); }
        Assert.Equal(403, (await Invoke(f, files, s.Request, "Confirm", p.Value.EntityId, command: cmd)).Status);
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("WrongRecipient")]
    public async Task Recovery_proposals_reserve_only_the_related_actual_and_races_accept_once(string source)
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        var s = await Incident(f, files, false, 8000, 2000); var evidence = (await Actual(f, files, s.Request, [8])).EvidenceIds;
        var amount = source == "Owner" ? 8000 : 2000;
        var p = await Invoke(f, files, s.Request, "Recovery", s.Id, amount: amount, source: source, evidence: evidence); Assert.Equal(201, p.Status);
        var other = (await Actual(f, files, s.Request, [9])).EvidenceIds;
        Assert.Equal(409, (await Invoke(f, files, s.Request, "Recovery", s.Id, amount: 1, source: source, evidence: other)).Status);
        var cmd = await Command(f, s.Request, p.Value!.EntityId, true);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Invoke(f, files, s.Request, "ConfirmRecovery", p.Value.EntityId, command: cmd with { CommandId = Guid.NewGuid() })));
        Assert.Single(results, x => x.Status == 200); Assert.Single(results, x => x.Status == 409);
        Assert.Equal(409, (await Invoke(f, files, s.Request, "Recovery", s.Id, amount: 1, source: source, evidence: other)).Status);
    }

    [Theory]
    [InlineData(0, "Owner")]
    [InlineData(-1, "Owner")]
    [InlineData(1.001, "Owner")]
    [InlineData(1, "owner")]
    [InlineData(1, "Other")]
    public async Task Recovery_validates_amount_and_source(decimal amount, string source)
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        var s = await Incident(f, files, false, 8000, 0);
        Assert.Equal(422, (await Invoke(f, files, s.Request, "Recovery", s.Id, amount: amount, source: source, evidence: s.Evidence)).Status);
    }

    [Fact]
    public async Task Downgrade_cannot_erase_persisted_incident_provenance()
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        await Source(f, files, false, 8000, 0);
        await using var db = f.CreateContext();
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.GetService<IMigrator>().MigrateAsync("20261101163230_AddExpenseExcess"));
        Assert.Equal("23514", error.SqlState);
        Assert.NotNull((await db.Set<AdvancePaymentHold>().SingleAsync()).SourceProposalId);
    }

    [Fact]
    public async Task Empty_migration_roundtrip_is_repeatable_and_matches_model()
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); await using var db = f.CreateContext();
        var grants = await db.Set<GrantAssignment>().CountAsync();
        await db.GetService<IMigrator>().MigrateAsync("20261101163230_AddExpenseExcess");
        await db.Database.MigrateAsync(); await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges()); Assert.Equal(grants, await db.Set<GrantAssignment>().CountAsync());
        Assert.Empty(await db.Set<AdvancePaymentHold>().ToArrayAsync());
    }

    [Fact]
    public async Task Source_link_is_unique_and_rejects_cross_family_proposals()
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        var first = await Source(f, files, false, 8000, 0); var second = await Source(f, files, false, 8000, 0);
        await using var db = f.CreateContext();
        var hold = await db.Set<AdvancePaymentHold>().SingleAsync(x => x.SourceProposalId != null && db.Set<AdvancePaymentProposal>().Any(p => p.Id == x.SourceProposalId && p.RequestId == first.Request));
        var other = await db.Set<AdvancePaymentHold>().SingleAsync(x => x.Id != hold.Id);
        var unique = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO field_advance_payment_holds SELECT gen_random_uuid(), workspace_id, project_id, family_id, request_id, installment_id, code, reason, created_by, created_by_person_id, created_at_utc, released_by_person_id, released_at_utc, source_proposal_id FROM field_advance_payment_holds WHERE id={hold.Id}"));
        Assert.Equal("23505", unique.SqlState);
        // Free the second source from the unique claim, then prove composite scope rejects it.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE field_advance_payment_holds SET source_proposal_id=NULL WHERE id={other.Id}");
        var scope = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE field_advance_payment_holds SET source_proposal_id={other.SourceProposalId} WHERE id={hold.Id}"));
        Assert.Equal("23503", scope.SqlState);
    }

    [Fact]
    public async Task Service_resolves_from_application_provider()
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<PaymentIncidentService>());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Return_proof_cannot_be_counted_in_both_channels(bool recoveryFirst, bool copy)
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        await FieldAdvanceSettlementAccessTests.Grant(f, AuthorId, AuthorPersonId, "request.create");
        var s = await Incident(f, files, true, 8000, 0);
        var a = (await Actual(f, files, s.Request, [22])).EvidenceIds;
        var b = copy ? (await Actual(f, files, s.Request, [22])).EvidenceIds : a;
        var refund = new RefundInput(500, SettlementDeadlineCalculator.LocalDate(f.Clock.GetUtcNow()), "Cash", "บริษัททดสอบ", recoveryFirst ? b : a, null);
        if (recoveryFirst)
        {
            Assert.Equal(201, (await Invoke(f, files, s.Request, "Recovery", s.Id, amount: 500, evidence: a)).Status);
            Assert.Equal(409, (await FieldAdvanceRefundTests.RefundCall(f, s.Request, "Propose", input: refund)).Status);
        }
        else
        {
            Assert.Equal(201, (await FieldAdvanceRefundTests.RefundCall(f, s.Request, "Propose", input: refund)).Status);
            Assert.Equal(409, (await Invoke(f, files, s.Request, "Recovery", s.Id, amount: 500, evidence: b)).Status);
        }
        Assert.Equal(8000, (await Totals(f, s.Request)).OwnerOutstanding);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_returns_reserve_shared_owner_capacity_in_both_orders(bool recoveryFirst)
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        await FieldAdvanceSettlementAccessTests.Grant(f, AuthorId, AuthorPersonId, "request.create");
        var s = await Incident(f, files, true, 8000, 0);
        var a = (await Actual(f, files, s.Request, [23])).EvidenceIds;
        var b = (await Actual(f, files, s.Request, [24])).EvidenceIds;
        var refund = new RefundInput(recoveryFirst ? 5000 : 4000, SettlementDeadlineCalculator.LocalDate(f.Clock.GetUtcNow()), "Cash", "บริษัททดสอบ", b, null);
        if (recoveryFirst)
        {
            Assert.Equal(201, (await Invoke(f, files, s.Request, "Recovery", s.Id, amount: 4000, evidence: a)).Status);
            Assert.Equal(409, (await FieldAdvanceRefundTests.RefundCall(f, s.Request, "Propose", input: refund)).Status);
        }
        else
        {
            Assert.Equal(201, (await FieldAdvanceRefundTests.RefundCall(f, s.Request, "Propose", input: refund)).Status);
            Assert.Equal(409, (await Invoke(f, files, s.Request, "Recovery", s.Id, amount: 5000, evidence: a)).Status);
        }
        Assert.Equal(4000, (await Totals(f, s.Request)).PendingRefund);
        Assert.Equal(8000, (await Totals(f, s.Request)).OwnerOutstanding);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Proposer_expiry_during_confirmation_rolls_back_fact_event_and_receipt(bool recovery, bool afterSave)
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        var s = await Source(f, files, true, 8000, 0);
        var p = await Invoke(f, files, s.Request, "Propose", input: new(s.Attempt, 8000, 0, s.Evidence)); Assert.Equal(201, p.Status);
        var id = p.Value!.EntityId!.Value;
        if (recovery)
        {
            Assert.Equal(200, (await Invoke(f, files, s.Request, "Confirm", id)).Status);
            var e = (await Actual(f, files, s.Request, [25])).EvidenceIds;
            var r = await Invoke(f, files, s.Request, "Recovery", id, amount: 500, evidence: e); Assert.Equal(201, r.Status); id = r.Value!.EntityId!.Value;
        }
        await using (var db = f.CreateContext()) { foreach (var grant in await db.Set<GrantProposal>().Where(x => x.RecipientId == RecipientId).ToArrayAsync()) grant.EndsAtUtc = f.Clock.GetUtcNow().AddSeconds(1); await db.SaveChangesAsync(); }
        var command = await Command(f, s.Request, id, recovery);
        Assert.Equal(403, (await Invoke(f, files, s.Request, recovery ? "ConfirmRecovery" : "Confirm", id, command: command,
            beforeAudit: () => f.Clock.Advance(TimeSpan.FromSeconds(2)), afterSave: afterSave)).Status);
        await using var verify = f.CreateContext();
        Assert.False(await verify.Set<FinanceCommandReceipt>().AnyAsync(x => x.CommandId == command.CommandId));
        Assert.False(await verify.Set<AdvancePaymentEvent>().AnyAsync(x => x.CommandId == command.CommandId));
        if (recovery) Assert.Null((await verify.Set<PaymentIncidentRecovery>().SingleAsync()).AcceptedAtUtc);
        else Assert.Null((await verify.Set<PaymentIncidentResolution>().SingleAsync()).AcceptedAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_source_creations_roll_back_atomically_and_retry(bool outcome)
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        var (request, installment, actual) = await Started(f, files); var command = await Meta(f, request, installment);
        await using (var db = f.CreateContext()) await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION incident_fail() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'test hold commit failure'; END; $$;
            CREATE CONSTRAINT TRIGGER incident_fail AFTER INSERT ON field_advance_payment_holds DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION incident_fail();
            """);
        Task<FinanceResult<PaymentView>> Run() => outcome
            ? Recover(f, request, installment, "Outcome", "Propose", files, new PaymentOutcomeInput("Unknown", actual with { Amount = "8000" }, actual.EvidenceIds), command)
            : Recover(f, request, installment, "Hold", "ReportMismatch", files, actual with { Amount = "8000" }, command);
        Assert.Equal("P0001", (await Assert.ThrowsAsync<Npgsql.PostgresException>(Run)).SqlState);
        await using (var db = f.CreateContext())
        {
            Assert.Empty(await db.Set<AdvancePaymentHold>().ToArrayAsync());
            Assert.False(await db.Set<AdvancePaymentProposal>().AnyAsync(x => x.CommandId == command.CommandId));
            Assert.False(await db.Set<FinanceCommandReceipt>().AnyAsync(x => x.CommandId == command.CommandId));
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER incident_fail ON field_advance_payment_holds");
        }
        Assert.Equal(200, (await Run()).Status);
        await using var verify = f.CreateContext(); Assert.NotNull((await verify.Set<AdvancePaymentHold>().SingleAsync()).SourceProposalId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Person_identity_blocks_owner_and_payer_alias_review(bool owner)
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        var s = await Source(f, files, true, 8000, 0);
        var person = owner ? AuthorPersonId : RecipientPersonId;
        await using (var db = f.CreateContext()) { db.Add(new FinanceAccountLink { Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, UserId = AliasAccountId, PersonId = person, CreatedBy = AuthorId, CreatedAtUtc = f.Clock.GetUtcNow().AddMinutes(-1), Reason = "บัญชีทดสอบบุคคลเดิม" }); await db.SaveChangesAsync(); }
        await FieldAdvanceSettlementAccessTests.Grant(f, AliasAccountId, person, "finance.check");
        var p = await Invoke(f, files, s.Request, "Propose", input: new(s.Attempt, 8000, 0, s.Evidence)); Assert.Equal(201, p.Status);
        Assert.Equal(403, (await Invoke(f, files, s.Request, "Confirm", p.Value!.EntityId, actor: AliasAccountId)).Status);
    }

    private sealed class BeforeAudit(TPR10.Api.Auditing.IAuditEventWriter inner, Action before) : TPR10.Api.Auditing.IAuditEventWriter
    {
        public async Task WriteAsync(TPR10.Api.Auditing.SecurityAuditRequest request, CancellationToken ct) { before(); await inner.WriteAsync(request, ct); }
        public Task WriteAsync(string eventType, Guid targetId, IReadOnlyDictionary<string, string> metadata, CancellationToken ct, Guid? actorId = null)
            => inner.WriteAsync(eventType, targetId, metadata, ct, actorId);
    }

    [Fact]
    public async Task Accepted_actual_replacement_preserves_company_debt_from_prior_confirmed_refund()
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        await FieldAdvanceSettlementAccessTests.Grant(f, AuthorId, AuthorPersonId, "request.create");
        var s = await Source(f, files, true, 8000, 0);
        var evidence = (await Actual(f, files, s.Request, [44])).EvidenceIds;
        var refund = await FieldAdvanceRefundTests.RefundCall(f, s.Request, "Propose", input: new(9000,
            SettlementDeadlineCalculator.LocalDate(f.Clock.GetUtcNow()), "Cash", "บริษัททดสอบ", evidence, null));
        Assert.Equal(201, refund.Status);
        Assert.Equal(200, (await FieldAdvanceRefundTests.RefundCall(f, s.Request, "Confirm", refund.Value!.EntityId)).Status);
        Assert.Equal(200, (await FieldAdvanceRefundTests.RefundCall(f, s.Request, "Approve", refund.Value.EntityId, actor: Checker)).Status);
        var incident = await Invoke(f, files, s.Request, "Propose", input: new(s.Attempt, 8000, 0, s.Evidence)); Assert.Equal(201, incident.Status);
        Assert.Equal(200, (await Invoke(f, files, s.Request, "Confirm", incident.Value!.EntityId)).Status);
        var totals = await Totals(f, s.Request);
        Assert.Equal(8000, totals.OwnerReceived); Assert.Equal(9000, totals.ConfirmedRefund);
        Assert.Equal(-1000, totals.OwnerOutstanding); Assert.True(totals.HasOpenCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Outcome_source_supports_pending_and_confirmed_7C_proposals(bool confirmSource)
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        var (request, installment, actual) = await Started(f, files);
        Assert.Equal(200, (await Recover(f, request, installment, "Outcome", "Propose", files,
            new PaymentOutcomeInput("Unknown", actual with { Amount = "8000" }, actual.EvidenceIds))).Status);
        Guid attempt; Guid proposal;
        await using (var db = f.CreateContext()) { attempt = (await db.Set<AdvancePaymentAttempt>().SingleAsync()).Id; proposal = (await db.Set<AdvancePaymentProposal>().SingleAsync(x => x.Kind == "PaymentOutcome")).Id; }
        if (confirmSource) Assert.Equal(200, (await Recover(f, request, installment, "Outcome", "Confirm", files, proposal)).Status);
        var p = await Invoke(f, files, request, "Propose", input: new(attempt, 8000, 0, actual.EvidenceIds)); Assert.Equal(201, p.Status);
        Assert.Equal(200, (await Invoke(f, files, request, "Confirm", p.Value!.EntityId)).Status);
        Assert.Equal(8000, (await Totals(f, request)).OwnerOutstanding);
        await using var verify = f.CreateContext(); Assert.Empty(await verify.Set<AdvancePaymentResult>().ToArrayAsync());
    }

    [Fact]
    public async Task Return_proof_race_across_requests_accepts_only_one_channel()
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        await FieldAdvanceSettlementAccessTests.Grant(f, AuthorId, AuthorPersonId, "request.create");
        var a = await Incident(f, files, true, 8000, 0); var b = await Incident(f, files, true, 8000, 0);
        var proofA = (await Actual(f, files, a.Request, [45])).EvidenceIds; var proofB = (await Actual(f, files, b.Request, [45])).EvidenceIds;
        var results = await Task.WhenAll(Invoke(f, files, a.Request, "Recovery", a.Id, amount: 500, evidence: proofA),
            FieldAdvanceRefundTests.RefundCall(f, b.Request, "Propose", input: new(500, SettlementDeadlineCalculator.LocalDate(f.Clock.GetUtcNow()), "Cash", "บริษัททดสอบ", proofB, null)));
        Assert.Single(results, x => x.Status == 201); Assert.Single(results, x => x.Status == 409);
    }

    private sealed class AfterSave(Guid command, Action action) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        private bool invoked;
        public override ValueTask<int> SavedChangesAsync(Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesCompletedEventData data, int result, CancellationToken ct = default)
        {
            if (!invoked && data.Context is { } db && db.Set<AdvancePaymentEvent>().Local.Any(x => x.CommandId == command)) { invoked = true; action(); }
            return new(result);
        }
    }

    [Fact]
    public async Task Older_history_downgrade_rejection_keeps_Task11_schema_usable()
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready();
        var request = await new FieldAdvancePaymentFixture(f).SeedApprovedAsync(10000, default);
        await using var db = f.CreateContext(); var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request);
        var original = await FieldAdvanceSettlementCommandTests.Row<SettlementRound>(f, db, r); db.Add(original); await db.SaveChangesAsync();
        var adjustment = await FieldAdvanceSettlementCommandTests.Row<SettlementRound>(f, db, r); adjustment.RoundKind = "Adjustment"; adjustment.SourceRoundId = original.Id;
        db.Add(adjustment); await db.SaveChangesAsync();
        Assert.Equal("23514", (await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.GetService<IMigrator>().MigrateAsync("20261031221353_AllowMultipleDocumentDuplicateCases"))).SqlState);
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_name='field_advance_payment_holds' AND column_name='source_proposal_id'").SingleAsync());
        Assert.Empty(await db.Set<AdvancePaymentHold>().ToArrayAsync());
    }

    internal sealed record Scenario(Guid Request, Guid Installment, Guid Attempt, Guid[] Evidence, ActualPaymentInput Actual, Guid Id = default);
    internal static async Task<Scenario> Source(FieldAdvanceFixture f, Files files, bool paid, decimal owner, decimal wrong)
    {
        var (request, installment, actual) = await Started(f, files);
        if (paid) Assert.Equal(200, (await Call(f, request, installment, "Record", files, actual)).Status);
        var observed = actual with { Amount = (wrong > 0 ? wrong : owner).ToString(System.Globalization.CultureInfo.InvariantCulture), RecipientPersonId = wrong > 0 ? ReviewerPersonId : AuthorPersonId };
        Assert.Equal(200, (await Recover(f, request, installment, "Hold", "ReportMismatch", files, observed)).Status);
        await using var db = f.CreateContext(); var attempt = await db.Set<AdvancePaymentAttempt>().SingleAsync(x => x.RequestId == request);
        return new(request, installment, attempt.Id, actual.EvidenceIds, actual);
    }
    internal static async Task<Scenario> Incident(FieldAdvanceFixture f, Files files, bool paid, decimal owner, decimal wrong)
    {
        var s = await Source(f, files, paid, owner, wrong);
        var p = await Invoke(f, files, s.Request, "Propose", input: new(s.Attempt, owner, wrong, s.Evidence)); Assert.Equal(201, p.Status);
        var id = p.Value!.EntityId!.Value; Assert.Equal(200, (await Invoke(f, files, s.Request, "Confirm", id)).Status); return s with { Id = id };
    }
    internal static async Task<SettlementTotals> Totals(FieldAdvanceFixture f, Guid request)
    {
        await using var db = f.CreateContext(); await using var tx = await db.Database.BeginTransactionAsync();
        Assert.NotNull(await FamilyParticipation.LockRequestAsync(db, request, default));
        var result = await SettlementBalanceReader.ReadLockedAsync(db, request, default); Assert.Equal(200, result.Status); return result.Value!;
    }
    internal static async Task<SettlementCommand> Command(FieldAdvanceFixture f, Guid request, Guid? id = null, bool recovery = false)
    {
        await using var db = f.CreateContext(); long? version = id is null ? null : recovery
            ? (await db.Set<PaymentIncidentRecovery>().SingleAsync(x => x.Id == id)).Version
            : (await db.Set<PaymentIncidentResolution>().SingleAsync(x => x.Id == id)).Version;
        return await FieldAdvanceSettlementAccessTests.Meta(f, request, version);
    }
    internal static async Task<FinanceResult<SettlementCommandResult>> Invoke(FieldAdvanceFixture f, Files files, Guid request, string operation,
        Guid? id = null, IncidentInput? input = null, decimal amount = 0, string source = "Owner", Guid[]? evidence = null, Guid? actor = null, SettlementCommand? command = null, Action? beforeAudit = null, bool afterSave = false)
    {
        var user = actor ?? (operation is "Confirm" or "ConfirmRecovery" ? Checker : RecipientId);
        command ??= await Command(f, request, operation is "Confirm" or "ConfirmRecovery" ? id : null, operation == "ConfirmRecovery");
        await using var db = afterSave && beforeAudit is not null
            ? new TPR10.Api.Data.Tpr10DbContext(new DbContextOptionsBuilder<TPR10.Api.Data.Tpr10DbContext>().UseNpgsql(f.Database.ConnectionString)
                .AddInterceptors(new AfterSave(command.CommandId, beforeAudit)).Options) : f.CreateContext();
        var service = new PaymentIncidentService(db, await FieldAdvanceSettlementAccessTests.Access(f, db, user),
            await FieldAdvanceSettlementAccessTests.Executor(f, db, user, beforeAudit is null || afterSave ? null : new BeforeAudit(new TPR10.Api.Auditing.AuditEventWriter(db, new TPR10.Api.Correlation.CorrelationContext(), f.Clock), beforeAudit)), f.Clock, files.Options);
        return operation switch
        {
            "Propose" => await service.ProposeAsync(user, request, input!, command, default),
            "Confirm" => await service.ConfirmAsync(user, request, id!.Value, command, default),
            "Recovery" => await service.RecordRecoveryAsync(user, request, id!.Value, amount, source, evidence!, command, default),
            "ConfirmRecovery" => await service.ConfirmRecoveryAsync(user, request, id!.Value, command, default),
            _ => throw new InvalidOperationException()
        };
    }

    internal static async Task<(Guid Request, Guid Installment, ActualPaymentInput Actual)> Started(FieldAdvanceFixture f, Files files)
    {
        var request = await new FieldAdvancePaymentFixture(f).SeedApprovedAsync(10000m, default);
        var create = await Prepare(f, request, null, "Create", "10000"); Assert.Equal(200, create.Status);
        var installment = create.Value!.InstallmentId!.Value;
        Assert.Equal(200, (await Prepare(f, request, installment, "Submit")).Status);
        Assert.Equal(200, (await Prepare(f, request, installment, "Approve")).Status);
        Assert.Equal(200, (await Call(f, request, installment, "Start", files)).Status);
        return (request, installment, (await Actual(f, files, request)) with { Amount = "10000" });
    }
}
