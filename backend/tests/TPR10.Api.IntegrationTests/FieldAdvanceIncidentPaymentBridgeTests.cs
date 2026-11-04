using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Payments;
using TPR10.Api.FieldAdvances.Settlements;
using TPR10.Api.FieldAdvances.Data;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvancePaymentRecoveryTests;
using static TPR10.Api.IntegrationTests.FieldAdvancePaymentIncidentTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceIncidentPaymentBridgeTests(PostgresFixture postgres)
{
    // Behavioral RED against the real migrated constraint, with accepted facts from real services.
    [Fact]
    public async Task Resolved_specific_hold_can_persist_explicit_release()
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready();
        using var files = new Files();
        var s = await Incident(f, files, true, 8000, 0);
        await using var db = f.CreateContext();
        var hold = await db.Set<AdvancePaymentHold>().SingleAsync();
        hold.ReleasedAtUtc = f.Clock.GetUtcNow();
        hold.ReleasedByPersonId = CheckerPerson;
        var error = await Record.ExceptionAsync(() => db.SaveChangesAsync());
        Assert.Null(error);
        db.ChangeTracker.Clear();
        Assert.NotNull((await db.Set<AdvancePaymentHold>().SingleAsync()).ReleasedAtUtc);
        Assert.Equal(10000m, (await db.Set<AdvancePaymentAccount>().SingleAsync()).Paid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_does_not_restore_capacity_or_reuse_old_review(bool paid)
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        var s = await Incident(f, files, paid, 8000, 2000);
        var beforeRecoveryAvailable = (await Summary(f, s.Request)).Available;
        Assert.Equal(409, (await Bridge(f, files, s, "Release")).Status);
        await Recovery(f, files, s, 2000);
        var afterRecoveryAvailable = (await Summary(f, s.Request)).Available;
        Assert.Equal(beforeRecoveryAvailable, afterRecoveryAvailable);
        Guid other;
        await using (var db = f.CreateContext())
        {
            var source = await db.Set<AdvancePaymentHold>().SingleAsync();
            other = Guid.NewGuid();
            db.Add(new AdvancePaymentHold
            {
                Id = other,
                WorkspaceId = source.WorkspaceId,
                ProjectId = source.ProjectId,
                FamilyId = source.FamilyId,
                Code = "OtherHold",
                Reason = "เหตุอื่น",
                CreatedBy = source.CreatedBy,
                CreatedByPersonId = source.CreatedByPersonId,
                CreatedAtUtc = source.CreatedAtUtc
            });
            await db.SaveChangesAsync();
        }
        var releaseCommand = await Command(f, s.Request, s.Id);
        var released = await Bridge(f, files, s, "Release", command: releaseCommand); Assert.Equal(200, released.Status);
        Assert.Equal(released, await Bridge(f, files, s, "Release", command: releaseCommand));
        var afterRelease = await Summary(f, s.Request);
        Assert.Contains("OtherHold", afterRelease.HoldCodes);
        Assert.DoesNotContain("PaymentMismatch", afterRelease.HoldCodes);
        Assert.Equal(beforeRecoveryAvailable, afterRelease.Available);
        Assert.Equal(409, (await Bridge(f, files, s, "Prepare", 2000)).Status);
        await using (var db = f.CreateContext())
        {
            var hold = await db.Set<AdvancePaymentHold>().SingleAsync(x => x.Id == other);
            hold.ReleasedAtUtc = f.Clock.GetUtcNow(); hold.ReleasedByPersonId = CheckerPerson; await db.SaveChangesAsync();
        }
        var ordinary = await Prepare(f, s.Request, null, "Create", "1"); Assert.Equal(200, ordinary.Status);
        Assert.Equal(409, (await Prepare(f, s.Request, ordinary.Value!.InstallmentId, "Submit")).Status);
        var command = await FieldAdvanceSettlementAccessTests.Meta(f, s.Request);
        var prepared = await Bridge(f, files, s, "Prepare", 2000, command); Assert.Equal(201, prepared.Status);
        Assert.Equal(prepared, await Bridge(f, files, s, "Prepare", 2000, command));
        Assert.Equal(409, (await Bridge(f, files, s, "Prepare", 1999, command)).Status);
        var id = prepared.Value!.EntityId!.Value;
        await using (var db = f.CreateContext())
        {
            var shortfall = await db.Set<AdvanceInstallment>().SingleAsync(x => x.Id == id);
            Assert.Equal("PendingReview", shortfall.State);
            Assert.Null((await db.Set<AdvanceInstallmentRevision>().SingleAsync(x => x.InstallmentId == id)).ReviewedAtUtc);
            var account = await db.Set<AdvancePaymentAccount>().SingleAsync();
            Assert.Equal(paid ? 10000m : 0m, account.Paid); Assert.Equal(2000m, account.Reserved);
            Assert.Single(await db.Set<IncidentPaymentAdjustment>().ToArrayAsync());
            Assert.Equal(paid ? 0m : 10000m, (await db.Set<IncidentPaymentAdjustment>().SingleAsync()).Amount);
        }
        var startBeforeNewReview = await Call(f, s.Request, id, "Start", files);
        Assert.Equal(409, startBeforeNewReview.Status);
        Assert.Equal(409, (await Bridge(f, files, s, "Prepare", 1)).Status);
        Assert.Equal(200, (await Prepare(f, s.Request, id, "Approve")).Status);
        Assert.Equal(200, (await Call(f, s.Request, id, "Start", files)).Status);
        Assert.Equal(200, (await Call(f, s.Request, id, "Record", files, s.Actual with { Amount = "2000" })).Status);
        Assert.Equal(10000m, (await Totals(f, s.Request)).OwnerReceived);
        Assert.Equal(0m, (await Summary(f, s.Request)).Available);
        await using var verify = f.CreateContext();
        Assert.Equal(paid ? 12000m : 2000m, (await verify.Set<AdvancePaymentAccount>().SingleAsync()).Paid);
        Assert.Equal("Consumed", (await verify.Set<PaymentShortfallClaim>().SingleAsync()).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_claims_do_not_reuse_incident_entitlement(bool sameCommand)
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        var s = await Incident(f, files, true, 8000, 0);
        Assert.Equal(200, (await Bridge(f, files, s, "Release")).Status);
        var command = await FieldAdvanceSettlementAccessTests.Meta(f, s.Request);
        var results = await Task.WhenAll(Bridge(f, files, s, "Prepare", 2000, command),
            Bridge(f, files, s, "Prepare", 2000, sameCommand ? command : command with { CommandId = Guid.NewGuid() }));
        Assert.Equal(sameCommand ? 2 : 1, results.Count(x => x.Status == 201));
        if (sameCommand) Assert.Equal(results[0], results[1]); else Assert.Single(results, x => x.Status == 409);
        await using var db = f.CreateContext(); Assert.Single(await db.Set<PaymentShortfallClaim>().ToArrayAsync());
        Assert.Equal(2000m, (await db.Set<AdvancePaymentAccount>().SingleAsync()).Reserved);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0.001)]
    [InlineData(2001)]
    public async Task Invalid_claim_amount_leaves_no_reservation(decimal amount)
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        var s = await Incident(f, files, true, 8000, 0); Assert.Equal(200, (await Bridge(f, files, s, "Release")).Status);
        Assert.Equal(amount == 2001 ? 409 : 422, (await Bridge(f, files, s, "Prepare", amount)).Status);
        await using var db = f.CreateContext(); Assert.Empty(await db.Set<PaymentShortfallClaim>().ToArrayAsync());
        Assert.Equal(0m, (await db.Set<AdvancePaymentAccount>().SingleAsync()).Reserved);
    }

    [Fact]
    public async Task Bridge_denies_owner_payer_review_foreign_scope_and_revoked_replay()
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        var s = await Incident(f, files, true, 8000, 0);
        Assert.Equal(403, (await Bridge(f, files, s, "Release", actor: RecipientId)).Status);
        Assert.Equal(403, (await Bridge(f, files, s, "Release", actor: AuthorId)).Status);
        Assert.Equal(200, (await Bridge(f, files, s, "Release")).Status);
        Assert.Equal(403, (await Bridge(f, files, s, "Prepare", 2000, actor: AuthorId)).Status);
        var command = await FieldAdvanceSettlementAccessTests.Meta(f, s.Request);
        Assert.Equal(201, (await Bridge(f, files, s, "Prepare", 2000, command)).Status);
        await using (var db = f.CreateContext())
        {
            foreach (var grant in await db.Set<GrantProposal>().Where(x => x.RecipientId == RecipientId).ToArrayAsync()) grant.EndsAtUtc = f.Clock.GetUtcNow();
            await db.SaveChangesAsync();
        }
        Assert.Equal(403, (await Bridge(f, files, s, "Prepare", 2000, command)).Status);
    }

    [Fact]
    public async Task Populated_bridge_prevents_destructive_downgrade()
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        var s = await Incident(f, files, false, 8000, 0); Assert.Equal(200, (await Bridge(f, files, s, "Release")).Status);
        Assert.Equal(201, (await Bridge(f, files, s, "Prepare", 2000)).Status);
        await using var db = f.CreateContext();
        Assert.Equal("23514", (await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.GetService<IMigrator>().MigrateAsync("20261101221319_AddPaymentIncidentProvenance"))).SqlState);
        Assert.Single(await db.Set<PaymentShortfallClaim>().ToArrayAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }


    [Fact]
    public async Task Return_update_and_cancel_adjust_only_the_linked_unconsumed_claim()
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        var s = await Incident(f, files, true, 8000, 0); Assert.Equal(200, (await Bridge(f, files, s, "Release")).Status);
        var p = await Bridge(f, files, s, "Prepare", 1500); Assert.Equal(201, p.Status); var id = p.Value!.EntityId!.Value;
        Assert.Equal(200, (await Edit(f, s.Request, id, "Return")).Status);
        Assert.Equal(1500m, (await Summary(f, s.Request)).Reserved);
        Assert.Equal(200, (await Edit(f, s.Request, id, "Update", "2000")).Status);
        Assert.Equal(409, (await Edit(f, s.Request, id, "Update", "2000.01")).Status);
        Assert.Equal(200, (await Prepare(f, s.Request, id, "Submit")).Status);
        Assert.Equal(200, (await Prepare(f, s.Request, id, "Approve")).Status);
        Assert.Equal(200, (await Edit(f, s.Request, id, "Update", "1000")).Status);
        Assert.Equal(409, (await Call(f, s.Request, id, "Start", files)).Status);
        var second = await Bridge(f, files, s, "Prepare", 1000); Assert.Equal(201, second.Status);
        Assert.Equal(200, (await Cancel(f, files, s.Request, id)).Status);
        Guid proposal;
        await using (var db = f.CreateContext()) proposal = (await db.Set<AdvancePaymentProposal>().SingleAsync(x => x.InstallmentId == id && x.Kind == "PaymentCancellation")).Id;
        Assert.Equal(200, (await Cancel(f, files, s.Request, id, proposal)).Status);
        var view = await Summary(f, s.Request); Assert.Equal(1000m, view.Reserved); Assert.Equal(0m, view.Available);
        Assert.Equal(201, (await Bridge(f, files, s, "Prepare", 1000)).Status);
        Assert.Equal(409, (await Bridge(f, files, s, "Prepare", 1)).Status);
        await using var verify = f.CreateContext();
        Assert.Equal("Cancelled", (await verify.Set<PaymentShortfallClaim>().SingleAsync(x => x.InstallmentId == id)).State);
        Assert.Equal(2000m, (await verify.Set<AdvancePaymentAccount>().SingleAsync()).ActivatedShortfall);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Chained_incident_spends_parent_once_and_uses_only_new_attempt_entitlement(bool secondPaid)
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        var first = await Incident(f, files, true, 8000, 0); Assert.Equal(200, (await Bridge(f, files, first, "Release")).Status);
        var p = await Bridge(f, files, first, "Prepare", 2000); Assert.Equal(201, p.Status); var id = p.Value!.EntityId!.Value;
        Assert.Equal(200, (await Prepare(f, first.Request, id, "Approve")).Status);
        Assert.Equal(200, (await Call(f, first.Request, id, "Start", files)).Status);
        var actual = (await Actual(f, files, first.Request, [99])) with { Amount = "2000" };
        if (secondPaid) Assert.Equal(200, (await Call(f, first.Request, id, "Record", files, actual)).Status);
        Assert.Equal(200, (await Recover(f, first.Request, id, "Hold", "ReportMismatch", files, actual with { Amount = "1000" })).Status);
        Guid attempt;
        await using (var db = f.CreateContext()) attempt = (await db.Set<AdvancePaymentAttempt>().SingleAsync(x => x.InstallmentId == id)).Id;
        var proposed = await Invoke(f, files, first.Request, "Propose", input: new(attempt, 1000, 0, actual.EvidenceIds)); Assert.Equal(201, proposed.Status);
        Assert.Equal(200, (await Invoke(f, files, first.Request, "Confirm", proposed.Value!.EntityId)).Status);
        var second = new Scenario(first.Request, id, attempt, actual.EvidenceIds, actual, proposed.Value.EntityId!.Value);
        Assert.Equal(200, (await Bridge(f, files, second, "Release")).Status);
        Assert.Equal(409, (await Bridge(f, files, first, "Prepare", 1)).Status);
        var next = await Bridge(f, files, second, "Prepare", 1000); Assert.Equal(201, next.Status);
        Assert.Equal(409, (await Bridge(f, files, second, "Prepare", 1)).Status);
        var nextId = next.Value!.EntityId!.Value;
        Assert.Equal(200, (await Prepare(f, first.Request, nextId, "Approve")).Status);
        Assert.Equal(200, (await Call(f, first.Request, nextId, "Start", files)).Status);
        Assert.Equal(200, (await Call(f, first.Request, nextId, "Record", files, actual with { Amount = "1000" })).Status);
        Assert.Equal(10000m, (await Totals(f, first.Request)).OwnerReceived);
        Assert.Equal(0m, (await Summary(f, first.Request)).Available);
        await using var verify = f.CreateContext();
        Assert.Equal(secondPaid ? "Consumed" : "Settled", (await verify.Set<PaymentShortfallClaim>().SingleAsync(x => x.InstallmentId == id)).State);
        Assert.Equal(2, await verify.Set<IncidentPaymentAdjustment>().CountAsync());
        Assert.Equal(secondPaid ? 13000m : 11000m, (await verify.Set<AdvancePaymentAccount>().SingleAsync()).Paid);
    }

    [Fact]
    public async Task Ordinary_refund_does_not_create_shortfall_or_reduce_effective_usage()
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        await FieldAdvanceSettlementAccessTests.Grant(f, AuthorId, AuthorPersonId, "request.create");
        var s = await Incident(f, files, true, 8000, 0); Assert.Equal(200, (await Bridge(f, files, s, "Release")).Status);
        var proof = (await Actual(f, files, s.Request, [34])).EvidenceIds;
        var refund = await FieldAdvanceRefundTests.RefundCall(f, s.Request, "Propose", input: new(1000,
            SettlementDeadlineCalculator.LocalDate(f.Clock.GetUtcNow()), "Cash", "บริษัท", proof, null)); Assert.Equal(201, refund.Status);
        Assert.Equal(200, (await FieldAdvanceRefundTests.RefundCall(f, s.Request, "Confirm", refund.Value!.EntityId)).Status);
        Assert.Equal(200, (await FieldAdvanceRefundTests.RefundCall(f, s.Request, "Approve", refund.Value.EntityId, actor: Checker)).Status);
        Assert.Equal(0m, (await Summary(f, s.Request)).Available);
        Assert.Equal(409, (await Bridge(f, files, s, "Prepare", 3000)).Status);
        Assert.Equal(201, (await Bridge(f, files, s, "Prepare", 2000)).Status);
        await using var db = f.CreateContext(); Assert.Equal(8000m, (await db.Set<AdvancePaymentAccount>().SingleAsync()).EffectiveUsage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_excess_and_pending_recovery_prevent_release(bool paid)
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        var s = await Incident(f, files, paid, 12000, 0);
        Assert.Equal(409, (await Bridge(f, files, s, "Release")).Status);
        var evidence = (await Actual(f, files, s.Request, [49])).EvidenceIds;
        var recovery = await Invoke(f, files, s.Request, "Recovery", s.Id, amount: 2000, source: "Owner", evidence: evidence); Assert.Equal(201, recovery.Status);
        Assert.Equal(409, (await Bridge(f, files, s, "Release")).Status);
        Assert.Equal(200, (await Invoke(f, files, s.Request, "ConfirmRecovery", recovery.Value!.EntityId)).Status);
        Assert.Equal(200, (await Bridge(f, files, s, "Release")).Status);
        Assert.Equal(409, (await Bridge(f, files, s, "Prepare", 1)).Status);
        Assert.Equal(0m, (await Summary(f, s.Request)).Available);
    }

    [Fact]
    public async Task Failed_commit_rolls_back_claim_reservation_event_receipt_and_retries_once()
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        var s = await Incident(f, files, true, 8000, 0); Assert.Equal(200, (await Bridge(f, files, s, "Release")).Status);
        var command = await FieldAdvanceSettlementAccessTests.Meta(f, s.Request);
        await using (var db = f.CreateContext()) await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION bridge_fail() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'test bridge commit failure'; END; $$;
            CREATE CONSTRAINT TRIGGER bridge_fail AFTER INSERT ON field_advance_payment_shortfall_claims DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION bridge_fail();
            """);
        Assert.Equal("P0001", (await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Bridge(f, files, s, "Prepare", 2000, command))).SqlState);
        await using (var db = f.CreateContext())
        {
            Assert.Empty(await db.Set<PaymentShortfallClaim>().ToArrayAsync());
            Assert.False(await db.Set<FinanceCommandReceipt>().AnyAsync(x => x.CommandId == command.CommandId));
            Assert.False(await db.Set<AdvancePaymentEvent>().AnyAsync(x => x.CommandId == command.CommandId));
            Assert.Equal(0m, (await db.Set<AdvancePaymentAccount>().SingleAsync()).Reserved);
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER bridge_fail ON field_advance_payment_shortfall_claims");
        }
        Assert.Equal(201, (await Bridge(f, files, s, "Prepare", 2000, command)).Status);
    }

    [Fact]
    public async Task Projection_cannot_be_forged_or_history_erased_and_empty_migration_roundtrips()
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        await using (var db = f.CreateContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261101221319_AddPaymentIncidentProvenance");
            await db.Database.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
        }
        var s = await Incident(f, files, true, 8000, 0); Assert.Equal(200, (await Bridge(f, files, s, "Release")).Status);
        Assert.Equal(201, (await Bridge(f, files, s, "Prepare", 2000)).Status);
        await using var verify = f.CreateContext();
        Assert.Equal("23514", (await Assert.ThrowsAsync<Npgsql.PostgresException>(() => verify.Database.ExecuteSqlRawAsync("UPDATE field_advance_payment_accounts SET activated_shortfall=3000"))).SqlState);
        Assert.Equal("23514", (await Assert.ThrowsAsync<Npgsql.PostgresException>(() => verify.Database.ExecuteSqlRawAsync("DELETE FROM field_advance_incident_payment_adjustments"))).SqlState);
        Assert.Equal("23514", (await Assert.ThrowsAsync<Npgsql.PostgresException>(() => verify.Database.ExecuteSqlRawAsync("DELETE FROM field_advance_payment_shortfall_claims"))).SqlState);
        Assert.Equal("23514", (await Assert.ThrowsAsync<Npgsql.PostgresException>(() => verify.Database.ExecuteSqlRawAsync("UPDATE field_advance_payment_shortfall_claims SET amount=2000.001"))).SqlState);
    }


    [Fact]
    public async Task Multiple_incidents_share_account_capacity_but_not_each_others_entitlement()
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        var request = await new FieldAdvancePaymentFixture(f).SeedApprovedAsync(10000m, default);
        var sources = new List<(Guid Id, ActualPaymentInput Actual)>();
        for (var n = 0; n < 2; n++)
        {
            var p = await Prepare(f, request, null, "Create", "5000"); Assert.Equal(200, p.Status); var id = p.Value!.InstallmentId!.Value;
            Assert.Equal(200, (await Prepare(f, request, id, "Submit")).Status);
            Assert.Equal(200, (await Prepare(f, request, id, "Approve")).Status);
            Assert.Equal(200, (await Call(f, request, id, "Start", files)).Status);
            var actual = (await Actual(f, files, request, [(byte)(80 + n)])) with { Amount = "5000" };
            Assert.Equal(200, (await Call(f, request, id, "Record", files, actual)).Status);
            sources.Add((id, actual));
        }
        var incidents = new List<Scenario>();
        foreach (var source in sources)
        {
            Assert.Equal(200, (await Recover(f, request, source.Id, "Hold", "ReportMismatch", files, source.Actual with { Amount = "4000" })).Status);
            await using var db = f.CreateContext(); var attempt = (await db.Set<AdvancePaymentAttempt>().SingleAsync(x => x.InstallmentId == source.Id)).Id;
            var p = await Invoke(f, files, request, "Propose", input: new(attempt, 4000, 0, source.Actual.EvidenceIds)); Assert.Equal(201, p.Status);
            Assert.Equal(200, (await Invoke(f, files, request, "Confirm", p.Value!.EntityId)).Status);
            incidents.Add(new(request, source.Id, attempt, source.Actual.EvidenceIds, source.Actual, p.Value.EntityId!.Value));
        }
        foreach (var incident in incidents) Assert.Equal(200, (await Bridge(f, files, incident, "Release")).Status);
        Assert.Equal(409, (await Bridge(f, files, incidents[0], "Prepare", 2000)).Status);
        foreach (var incident in incidents) Assert.Equal(201, (await Bridge(f, files, incident, "Prepare", 1000)).Status);
        Assert.Equal(409, (await Bridge(f, files, incidents[1], "Prepare", 1)).Status);
        await using var verify = f.CreateContext();
        var account = await verify.Set<AdvancePaymentAccount>().SingleAsync();
        Assert.Equal(10000m, account.Paid); Assert.Equal(8000m, account.EffectiveUsage); Assert.Equal(2000m, account.Reserved);
        Assert.Equal(2, await verify.Set<PaymentShortfallClaim>().CountAsync());
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("evidence")]
    [InlineData("revision")]
    public async Task Release_revalidates_provenance_and_evidence_and_preserves_source_reservation(string corruption)
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        var s = await Incident(f, files, false, 8000, 0);
        await using (var db = f.CreateContext())
        {
            if (corruption == "legacy") (await db.Set<AdvancePaymentHold>().SingleAsync()).SourceProposalId = null;
            if (corruption == "evidence") (await db.Set<AdvanceEvidence>().SingleAsync()).State = "Missing";
            if (corruption == "revision") (await db.Set<AdvanceInstallmentRevision>().SingleAsync()).IsCurrent = false;
            await db.SaveChangesAsync();
        }
        Assert.Equal(409, (await Bridge(f, files, s, "Release")).Status);
        await using var verify = f.CreateContext();
        Assert.Null((await verify.Set<AdvancePaymentHold>().SingleAsync()).ReleasedAtUtc);
        Assert.Empty(await verify.Set<IncidentPaymentAdjustment>().ToArrayAsync());
        Assert.Equal(10000m, (await verify.Set<AdvancePaymentAccount>().SingleAsync()).Reserved);
    }

    [Fact]
    public async Task Foreign_incident_and_missing_new_review_do_not_mutate_either_request()
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        var a = await Incident(f, files, true, 8000, 0); var b = await Incident(f, files, true, 8000, 0);
        Assert.Equal(403, (await Bridge(f, files, a with { Id = b.Id }, "Release", command: await Command(f, a.Request, b.Id))).Status);
        Assert.Equal(403, (await Bridge(f, files, a with { Id = b.Id }, "Prepare", 2000)).Status);
        Assert.Equal(409, (await Bridge(f, files, a, "Prepare", 2000)).Status);
        await using var db = f.CreateContext(); Assert.Empty(await db.Set<PaymentShortfallClaim>().ToArrayAsync());
        Assert.All(await db.Set<AdvancePaymentHold>().ToArrayAsync(), h => Assert.Null(h.ReleasedAtUtc));
    }

    [Fact]
    public async Task Older_guard_rejection_preserves_current_bridge_schema()
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready();
        var request = await new FieldAdvancePaymentFixture(f).SeedApprovedAsync(10000m, default);
        await using var db = f.CreateContext(); var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request);
        var original = await FieldAdvanceSettlementCommandTests.Row<SettlementRound>(f, db, r); db.Add(original); await db.SaveChangesAsync();
        var adjustment = await FieldAdvanceSettlementCommandTests.Row<SettlementRound>(f, db, r); adjustment.RoundKind = "Adjustment"; adjustment.SourceRoundId = original.Id;
        db.Add(adjustment); await db.SaveChangesAsync();
        Assert.Equal("23514", (await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.GetService<IMigrator>().MigrateAsync("20261031221353_AllowMultipleDocumentDuplicateCases"))).SqlState);
        Assert.Empty(await db.Set<PaymentShortfallClaim>().ToArrayAsync());
        Assert.Equal(0m, (await db.Set<AdvancePaymentAccount>().SingleAsync()).EffectiveUsage);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData("incident_payment_adjustments")]
    [InlineData("payment_shortfall_claims")]
    public async Task Incident_entitlement_history_cannot_be_truncated(string table)
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready(); using var files = new Files();
        var s = await Incident(f, files, true, 8000, 0); Assert.Equal(200, (await Bridge(f, files, s, "Release")).Status);
        Assert.Equal(201, (await Bridge(f, files, s, "Prepare", 2000)).Status);
        await using var db = f.CreateContext();
        // The identifier comes only from this fixed test allowlist.
        Assert.Contains(table, new[] { "incident_payment_adjustments", "payment_shortfall_claims" });
        var sql = table == "incident_payment_adjustments" ? "TRUNCATE field_advance_incident_payment_adjustments CASCADE" : "TRUNCATE field_advance_payment_shortfall_claims CASCADE";
        Assert.Equal("23514", (await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql))).SqlState);
        Assert.Single(await db.Set<PaymentShortfallClaim>().ToArrayAsync());
        Assert.Single(await db.Set<IncidentPaymentAdjustment>().ToArrayAsync());
    }

    private static async Task<FinanceResult<PaymentView>> Cancel(FieldAdvanceFixture f, Files files, Guid request, Guid id, Guid? proposal = null)
    {
        var actor = proposal is null ? RecipientId : Checker; var session = await f.SessionAsync(actor);
        await using var db = f.CreateContext(); var finance = FieldAdvanceAccessTests.Access(db, session, f.Clock);
        var people = new TPR10.Api.FieldAdvances.Access.FinancePersonResolver(db, f.Clock);
        var access = new PaymentAccess(db, finance, people, new(db, finance, people, f.Clock), f.Clock);
        var executor = new PaymentCommandExecutor(db, access, new(db, session, finance, people, f.Clock),
            new TPR10.Api.Auditing.AuditEventWriter(db, new TPR10.Api.Correlation.CorrelationContext(), f.Clock), f.Clock);
        var service = new PaymentCancellationService(db, executor, new(db, f.Clock), people, null!, files.Options, f.Clock);
        var command = await Meta(f, request, id);
        return proposal is null ? await service.ProposeAsync(actor, id, command, default) : await service.ConfirmAsync(actor, proposal.Value, command, default);
    }

    private static async Task<FinanceResult<PaymentView>> Edit(FieldAdvanceFixture f, Guid request, Guid id, string operation, string amount = "1000")
    {
        var actor = operation == "Return" ? Checker : RecipientId; var session = await f.SessionAsync(actor);
        await using var db = f.CreateContext(); var finance = FieldAdvanceAccessTests.Access(db, session, f.Clock);
        var people = new TPR10.Api.FieldAdvances.Access.FinancePersonResolver(db, f.Clock);
        var access = new PaymentAccess(db, finance, people, new(db, finance, people, f.Clock), f.Clock);
        var executor = new PaymentCommandExecutor(db, access, new(db, session, finance, people, f.Clock),
            new TPR10.Api.Auditing.AuditEventWriter(db, new TPR10.Api.Correlation.CorrelationContext(), f.Clock), f.Clock);
        var command = await Meta(f, request, id);
        return operation == "Return" ? await new InstallmentReviewService(db, executor, people, f.Clock).ReviewAsync(actor, id, "Return", command, default)
            : await new InstallmentService(db, executor, new(db, f.Clock), f.Clock).UpdateAsync(actor, id, new(amount, new("Cash", AuthorPersonId, null, null, null)), command, default);
    }

    internal static async Task Recovery(FieldAdvanceFixture f, Files files, Scenario s, decimal amount, string kind = "WrongRecipient")
    {
        var evidence = (await Actual(f, files, s.Request, Guid.NewGuid().ToByteArray())).EvidenceIds;
        var p = await Invoke(f, files, s.Request, "Recovery", s.Id, amount: amount, source: kind, evidence: evidence); Assert.Equal(201, p.Status);
        Assert.Equal(200, (await Invoke(f, files, s.Request, "ConfirmRecovery", p.Value!.EntityId)).Status);
    }
    internal static async Task<PaymentView> Summary(FieldAdvanceFixture f, Guid request)
    {
        await using var db = f.CreateContext(); var people = new TPR10.Api.FieldAdvances.Access.FinancePersonResolver(db, f.Clock);
        var finance = FieldAdvanceAccessTests.Access(db, await f.SessionAsync(RecipientId), f.Clock);
        var access = new PaymentAccess(db, finance, people, new(db, finance, people, f.Clock), f.Clock);
        // Summary is internal; public tests use the real authorized Get endpoint through its service.
        var service = new PaymentReadService(db, access, finance, null!);
        var result = await service.GetAsync(RecipientId, request, default); Assert.Equal(200, result.Status); return result.Value!;
    }
    internal static async Task<FinanceResult<SettlementCommandResult>> Bridge(FieldAdvanceFixture f, Files files, Scenario s,
        string operation, decimal amount = 0, SettlementCommand? command = null, Guid? actor = null)
    {
        var user = actor ?? (operation == "Release" ? Checker : RecipientId);
        command ??= operation == "Release" ? await Command(f, s.Request, s.Id) : await FieldAdvanceSettlementAccessTests.Meta(f, s.Request);
        await using var db = f.CreateContext();
        var service = new IncidentPaymentBridge(db, await FieldAdvanceSettlementAccessTests.Access(f, db, user),
            await FieldAdvanceSettlementAccessTests.Executor(f, db, user), f.Clock, files.Options);
        return operation == "Release" ? await service.ReleaseHoldAsync(user, s.Request, s.Id, command, default)
            : await service.PrepareShortfallAsync(user, s.Request, s.Id, amount, command, default);
    }
}
