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
public sealed class FieldAdvancePaymentExecutionTests(PostgresFixture postgres)
{
    private static readonly Guid Checker = Guid.Parse("71000000-0000-0000-0000-000000000045");
    private static readonly Guid CheckerPerson = Guid.Parse("72000000-0000-0000-0000-000000000045");
    [Fact]
    public void Execution_is_registered()
    {
        var services = new ServiceCollection(); services.AddFieldAdvances();
        Assert.Contains(services, x => x.ServiceType.Name == "PaymentExecutionService");
    }

    // A second start or repeated record must never create a second payment or consume twice.
    [Fact]
    public async Task StartResponseLost()
    {
        await using var f = await Ready(); var (request, id) = await Reviewed(f); using var files = new Files();
        var meta = await Meta(f, request, id);
        var started = await Call(f, request, id, "Start", files, meta: meta); Value(started);
        Assert.Equal(JsonSerializer.Serialize(started), JsonSerializer.Serialize(await Call(f, request, id, "Start", files, meta: meta)));
        Assert.Equal(409, (await Call(f, request, id, "Start", files)).Status);
        await using (var db = f.CreateContext())
        {
            var attempts = await db.Set<AdvancePaymentAttempt>().ToArrayAsync(); Assert.Single(attempts);
            Assert.Equal(RecipientId, attempts[0].PayerId); Assert.Equal(RecipientPersonId, attempts[0].PayerPersonId);
            Assert.Equal("Open", attempts[0].State); Assert.Equal(0m, (await db.Set<AdvancePaymentAccount>().SingleAsync()).Paid);
        }
        var input = await Actual(f, files, request); var record = await Meta(f, request, id);
        var paid = await Call(f, request, id, "Record", files, input, record); Assert.Equal("Paid", Value(paid).State);
        Assert.Equal(JsonSerializer.Serialize(paid), JsonSerializer.Serialize(await Call(f, request, id, "Record", files, input, record)));
        Assert.Equal(409, (await Call(f, request, id, "Record", files, input)).Status);
        await using var verify = f.CreateContext(); var account = await verify.Set<AdvancePaymentAccount>().SingleAsync();
        Assert.Equal(3000m, account.Paid); Assert.Equal(0m, account.Reserved); Assert.True(account.EverPaid);
        var storedResult = Assert.Single(await verify.Set<AdvancePaymentResult>().ToArrayAsync());
        Assert.Equal(3000m, storedResult.Amount); Assert.Equal(AuthorPersonId, storedResult.RecipientPersonId);
        Assert.Equal(input.PaidOn, storedResult.PaidOn); Assert.Equal(input.Reference, storedResult.Reference);
        Assert.Equal(input.EvidenceIds, JsonSerializer.Deserialize<Guid[]>(storedResult.EvidenceIdsJson));
        Assert.Equal(RecipientId, storedResult.RecordedBy); Assert.Equal(RecipientPersonId, storedResult.RecordedByPersonId);
        Assert.Equal((await verify.Set<AdvanceInstallmentRevision>().SingleAsync(x => x.IsCurrent)).Id,
            (await verify.Set<AdvancePaymentAttempt>().SingleAsync()).RevisionId);
        Assert.Single(await verify.Set<AdvanceReservationEntry>().Where(x => x.Action == "Consume").ToArrayAsync());
        Assert.Equal("Matched", (await verify.Set<AdvancePaymentAttempt>().SingleAsync()).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_payers_or_accounts_cannot_steal_start(bool samePerson)
    {
        await using var f = await Ready(); var (request, id) = await Reviewed(f); using var files = new Files();
        await OtherPayer(f, samePerson); var meta = await Meta(f, request, id);
        var results = await Task.WhenAll(Call(f, request, id, "Start", files, meta: meta),
            Call(f, request, id, "Start", files, meta: meta with { CommandId = Guid.NewGuid() }, actor: AliasAccountId));
        Assert.Single(results, x => x.Status == 200); Assert.Single(results, x => x.Status == 409);
        var winner = results[0].Status == 200 ? RecipientId : AliasAccountId;
        var loser = winner == RecipientId ? AliasAccountId : RecipientId;
        var input = await Actual(f, files, request);
        Assert.Equal(403, (await Call(f, request, id, "Record", files, input, actor: loser)).Status);
        Value(await Call(f, request, id, "Record", files, input, actor: winner));
        await using var db = f.CreateContext(); Assert.Single(await db.Set<AdvancePaymentAttempt>().ToArrayAsync());
        Assert.Equal(winner, (await db.Set<AdvancePaymentResult>().SingleAsync()).RecordedBy);
    }

    [Fact]
    public async Task Record_without_start_preserves_reservation()
    {
        await using var f = await Ready(); var (request, id) = await Reviewed(f); using var files = new Files();
        Assert.Equal(409, (await Call(f, request, id, "Record", files, await Actual(f, files, request))).Status);
        await Unpaid(f);
    }

    [Theory]
    [InlineData("amount")]
    [InlineData("recipient")]
    [InlineData("date")]
    [InlineData("infinite")]
    [InlineData("precision")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("foreign")]
    [InlineData("not-ready")]
    [InlineData("file")]
    public async Task Invalid_actual_keeps_attempt_and_reservation(string defect)
    {
        await using var f = await Ready(); var (request, id) = await Reviewed(f); using var files = new Files();
        Value(await Call(f, request, id, "Start", files)); var input = await Actual(f, files, request);
        input = defect switch
        {
            "amount" => input with { Amount = "2999" },
            "recipient" => input with { RecipientPersonId = RecipientPersonId },
            "date" => input with { PaidOn = default },
            "infinite" => input with { PaidOn = DateOnly.MaxValue },
            "precision" => input with { Amount = "3000.001" },
            "missing" => input with { EvidenceIds = [] },
            "duplicate" => input with { EvidenceIds = [input.EvidenceIds[0], input.EvidenceIds[0]] },
            _ => input
        };
        if (defect == "foreign") input = await Actual(f, files, await new FieldAdvancePaymentFixture(f).SeedApprovedAsync(10000, default));
        if (defect == "not-ready")
        { await using var db = f.CreateContext(); (await db.Set<AdvanceEvidence>().SingleAsync()).State = "Missing"; await db.SaveChangesAsync(); }
        if (defect == "file") files.Remove();
        Assert.NotEqual(200, (await Call(f, request, id, "Record", files, input)).Status);
        await using var verify = f.CreateContext(); Assert.Equal("Open", (await verify.Set<AdvancePaymentAttempt>().SingleAsync()).State);
        Assert.Equal("Paying", (await verify.Set<AdvanceInstallment>().SingleAsync()).State);
        await Unpaid(f, request);
    }

    [Fact]
    public async Task Successful_replay_does_not_read_missing_evidence_but_still_checks_current_auth()
    {
        await using var f = await Ready(); var (request, id) = await Reviewed(f); using var files = new Files();
        Value(await Call(f, request, id, "Start", files)); var input = await Actual(f, files, request);
        var meta = await Meta(f, request, id); var result = await Call(f, request, id, "Record", files, input, meta); Value(result);
        files.Remove();
        await using (var db = f.CreateContext())
        { (await db.Set<AdvanceEvidence>().SingleAsync()).State = "Missing"; await db.SaveChangesAsync(); }
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(await Call(f, request, id, "Record", files, input, meta, forbidIO: true)));
        Assert.Equal(409, (await Call(f, request, id, "Record", files, input with { Amount = "2999" }, meta, forbidIO: true)).Status);
        await Revoke(f);
        Assert.Equal(403, (await Call(f, request, id, "Record", files, input, meta, forbidIO: true)).Status);
    }

    [Fact]
    public async Task Revoked_payer_cannot_record_or_replay_start()
    {
        await using var f = await Ready(); var (request, id) = await Reviewed(f); using var files = new Files();
        var meta = await Meta(f, request, id); Value(await Call(f, request, id, "Start", files, meta: meta));
        var input = await Actual(f, files, request); await Revoke(f);
        Assert.Equal(403, (await Call(f, request, id, "Start", files, meta: meta)).Status);
        Assert.Equal(403, (await Call(f, request, id, "Record", files, input)).Status); await Unpaid(f);
    }

    [Fact]
    public async Task Elapsed_days_do_not_unlock_the_attempt()
    {
        await using var f = await Ready(); var (request, id) = await Reviewed(f); using var files = new Files();
        Value(await Call(f, request, id, "Start", files));
        await using (var db = f.CreateContext())
        { foreach (var p in await db.Set<GrantProposal>().ToArrayAsync()) p.EndsAtUtc = f.Clock.GetUtcNow().AddDays(30); await db.SaveChangesAsync(); }
        f.Clock.Advance(TimeSpan.FromDays(5));
        Assert.Equal(409, (await Call(f, request, id, "Start", files)).Status);
        Value(await Call(f, request, id, "Record", files, await Actual(f, files, request)));
        await using var verify = f.CreateContext(); Assert.Single(await verify.Set<AdvancePaymentAttempt>().ToArrayAsync());
    }

    [Theory]
    [InlineData("Start")]
    [InlineData("Record")]
    public async Task Commit_failure_rolls_back_every_financial_change(string operation)
    {
        await using var f = await Ready(); var (request, id) = await Reviewed(f); using var files = new Files();
        if (operation == "Record") Value(await Call(f, request, id, "Start", files));
        var input = await Actual(f, files, request); var before = await Snapshot(f); var meta = await Meta(f, request, id);
        await using (var db = f.CreateContext()) await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION execution_fail() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'execution test commit failure'; END; $$;
            CREATE CONSTRAINT TRIGGER execution_fail AFTER INSERT ON field_advance_payment_events DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION execution_fail();
            """);
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Call(f, request, id, operation, files, input, meta));
        Assert.Equal("P0001", error.SqlState); Assert.Equal(before, await Snapshot(f));
        await using (var db = f.CreateContext()) await db.Database.ExecuteSqlRawAsync("DROP TRIGGER execution_fail ON field_advance_payment_events");
        Value(await Call(f, request, id, operation, files, input, meta));
    }

    [Fact]
    public async Task New_hold_blocks_start_but_not_record_of_an_already_started_payment()
    {
        await using var f = await Ready(); var (request, id) = await Reviewed(f); using var files = new Files();
        var second = Value(await Prepare(f, request, null, "Create")).InstallmentId!.Value;
        Value(await Prepare(f, request, second, "Submit")); Value(await Prepare(f, request, second, "Approve"));
        Value(await Call(f, request, id, "Start", files));
        await Hold(f, request);
        Assert.Equal(409, (await Call(f, request, second, "Start", files)).Status);
        var result = Value(await Call(f, request, id, "Record", files, await Actual(f, files, request)));
        Assert.Equal(3000m, result.Paid); Assert.Equal(3000m, result.Reserved); Assert.Contains("PaymentMismatch", result.HoldCodes);
        await using var db = f.CreateContext(); Assert.Null((await db.Set<AdvancePaymentHold>().SingleAsync()).ReleasedAtUtc);
        Assert.Single(await db.Set<AdvancePaymentAttempt>().ToArrayAsync());
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("PendingReview")]
    [InlineData("Returned")]
    [InlineData("OutcomePending")]
    [InlineData("CancelPending")]
    [InlineData("Cancelled")]
    [InlineData("Paid")]
    public async Task Start_requires_reviewed_state(string state)
    {
        await using var f = await Ready(); var (request, id) = await Reviewed(f); using var files = new Files();
        await using (var db = f.CreateContext())
        { (await db.Set<AdvanceInstallment>().SingleAsync()).State = state; await db.SaveChangesAsync(); }
        Assert.Equal(409, (await Call(f, request, id, "Start", files)).Status);
        await using var verify = f.CreateContext(); Assert.Empty(await verify.Set<AdvancePaymentAttempt>().ToArrayAsync()); await Unpaid(f);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("approval")]
    [InlineData("hold")]
    [InlineData("revoked")]
    public async Task Start_rechecks_current_revision_approval_hold_and_authorization(string defect)
    {
        await using var f = await Ready(); var (request, id) = await Reviewed(f); using var files = new Files();
        if (defect == "hold") await Hold(f, request);
        if (defect == "revoked") await Revoke(f);
        await using (var db = f.CreateContext())
        {
            if (defect == "revision") (await db.Set<AdvanceInstallmentRevision>().SingleAsync()).IsCurrent = false;
            if (defect == "approval") (await db.Set<AdvanceRequest>().SingleAsync()).State = "Returned";
            await db.SaveChangesAsync();
        }
        Assert.Equal(defect == "revoked" ? 403 : 409, (await Call(f, request, id, "Start", files)).Status); await Unpaid(f);
    }

    [Theory]
    [InlineData("Start", "family")]
    [InlineData("Start", "request")]
    [InlineData("Start", "installment")]
    [InlineData("Record", "family")]
    [InlineData("Record", "request")]
    [InlineData("Record", "installment")]
    public async Task Stale_versions_do_not_write_a_receipt(string operation, string version)
    {
        await using var f = await Ready(); var (request, id) = await Reviewed(f); using var files = new Files();
        if (operation == "Record") Value(await Call(f, request, id, "Start", files));
        var input = await Actual(f, files, request); var meta = await Meta(f, request, id);
        meta = version switch
        {
            "family" => meta with { FamilyVersion = meta.FamilyVersion - 1 },
            "request" => meta with { RequestVersion = meta.RequestVersion - 1 },
            _ => meta with { InstallmentVersion = meta.InstallmentVersion - 1 }
        };
        Assert.Equal(409, (await Call(f, request, id, operation, files, input, meta)).Status);
        await using var db = f.CreateContext(); Assert.False(await db.Set<FinanceCommandReceipt>().AnyAsync(x => x.CommandId == meta.CommandId)); await Unpaid(f);
    }

    [Fact]
    public async Task Concurrent_record_commands_consume_once()
    {
        await using var f = await Ready(); var (request, id) = await Reviewed(f); using var files = new Files();
        Value(await Call(f, request, id, "Start", files)); var input = await Actual(f, files, request); var meta = await Meta(f, request, id);
        var results = await Task.WhenAll(Call(f, request, id, "Record", files, input, meta),
            Call(f, request, id, "Record", files, input, meta with { CommandId = Guid.NewGuid() }));
        Assert.Single(results, x => x.Status == 200); Assert.Single(results, x => x.Status == 409);
        await using var db = f.CreateContext(); Assert.Single(await db.Set<AdvancePaymentResult>().ToArrayAsync());
        Assert.Single(await db.Set<AdvanceReservationEntry>().Where(x => x.Action == "Consume").ToArrayAsync());
        Assert.Equal(3000m, (await db.Set<AdvancePaymentAccount>().SingleAsync()).Paid);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("digest")]
    [InlineData("length")]
    [InlineData("installment")]
    public async Task Evidence_changed_after_IO_is_rechecked_inside_the_financial_transaction(string defect)
    {
        await using var f = await Ready(); var (request, id) = await Reviewed(f); using var files = new Files();
        Guid? other = null;
        if (defect == "installment") other = Value(await Prepare(f, request, null, "Create")).InstallmentId;
        Value(await Call(f, request, id, "Start", files)); var input = await Actual(f, files, request);
        var changed = false;
        async Task Change()
        {
            await using var db = f.CreateContext(); var row = await db.Set<AdvanceEvidence>().SingleAsync();
            switch (defect)
            {
                case "state": row.State = "Missing"; break;
                case "digest": row.Sha256 = new('b', 64); break;
                case "length": row.Length++; break;
                case "installment": row.InstallmentId = other; break;
            }
            await db.SaveChangesAsync(); changed = true;
        }
        Assert.Equal(409, (await Call(f, request, id, "Record", files, input, beforeBody: Change)).Status);
        Assert.True(changed); await Unpaid(f);
    }

    [Fact]
    public async Task Entered_payment_date_is_preserved_separately_from_server_UTC_event_time()
    {
        await using var f = await Ready(); var (request, id) = await Reviewed(f); using var files = new Files();
        Value(await Call(f, request, id, "Start", files));
        var now = f.Clock.GetUtcNow();
        var nextEvening = new DateTimeOffset(now.UtcDateTime.Date.AddDays(1).AddHours(18), TimeSpan.Zero);
        f.Clock.Advance(nextEvening - now);
        var input = (await Actual(f, files, request)) with { PaidOn = DateOnly.FromDateTime(nextEvening.AddHours(7).DateTime) };
        Value(await Call(f, request, id, "Record", files, input));
        await using var db = f.CreateContext(); var stored = await db.Set<AdvancePaymentResult>().SingleAsync();
        Assert.Equal(input.PaidOn, stored.PaidOn); Assert.Equal(nextEvening, stored.CreatedAtUtc);
    }

    private static async Task Hold(FieldAdvanceFixture f, Guid request)
    {
        await using var db = f.CreateContext(); var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request);
        db.Add(new AdvancePaymentHold
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            FamilyId = r.FamilyId,
            Code = "PaymentMismatch",
            Reason = "test family incident",
            CreatedBy = RecipientId,
            CreatedByPersonId = RecipientPersonId,
            CreatedAtUtc = f.Clock.GetUtcNow()
        });
        await db.SaveChangesAsync();
    }

    private static async Task<string> Snapshot(FieldAdvanceFixture f)
    {
        await using var db = f.CreateContext();
        return JsonSerializer.Serialize(new
        {
            Families = await db.Set<AdvanceFamily>().OrderBy(x => x.Id).ToArrayAsync(),
            Requests = await db.Set<AdvanceRequest>().OrderBy(x => x.Id).ToArrayAsync(),
            Accounts = await db.Set<AdvancePaymentAccount>().OrderBy(x => x.Id).ToArrayAsync(),
            Installments = await db.Set<AdvanceInstallment>().OrderBy(x => x.Id).ToArrayAsync(),
            Attempts = await db.Set<AdvancePaymentAttempt>().OrderBy(x => x.Id).ToArrayAsync(),
            Results = await db.Set<AdvancePaymentResult>().OrderBy(x => x.Id).ToArrayAsync(),
            Entries = await db.Set<AdvanceReservationEntry>().OrderBy(x => x.Id).ToArrayAsync(),
            Participation = await db.Set<AdvancePaymentParticipation>().OrderBy(x => x.Id).ToArrayAsync(),
            Receipts = await db.Set<FinanceCommandReceipt>().OrderBy(x => x.CommandId).ToArrayAsync(),
            Events = await db.Set<AdvancePaymentEvent>().OrderBy(x => x.Id).ToArrayAsync()
        });
    }
    private static async Task Unpaid(FieldAdvanceFixture f, Guid? request = null)
    {
        await using var db = f.CreateContext(); var account = await db.Set<AdvancePaymentAccount>().SingleAsync(x => request == null || x.RequestId == request);
        Assert.Equal(0m, account.Paid); Assert.Equal(3000m, account.Reserved);
        Assert.Empty(await db.Set<AdvancePaymentResult>().ToArrayAsync());
        Assert.Empty(await db.Set<AdvanceReservationEntry>().Where(x => x.Action == "Consume").ToArrayAsync());
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
            id is null ? null : (await db.Set<AdvanceInstallment>().SingleAsync(x => x.Id == id)).Version, "test execution");
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
    private sealed class RejectPdf : IFinancePdfValidator { public Task<bool> ValidateAsync(byte[] content, CancellationToken ct) => Task.FromResult(false); }
    private sealed class ObservedStore(IFinanceEvidenceStore inner, Tpr10DbContext db, bool forbidIO) : IFinanceEvidenceStore
    {
        public Task<EvidenceTicket> WriteImmutableAsync(Guid storage, string key, Stream stream, CancellationToken ct) => inner.WriteImmutableAsync(storage, key, stream, ct);
        public Task<byte[]> ReadVerifiedAsync(Guid storage, string key, string hash, long length, CancellationToken ct)
        { Assert.False(forbidIO, "Receipt replay must not read files"); Assert.Null(db.Database.CurrentTransaction); return inner.ReadVerifiedAsync(storage, key, hash, length, ct); }
        public IEnumerable<EvidenceLocator> Enumerate() => inner.Enumerate();
    }
}
