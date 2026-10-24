using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Payments;
using TPR10.Api.Identity.Sessions;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvancePaymentAtomicityTests(PostgresFixture postgres)
{
    // Removing serialization/version validation lets both commands spend the same 10,000.
    [Fact]
    public async Task ConcurrentReservationsCannotExceedCeiling()
    {
        await using var f = await Ready();
        var p = new FieldAdvancePaymentFixture(f);
        var request = await p.SeedApprovedAsync(10000m, default);
        var first = (await p.SeedInstallmentAsync(request)).Installment.Id;
        var second = (await p.SeedInstallmentAsync(request)).Installment.Id;
        var session = await f.SessionAsync(RecipientId);
        await using var a = f.CreateContext(); await using var b = f.CreateContext();
        var one = await Meta(a, request, first); var two = await Meta(b, request, second);
        var results = await Task.WhenAll(Run(f, a, session, request, first, one, 6000m),
            Run(f, b, session, request, second, two, 6000m));
        Assert.Equal(1, results.Count(x => x.Status == 200));
        Assert.Contains(results, x => x.Status == 409);
        Assert.Empty(a.ChangeTracker.Entries()); Assert.Empty(b.ChangeTracker.Entries());
        await using var verify = f.CreateContext();
        var account = await verify.Set<AdvancePaymentAccount>().SingleAsync();
        Assert.Equal(6000m, account.Reserved); Assert.Equal(0m, account.Paid);
        Assert.Single(await verify.Set<AdvanceReservationEntry>().ToArrayAsync());
        Assert.Single(await verify.Set<AdvancePaymentEvent>().ToArrayAsync());
        Assert.Single(await verify.Set<FinanceCommandReceipt>().ToArrayAsync());
        Assert.Single(await verify.Set<AdvancePaymentParticipation>().ToArrayAsync());
        Assert.Single(await verify.AuditEvents.Where(x => x.EventType == "field-advance.payment.prepare").ToArrayAsync());
    }

    // A replay must not execute the body, bypass revocation, or ignore any input.
    [Fact]
    public async Task Replay_checks_payload_versions_actor_and_current_authority()
    {
        await using var f = await Ready(); var p = new FieldAdvancePaymentFixture(f);
        var request = await p.SeedApprovedAsync(10000m, default);
        var installment = (await p.SeedInstallmentAsync(request)).Installment.Id;
        var session = await f.SessionAsync(RecipientId); await using var db = f.CreateContext();
        var meta = await Meta(db, request, installment);
        var original = await Run(f, db, session, request, installment, meta, 2000m);
        Assert.Equal(200, original.Status);
        var replay = await Run(f, db, session, request, installment, meta, 2000m);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(replay));
        Assert.Equal(409, (await Run(f, db, session, request, installment, meta, 2001m)).Status);
        Assert.Equal(409, (await Run(f, db, session, request, installment, meta with { Reason = "เปลี่ยนเหตุผล" }, 2000m)).Status);
        Assert.Equal(409, (await Run(f, db, session, request, installment, meta with { FamilyVersion = 99 }, 2000m)).Status);
        Assert.Equal(409, (await Run(f, db, session, request, installment, meta, 2000m, evidence: Guid.NewGuid())).Status);
        await using (var link = f.CreateContext())
        {
            link.Add(new FinanceAccountLink
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                UserId = AliasAccountId,
                PersonId = RecipientPersonId,
                CreatedBy = AuthorId,
                CreatedAtUtc = f.Clock.GetUtcNow().AddMinutes(-1),
                Reason = "บัญชีจำลอง"
            });
            await link.SaveChangesAsync();
        }
        await Grant(f, AliasAccountId, RecipientPersonId);
        var other = await f.SessionAsync(AliasAccountId);
        Assert.Equal(409, (await Run(f, db, other, request, installment, meta, 2000m)).Status);
        await using (var revoke = f.CreateContext())
        {
            var grant = await revoke.Set<GrantProposal>().SingleAsync(x => x.RecipientId == RecipientId);
            grant.EndsAtUtc = f.Clock.GetUtcNow(); await revoke.SaveChangesAsync();
        }
        Assert.Equal(403, (await Run(f, db, session, request, installment, meta, 2000m)).Status);
        await using var verify = f.CreateContext();
        Assert.Equal(2000m, (await verify.Set<AdvancePaymentAccount>().SingleAsync()).Reserved);
        Assert.Single(await verify.Set<AdvanceReservationEntry>().ToArrayAsync());
        Assert.Single(await verify.Set<FinanceCommandReceipt>().ToArrayAsync());
    }

    // Fail after real writes, including a deferred PostgreSQL failure during COMMIT.
    [Theory]
    [InlineData("save")]
    [InlineData("commit")]
    [InlineData("audit")]
    [InlineData("expiry")]
    [InlineData("body-denied")]
    public async Task Failure_rolls_back_every_effect_and_clears_reusable_context(string failure)
    {
        await using var f = await Ready(); var p = new FieldAdvancePaymentFixture(f);
        var request = await p.SeedApprovedAsync(10000m, default);
        var installment = (await p.SeedInstallmentAsync(request)).Installment.Id;
        var session = await f.SessionAsync(RecipientId); await using var db = f.CreateContext();
        var meta = await Meta(db, request, installment); var before = await Snapshot(db);
        if (failure is "save" or "commit")
            await db.Database.ExecuteSqlRawAsync(failure == "commit" ? """
                CREATE FUNCTION fail_payment() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'test-payment-fault'; END $$;
                CREATE CONSTRAINT TRIGGER fail_payment AFTER INSERT ON field_advance_payment_events
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fail_payment();
                """ : """
                CREATE FUNCTION fail_payment() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'test-payment-fault'; END $$;
                CREATE TRIGGER fail_payment BEFORE INSERT ON field_advance_payment_events
                FOR EACH ROW EXECUTE FUNCTION fail_payment();
                """);
        var audit = new FieldAdvanceRequestRaceTests.CallbackAudit(new AuditEventWriter(db, new CorrelationContext(), f.Clock), () =>
        {
            if (failure == "audit") throw new InvalidOperationException("test-audit-fault");
            if (failure == "expiry") f.Clock.Advance(TimeSpan.FromHours(2));
            return Task.CompletedTask;
        });
        if (failure is "expiry" or "body-denied")
            Assert.Equal(403, (await Run(f, db, session, request, installment, meta, 2000m, audit: audit,
                deny: failure == "body-denied")).Status);
        else
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => Run(f, db, session, request, installment, meta, 2000m, audit: audit));
            while (error.InnerException is not null) error = error.InnerException;
            if (failure == "audit") Assert.Equal("test-audit-fault", error.Message);
            else Assert.Equal("P0001", Assert.IsType<Npgsql.PostgresException>(error).SqlState);
        }
        Assert.Empty(db.ChangeTracker.Entries()); Assert.Null(db.Database.CurrentTransaction);
        await db.SaveChangesAsync();
        await using (var verify = f.CreateContext()) Assert.Equal(before, await Snapshot(verify));
        if (failure is "save" or "commit") await db.Database.ExecuteSqlRawAsync("DROP FUNCTION fail_payment() CASCADE");
        if (failure == "expiry") session = await f.SessionAsync(RecipientId);
        Assert.Equal(200, (await Run(f, db, session, request, installment, meta, 2000m)).Status);
    }

    [Fact]
    public async Task Release_is_installment_scoped_and_consume_is_once_even_with_new_command()
    {
        await using var f = await Ready(); var p = new FieldAdvancePaymentFixture(f);
        var request = await p.SeedApprovedAsync(10000m, default);
        var installment = (await p.SeedInstallmentAsync(request)).Installment.Id;
        var other = (await p.SeedInstallmentAsync(request)).Installment.Id;
        var session = await f.SessionAsync(RecipientId); await using var db = f.CreateContext();
        Assert.Equal(200, (await Run(f, db, session, request, installment, await Meta(db, request, installment), 6000m)).Status);
        Assert.Equal(409, (await Run(f, db, session, request, other, await Meta(db, request, other), 2000m, "Release")).Status);
        Assert.Equal(200, (await Run(f, db, session, request, installment, await Meta(db, request, installment), 2000m, "Release")).Status);
        var consume = await Meta(db, request, installment);
        Assert.Equal(200, (await Run(f, db, session, request, installment, consume, 4000m, "Consume")).Status);
        Assert.Equal(200, (await Run(f, db, session, request, installment, consume, 4000m, "Consume")).Status);
        Assert.Equal(409, (await Run(f, db, session, request, installment, await Meta(db, request, installment), 4000m, "Consume")).Status);
        Assert.Equal(409, (await Run(f, db, session, request, installment, await Meta(db, request, installment), 1000m)).Status);
        await using var verify = f.CreateContext(); var account = await verify.Set<AdvancePaymentAccount>().SingleAsync();
        Assert.Equal(0m, account.Reserved); Assert.Equal(4000m, account.Paid); Assert.True(account.EverPaid);
        Assert.Equal(3, await verify.Set<AdvanceReservationEntry>().CountAsync());
        Assert.Equal(3, await verify.Set<FinanceCommandReceipt>().CountAsync());
    }

    // Latest versions must not permit overdraft even when an old account is already tracked.
    [Fact]
    public async Task Latest_versions_still_enforce_ceiling_with_preloaded_context()
    {
        await using var f = await Ready(); var p = new FieldAdvancePaymentFixture(f);
        var request = await p.SeedApprovedAsync(10000m, default);
        var first = (await p.SeedInstallmentAsync(request)).Installment.Id;
        var second = (await p.SeedInstallmentAsync(request)).Installment.Id;
        var session = await f.SessionAsync(RecipientId);
        await using var old = f.CreateContext(); await old.Set<AdvancePaymentAccount>().LoadAsync();
        await old.Set<AdvanceFamily>().LoadAsync(); await old.Set<AdvanceRequest>().LoadAsync();
        await using (var db = f.CreateContext())
            Assert.Equal(200, (await Run(f, db, session, request, first, await Meta(db, request, first), 6000m)).Status);
        Assert.Equal(409, (await Run(f, old, session, request, second, await Meta(old, request, second), 6000m)).Status);
        Assert.Equal(200, (await Run(f, old, session, request, second, await Meta(old, request, second), 4000m)).Status);
        await using var verify = f.CreateContext();
        Assert.Equal(10000m, (await verify.Set<AdvancePaymentAccount>().SingleAsync()).Reserved);
        Assert.Equal(2, await verify.Set<AdvanceReservationEntry>().CountAsync());
        Assert.Equal(3L, (await verify.Set<AdvanceFamily>().SingleAsync()).Version);
        Assert.Equal(3L, (await verify.Set<AdvanceRequest>().SingleAsync()).Version);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0.001")]
    [InlineData("10000000000000000")]
    [InlineData("79228162514264337593543950335")]
    public async Task Invalid_money_rejects_without_rounding_or_partial_participation(string value)
    {
        await using var f = await Ready(); var p = new FieldAdvancePaymentFixture(f);
        var request = await p.SeedApprovedAsync(10000m, default);
        var installment = (await p.SeedInstallmentAsync(request)).Installment.Id;
        var session = await f.SessionAsync(RecipientId); await using var db = f.CreateContext();
        var before = await Snapshot(db);
        Assert.Equal(400, (await Run(f, db, session, request, installment, await Meta(db, request, installment),
            decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture))).Status);
        await using var verify = f.CreateContext(); Assert.Equal(before, await Snapshot(verify));
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ledger_rejects_calls_outside_executor_even_inside_transaction(bool transaction)
    {
        await using var f = await Ready(); await using var db = f.CreateContext();
        await using var tx = transaction ? await db.Database.BeginTransactionAsync() : null;
        var ledger = new ReservationLedger(db, f.Clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), 1m, Guid.NewGuid(), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.ReleaseAsync(Guid.NewGuid(), Guid.NewGuid(), 1m, Guid.NewGuid(), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.ConsumeAsync(Guid.NewGuid(), Guid.NewGuid(), 1m, Guid.NewGuid(), default));
        Assert.Empty(await db.Set<AdvanceReservationEntry>().ToArrayAsync());
    }

    [Theory]
    [InlineData("request")]
    [InlineData("installment")]
    [InlineData("command")]
    public async Task Ledger_cannot_escape_authorized_command_scope(string mismatch)
    {
        await using var f = await Ready(); var p = new FieldAdvancePaymentFixture(f);
        var request = await p.SeedApprovedAsync(10000m, default);
        var installment = (await p.SeedInstallmentAsync(request)).Installment.Id;
        var session = await f.SessionAsync(RecipientId); await using var db = f.CreateContext();
        var meta = await Meta(db, request, installment); var before = await Snapshot(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Executor(f, db, session).ExecuteAsync(RecipientId, request,
            installment, "prepare", meta, new('a', 64), async ct =>
            {
                await new ReservationLedger(db, f.Clock).ReserveAsync(mismatch == "request" ? Guid.NewGuid() : request,
                    mismatch == "installment" ? Guid.NewGuid() : installment, 1m, mismatch == "command" ? Guid.NewGuid() : meta.CommandId, ct);
                return new(200, "unexpected", null);
            }, default));
        await using var verify = f.CreateContext(); Assert.Equal(before, await Snapshot(verify));
    }

    [Theory]
    [InlineData("request")]
    [InlineData("installment")]
    public async Task Stale_versions_do_not_execute_body(string stale)
    {
        await using var f = await Ready(); var p = new FieldAdvancePaymentFixture(f);
        var request = await p.SeedApprovedAsync(10000m, default);
        var installment = (await p.SeedInstallmentAsync(request)).Installment.Id;
        var session = await f.SessionAsync(RecipientId); await using var db = f.CreateContext();
        var meta = await Meta(db, request, installment); var before = await Snapshot(db);
        meta = stale == "request" ? meta with { RequestVersion = 99 } : meta with { InstallmentVersion = 99 };
        Assert.Equal(409, (await Run(f, db, session, request, installment, meta, 2000m)).Status);
        await using var verify = f.CreateContext(); Assert.Equal(before, await Snapshot(verify));
    }

    // A swallowed ledger error must not commit mutations made earlier by the same body.
    [Fact]
    public async Task Swallowed_ledger_failure_cannot_commit_partial_command()
    {
        await using var f = await Ready(); var p = new FieldAdvancePaymentFixture(f);
        var request = await p.SeedApprovedAsync(10000m, default);
        var installment = (await p.SeedInstallmentAsync(request)).Installment.Id;
        var session = await f.SessionAsync(RecipientId); await using var db = f.CreateContext();
        var meta = await Meta(db, request, installment); var before = await Snapshot(db);
        var result = await Executor(f, db, session).ExecuteAsync(RecipientId, request, installment, "prepare", meta,
            new('a', 64), async ct =>
            {
                var ledger = new ReservationLedger(db, f.Clock);
                var balance = await ledger.ReserveAsync(request, installment, 2000m, meta.CommandId, ct);
                try { await ledger.ReserveAsync(request, installment, 2001m, meta.CommandId, ct); }
                catch (Exception) { /* Simulate a caller that incorrectly swallows the conflict. */ }
                return new(200, "payment-test", new(request, installment, 0, 0, null, "Draft",
                    balance.Approved, balance.Spendable, balance.Reserved, balance.Paid, 8000m, []));
            }, default);
        Assert.Equal(409, result.Status);
        await using var verify = f.CreateContext(); Assert.Equal(before, await Snapshot(verify));
    }

    [Fact]
    public async Task Receipt_reflects_body_changes_and_never_copies_sensitive_payload_to_audit()
    {
        await using var f = await Ready(); var p = new FieldAdvancePaymentFixture(f);
        var request = await p.SeedApprovedAsync(10000m, default);
        var installment = (await p.SeedInstallmentAsync(request)).Installment.Id;
        var session = await f.SessionAsync(RecipientId); await using var db = f.CreateContext();
        var meta = await Meta(db, request, installment);
        var hash = PaymentCommandExecutor.Hash(new PaymentDestination("Transfer", AuthorPersonId, "test-bank", "secret-account-sentinel", "secret-holder-sentinel"));
        var result = await Executor(f, db, session).ExecuteAsync(RecipientId, request, installment, "prepare", meta, hash, async ct =>
        {
            var row = await db.Set<AdvanceInstallment>().SingleAsync(x => x.Id == installment, ct);
            row.State = "PendingReview";
            db.Add(new AdvancePaymentHold
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                ProjectId = ProjectId,
                FamilyId = row.FamilyId,
                RequestId = request,
                Code = "TestHold",
                Reason = "เหตุจำลอง",
                CreatedBy = RecipientId,
                CreatedByPersonId = RecipientPersonId,
                CreatedAtUtc = f.Clock.GetUtcNow()
            });
            return new(200, "payment-test", new(request, installment, 0, 0, null, "Draft", 0, 0, 0, 0, 0, []));
        }, default);
        Assert.Equal(200, result.Status); Assert.Equal("PendingReview", result.Value!.State);
        Assert.Equal(new[] { "TestHold" }, result.Value.HoldCodes);
        Assert.Equal(2L, result.Value.FamilyVersion); Assert.Equal(2L, result.Value.RequestVersion);
        Assert.Equal(2L, result.Value.InstallmentVersion);
        await using var verify = f.CreateContext();
        var receipt = await verify.Set<FinanceCommandReceipt>().SingleAsync();
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(JsonSerializer.Deserialize<FinanceResult<PaymentView>>(receipt.ResultJson)));
        var retained = JsonSerializer.Serialize(new
        {
            receipt,
            Events = await verify.Set<AdvancePaymentEvent>().ToArrayAsync(),
            Audit = await verify.AuditEvents.ToArrayAsync(),
            Metadata = await verify.AuditMetadata.ToArrayAsync()
        });
        Assert.DoesNotContain("secret-account-sentinel", retained); Assert.DoesNotContain("secret-holder-sentinel", retained);
    }

    private async Task<FieldAdvanceFixture> Ready()
    {
        var f = await CreateAsync(postgres); await f.SeedPoliciesAsync();
        await Grant(f, RecipientId, RecipientPersonId); return f;
    }

    private static async Task Grant(FieldAdvanceFixture f, Guid actor, Guid person)
    {
        var id = await FieldAdvanceGrantTests.AuthorityAsync(f, actor, person);
        await using var db = f.CreateContext();
        var assignment = await db.Set<GrantAssignment>().SingleAsync(x => x.Id == id);
        (await db.Set<GrantProposal>().SingleAsync(x => x.Id == assignment.ProposalId)).Capabilities = ["payment.record"];
        await db.SaveChangesAsync();
    }

    private static async Task<PaymentCommand> Meta(Tpr10DbContext db, Guid request, Guid installment)
    {
        var r = await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == request);
        var family = await db.Set<AdvanceFamily>().AsNoTracking().SingleAsync(x => x.Id == r.FamilyId);
        var i = await db.Set<AdvanceInstallment>().AsNoTracking().SingleAsync(x => x.Id == installment);
        return new(Guid.NewGuid(), family.Version, r.Version, i.Version, "ทดสอบธุรกรรม");
    }

    private static PaymentCommandExecutor Executor(FieldAdvanceFixture f, Tpr10DbContext db, RequestSession session, IAuditEventWriter? audit = null)
    {
        var access = FieldAdvanceAccessTests.Access(db, session, f.Clock); var people = new FinancePersonResolver(db, f.Clock);
        return new(db, new(db, access, people, new(db, access, people, f.Clock), f.Clock),
            new(db, session, access, people, f.Clock), audit ?? new AuditEventWriter(db, new CorrelationContext(), f.Clock), f.Clock);
    }

    private static Task<FinanceResult<PaymentView>> Run(FieldAdvanceFixture f, Tpr10DbContext db, RequestSession session,
        Guid request, Guid installment, PaymentCommand meta, decimal amount, string operation = "Reserve", Guid? evidence = null,
        IAuditEventWriter? audit = null, bool deny = false)
        => Executor(f, db, session, audit).ExecuteAsync(session.Entity!.UserId, request, installment, "prepare", meta,
            PaymentCommandExecutor.Hash(new { amount, operation, evidence }), async ct =>
            {
                var ledger = new ReservationLedger(db, f.Clock);
                var balance = operation switch
                {
                    "Release" => await ledger.ReleaseAsync(request, installment, amount, meta.CommandId, ct),
                    "Consume" => await ledger.ConsumeAsync(request, installment, amount, meta.CommandId, ct),
                    _ => await ledger.ReserveAsync(request, installment, amount, meta.CommandId, ct)
                };
                return new(deny ? 403 : 200, "payment-test", new(request, installment, 0, 0, null, "Draft",
                    balance.Approved, balance.Spendable, balance.Reserved, balance.Paid, BalanceMath.Available(balance), []));
            }, default);

    private static async Task<string> Snapshot(Tpr10DbContext db) => JsonSerializer.Serialize(new
    {
        Account = await db.Set<AdvancePaymentAccount>().AsNoTracking().ToArrayAsync(),
        Family = await db.Set<AdvanceFamily>().AsNoTracking().ToArrayAsync(),
        Request = await db.Set<AdvanceRequest>().AsNoTracking().ToArrayAsync(),
        Installment = await db.Set<AdvanceInstallment>().AsNoTracking().ToArrayAsync(),
        Ledger = await db.Set<AdvanceReservationEntry>().CountAsync(),
        Events = await db.Set<AdvancePaymentEvent>().CountAsync(),
        Receipts = await db.Set<FinanceCommandReceipt>().CountAsync(),
        Participation = await db.Set<AdvancePaymentParticipation>().CountAsync(),
        Audit = await db.AuditEvents.CountAsync(),
        Metadata = await db.AuditMetadata.CountAsync()
    });
}
