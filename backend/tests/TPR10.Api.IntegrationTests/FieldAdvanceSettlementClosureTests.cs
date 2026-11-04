using TPR10.Api.FieldAdvances.Payments;
using L = TPR10.Api.IntegrationTests.FieldAdvanceRequestLifecycleTests;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using TPR10.Api.FieldAdvances.Settlements;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Data;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementAccessTests;
using C = TPR10.Api.IntegrationTests.FieldAdvanceSettlementCommandTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceSettlementClosureTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Wrapped_transient_batch_failure_recovers_in_same_worker_without_duplicate_history()
    {
        await using var f = await Ready(postgres);
        var request = await Eligible(f);
        var failure = new FailFirstBatch();
        await using var db = f.CreateContext();
        await RunWorker(f, async () => await db.Set<ClosureRecord>().AnyAsync(x => x.RequestId == request), failure);
        Assert.Equal(1, failure.Failures);
        Assert.Single(await db.Set<ClosureRecord>().Where(x => x.RequestId == request).ToArrayAsync());
        Assert.Single(await db.AuditEvents.Where(x => x.EventType == "field-advance.settlement.close").ToArrayAsync());
    }

    private sealed class FailFirstBatch : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public int Failures { get; private set; }
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Failures == 0 && command.CommandText.Contains("LIMIT"))
            {
                Failures++;
                throw new InvalidOperationException("An exception has been raised that is likely due to a transient failure.",
                    new NpgsqlException("simulated connection outage", new System.IO.IOException("connection lost")));
            }
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task Registered_worker_closes_reconciled_request_without_a_human_command()
    {
        await using var f = await Ready(postgres);
        var request = await Eligible(f);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(f.Clock);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FieldAdvances:SettlementClosure:IntervalSeconds"] = "0.05",
            ["FieldAdvances:SettlementClosure:BatchSize"] = "1"
        }).Build());
        services.AddDbContext<Tpr10DbContext>(o => o.UseNpgsql(f.Database.ConnectionString));
        services.AddScoped<IAuditEventWriter>(sp => new AuditEventWriter(sp.GetRequiredService<Tpr10DbContext>(), new CorrelationContext(), f.Clock));
        services.AddFieldAdvances();
        await using var provider = services.BuildServiceProvider();
        var workers = provider.GetServices<IHostedService>().ToArray();
        foreach (var worker in workers) await worker.StartAsync(default);
        string state = "";
        try
        {
            var until = DateTime.UtcNow.AddSeconds(3);
            do
            {
                await using var db = f.CreateContext();
                state = await db.Set<AdvanceRequest>().Where(x => x.Id == request).Select(x => x.State).SingleAsync();
                if (state == "Closed") break;
                await Task.Delay(25);
            } while (DateTime.UtcNow < until);
        }
        finally { foreach (var worker in workers) await worker.StopAsync(default); }
        Assert.Equal("Closed", state);
        await using var check = f.CreateContext();
        Assert.Single(await check.Set<ClosureRecord>().Where(x => x.RequestId == request).ToArrayAsync());
        var audit = Assert.Single(await check.AuditEvents.Where(x => x.EventType == "field-advance.settlement.close").ToArrayAsync());
        Assert.Null(audit.ActorId);
    }

    [Fact]
    public async Task Open_adjustment_or_family_hold_prevents_closure()
    {
        await using var f = await Ready(postgres);
        await Grant(f, AuthorId, AuthorPersonId, "request.create");
        var root = await Eligible(f);
        await using var db = f.CreateContext();
        var family = (await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == root)).FamilyId;
        var requestId = await Eligible(f, await Request(f, family, root));
        var closure = CloseService(f, db);
        Assert.True(await closure.TryCloseAsync(root, default));
        var original = await db.Set<ClosureRecord>().AsNoTracking().SingleAsync();
        var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == root);
        var adjustment = await C.Row<SettlementAdjustmentCase>(f, db, r);
        adjustment.ClosureRecordId = original.Id;
        db.Add(adjustment);
        db.Add(new AdvancePaymentHold
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            FamilyId = family,
            Code = "PaymentMismatch",
            Reason = "เหตุระงับทั้งชุดจำลอง",
            CreatedBy = RecipientId,
            CreatedByPersonId = RecipientPersonId,
            CreatedAtUtc = f.Clock.GetUtcNow()
        });
        await db.SaveChangesAsync();
        var ct = CancellationToken.None;
        Assert.False(await closure.TryCloseAsync(requestId, ct));
        var alreadyClosedRoot = await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == root);
        Assert.Equal("Closed", alreadyClosedRoot.State);
        var familyResult = await (await Reader(f, db, AuthorId)).ReadFamilyAsync(AuthorId, root, ct);
        Assert.Equal(200, familyResult.Status);
        var familySummary = familyResult.Value!;
        Assert.False(familySummary.Settled);
        var originalClosureRecords = await db.Set<ClosureRecord>().AsNoTracking().Where(x => x.RequestId == root).ToArrayAsync();
        Assert.Single(originalClosureRecords);
        Assert.Equal(original.SnapshotJson, originalClosureRecords[0].SnapshotJson);
        Assert.False(await closure.TryCloseAsync(root, ct));
    }

    [Theory]
    [InlineData("round")]
    [InlineData("completion-proposal")]
    [InlineData("declaration-withdrawn")]
    [InlineData("unpaid")]
    [InlineData("reservation")]
    [InlineData("installment")]
    [InlineData("payment-proposal")]
    [InlineData("hold")]
    [InlineData("request-hold")]
    public async Task Every_open_obligation_blocks_zero_balance_closure(string blocker)
    {
        await using var f = await Ready(postgres);
        var id = await Eligible(f, blocker is "unpaid" or "reservation" ? await FieldAdvanceSettlementDraftTests.Approved(f) : null);
        await using var db = f.CreateContext();
        var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == id);
        if (blocker == "round") db.Add(await C.Row<SettlementRound>(f, db, r));
        if (blocker == "completion-proposal")
        {
            var end = await C.Row<WorkCompletion>(f, db, r);
            end.CompletedOn = end.ClearingDueOn = end.RefundDueOn = new(2026, 10, 30); db.Add(end);
        }
        if (blocker == "declaration-withdrawn")
        {
            var old = await db.Set<SubmissionDeclaration>().SingleAsync(x => x.RequestId == id);
            var row = await C.Row<SubmissionDeclaration>(f, db, r);
            row.WorkCompletionId = old.WorkCompletionId; row.SupersedesId = old.Id; row.IsWithdrawal = true;
            row.State = "Accepted"; row.AcceptedAtUtc = row.ReviewedAtUtc = f.Clock.GetUtcNow();
            row.ReviewedBy = AuthorId; row.ReviewedByPersonId = AuthorPersonId; db.Add(row);
        }
        if (blocker is "unpaid" or "reservation")
        {
            var account = await db.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == id);
            account.Approved = account.Spendable = 100m;
            if (blocker == "reservation") account.Reserved = 100m;
        }
        if (blocker == "installment") db.Add(new AdvanceInstallment
        {
            Id = Guid.NewGuid(),
            RequestId = id,
            FamilyId = r.FamilyId,
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            CreatedBy = AuthorId,
            CreatedAtUtc = f.Clock.GetUtcNow()
        });
        if (blocker == "payment-proposal")
        {
            var receipt = FieldAdvancePaymentFixture.Receipt(); db.Add(receipt); await db.SaveChangesAsync();
            db.Add(new AdvancePaymentProposal
            {
                Id = Guid.NewGuid(),
                RequestId = id,
                FamilyId = r.FamilyId,
                WorkspaceId = WorkspaceId,
                ProjectId = ProjectId,
                CommandId = receipt.CommandId,
                Kind = "PaymentDisposition",
                ContentHash = new('a', 64),
                BeforeJson = "{}",
                AfterJson = "{}",
                ProposedBy = RecipientId,
                ProposedByPersonId = RecipientPersonId,
                Reason = "คำร้องจำลอง",
                CreatedAtUtc = f.Clock.GetUtcNow(),
                FamilyVersion = 1,
                RequestVersion = 1
            });
        }
        if (blocker == "hold") db.Add(new AdvancePaymentHold
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            FamilyId = r.FamilyId,
            RequestId = id,
            Code = "PaymentTermination",
            Reason = "ระงับจำลอง",
            CreatedBy = RecipientId,
            CreatedByPersonId = RecipientPersonId,
            CreatedAtUtc = f.Clock.GetUtcNow()
        });
        if (blocker == "request-hold") r.HoldCode = "NoApprover";
        await db.SaveChangesAsync();
        Assert.False(await CloseService(f, db).TryCloseAsync(id, default));
        Assert.Empty(await db.Set<ClosureRecord>().ToArrayAsync());
        Assert.Equal("Approved", (await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == id)).State);
    }

    [Fact]
    public async Task Authorized_family_summary_never_includes_hidden_money_and_rechecks_revocation()
    {
        await using var f = await Ready(postgres);
        await Grant(f, AuthorId, AuthorPersonId, "request.create");
        var grant = await Grant(f, RecipientId, RecipientPersonId, "request.delegate");
        var root = await Request(f, delegatePerson: RecipientPersonId);
        await using var db = f.CreateContext();
        var family = (await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == root)).FamilyId;
        var hidden = await Request(f, family, root);
        await FieldAdvanceRefundTests.SeedPaid(f, root, 100m);
        await FieldAdvanceRefundTests.SeedPaid(f, hidden, 9876m);
        var reader = await Reader(f, db, RecipientId);
        var visible = await reader.ReadFamilyAsync(RecipientId, root, default);
        Assert.Equal(200, visible.Status); Assert.False(visible.Value!.Complete);
        Assert.Single(visible.Value.Requests); Assert.Equal(root, visible.Value.Requests[0].RequestId);
        Assert.Equal(100m, visible.Value.Totals.OwnerReceived); Assert.Equal(100m, visible.Value.Totals.OwnerOutstanding);
        Assert.DoesNotContain(hidden.ToString(), JsonSerializer.Serialize(visible));
        Assert.DoesNotContain("9876", JsonSerializer.Serialize(visible));
        Assert.Equal(403, (await reader.ReadAsync(RecipientId, hidden, default)).Status);
        var full = await (await Reader(f, db, AuthorId)).ReadFamilyAsync(AuthorId, root, default);
        Assert.Equal(200, full.Status); Assert.True(full.Value!.Complete); Assert.Equal(9976m, full.Value.Totals.OwnerReceived);
        await db.Set<GrantAssignment>().Where(x => x.Id == grant).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAtUtc, f.Clock.GetUtcNow()).SetProperty(x => x.RevokedBy, AuthorId).SetProperty(x => x.RevocationReason, "ถอนสิทธิ์จำลอง"));
        Assert.Equal(403, (await reader.ReadAsync(RecipientId, root, default)).Status);
        Assert.Equal(403, (await reader.ReadFamilyAsync(RecipientId, root, default)).Status);
        Assert.Equal(403, (await (await Reader(f, db, ReviewerId)).ReadAsync(ReviewerId, root, default)).Status);
    }

    [Theory]
    [InlineData("Rejected", true)]
    [InlineData("Withdrawn", true)]
    [InlineData("Draft", false)]
    public async Task Existing_supplement_does_not_block_root_but_family_requires_terminal_obligation_free_members(string state, bool settled)
    {
        await using var f = await Ready(postgres); await Grant(f, AuthorId, AuthorPersonId, "request.create");
        var root = await Eligible(f);
        await using var db = f.CreateContext();
        var family = (await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == root)).FamilyId;
        var supplement = await Request(f, family, root);
        await db.Set<AdvanceRequest>().Where(x => x.Id == supplement).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, state));
        Assert.True(await CloseService(f, db).TryCloseAsync(root, default));
        var record = await db.Set<ClosureRecord>().SingleAsync();
        Assert.Equal((await db.Set<AdvanceFamily>().SingleAsync(x => x.Id == family)).Version, record.FamilyVersion);
        Assert.True(record.FamilyVersion > (await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == supplement)).FamilyVersionAtCreation);
        var summary = await (await Reader(f, db, AuthorId)).ReadFamilyAsync(AuthorId, root, default);
        Assert.Equal(200, summary.Status); Assert.True(summary.Value!.Complete); Assert.Equal(settled, summary.Value.Settled);
        Assert.Empty(await db.Set<ClosureRecord>().Where(x => x.RequestId == supplement).ToArrayAsync());
        if (settled)
        {
            await FieldAdvanceRefundTests.SeedPaid(f, supplement, 10m);
            Assert.False((await (await Reader(f, db, AuthorId)).ReadFamilyAsync(AuthorId, root, default)).Value!.Settled);
        }
    }

    [Fact]
    public async Task System_closure_retains_sources_and_immutable_history_and_blocks_unsafe_downgrade()
    {
        await using var f = await Ready(postgres);
        var id = await Eligible(f);
        await using var db = f.CreateContext();
        var receiptCount = await db.Set<FinanceCommandReceipt>().CountAsync();
        Assert.True(await CloseService(f, db).TryCloseAsync(id, default));
        Assert.False(await CloseService(f, db).TryCloseAsync(id, default));
        var row = await db.Set<ClosureRecord>().SingleAsync();
        Assert.Equal("System", row.ActorKind); Assert.Null(row.CommandId); Assert.Null(row.ProposedBy); Assert.Null(row.ProposedByPersonId);
        Assert.Null(row.ReviewedBy); Assert.Null(row.ReviewedByPersonId); Assert.Null(row.ReviewedAtUtc);
        Assert.Equal(receiptCount, await db.Set<FinanceCommandReceipt>().CountAsync());
        using var snapshot = JsonDocument.Parse(row.SnapshotJson);
        Assert.Equal(row.WorkCompletionId, snapshot.RootElement.GetProperty("WorkCompletionId").GetGuid());
        Assert.Equal(row.DeclarationId, snapshot.RootElement.GetProperty("DeclarationId").GetGuid());
        var audit = Assert.Single(await db.AuditEvents.Where(x => x.EventType == "field-advance.settlement.close").ToArrayAsync());
        Assert.Null(audit.ActorId);
        var references = await db.AuditMetadata.Where(x => x.AuditEventId == audit.Id && x.Key == "evidence-reference").Select(x => x.Value).SingleAsync();
        Assert.Contains(row.Id.ToString(), references); Assert.Contains(row.WorkCompletionId.ToString(), references); Assert.Contains(row.DeclarationId.ToString(), references);
        var mutation = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE field_advance_closure_records SET reason='changed' WHERE id={row.Id}"));
        Assert.Equal("23514", mutation.SqlState);
        var before = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        var down = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync("20261102041059_AddSupplementClosureOrdering"));
        Assert.Equal("system-closure-downgrade-blocked", down.MessageText);
        Assert.Equal(before, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Equal(row.SnapshotJson, (await db.Set<ClosureRecord>().AsNoTracking().SingleAsync()).SnapshotJson);
    }

    [Fact]
    public async Task Empty_migration_roundtrip_and_legacy_human_history_are_preserved()
    {
        await using var database = new IdentityDatabase(postgres.ConnectionString);
        await database.InitializeAsync(); await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var latest = (await db.Database.GetAppliedMigrationsAsync()).Last();
        await db.GetService<IMigrator>().MigrateAsync("20261102041059_AddSupplementClosureOrdering");
        await db.Database.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(latest, (await db.Database.GetAppliedMigrationsAsync()).Last());
        await db.GetService<IMigrator>().MigrateAsync("20261102041059_AddSupplementClosureOrdering");
        await FieldAdvanceFixture.SeedDirectoryAsync(db);
        // All preparation is on the predecessor schema; no current closure model is materialized.
        var f = new FieldAdvanceFixture(database); await f.SeedPoliciesAsync();
        var request = await Eligible(f);
        var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request);
        var end = await db.Set<WorkCompletion>().SingleAsync(); var declaration = await db.Set<SubmissionDeclaration>().SingleAsync();
        var receipt = FieldAdvancePaymentFixture.Receipt(); db.Add(receipt); await db.SaveChangesAsync();
        var id = Guid.NewGuid(); var at = f.Clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO field_advance_closure_records(id,workspace_id,project_id,family_id,request_id,version,command_id,
                proposed_by,proposed_by_person_id,created_at_utc,reviewed_by,reviewed_by_person_id,reviewed_at_utc,accepted_at_utc,
                state,reason,snapshot_json,request_version,family_version,work_completion_id,declaration_id)
            VALUES ({id},{WorkspaceId},{ProjectId},{r.FamilyId},{request},1,{receipt.CommandId},{AuthorId},{AuthorPersonId},{at},
                {ReviewerId},{ReviewerPersonId},{at},{at},'Accepted','ประวัติเดิม',jsonb_build_object(),1,1,{end.Id},{declaration.Id})
            """);
        var before = await db.Database.SqlQuery<string>($"SELECT row_to_json(c)::text AS \"Value\" FROM field_advance_closure_records c WHERE id={id}").SingleAsync();
        await db.Database.MigrateAsync();
        var after = await db.Database.SqlQuery<string>($"SELECT (to_jsonb(c)-'actor_kind')::text AS \"Value\" FROM field_advance_closure_records c WHERE id={id}").SingleAsync();
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(before), System.Text.Json.Nodes.JsonNode.Parse(after)));
        var human = await db.Set<ClosureRecord>().SingleAsync(); Assert.Equal("Human", human.ActorKind); Assert.Equal(AuthorId, human.ProposedBy);
        Assert.Equal(ReviewerId, human.ReviewedBy); Assert.Equal(receipt.CommandId, human.CommandId);
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Equal("supplement-order-downgrade-blocked", (await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync("20261102010154_AddIncidentPaymentBridge"))).MessageText);
        Assert.Equal(applied, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
    }

    [Theory]
    [InlineData("human-missing-receipt")]
    [InlineData("system-human-actor")]
    [InlineData("foreign-source")]
    public async Task Closure_provenance_and_scoped_sources_are_database_constraints(string fault)
    {
        await using var f = await Ready(postgres); var request = await Eligible(f); var other = await Eligible(f);
        await using var db = f.CreateContext(); var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request);
        var end = await db.Set<WorkCompletion>().SingleAsync(x => x.RequestId == request);
        var declaration = await db.Set<SubmissionDeclaration>().SingleAsync(x => x.RequestId == request);
        var row = new ClosureRecord
        {
            Id = Guid.NewGuid(),
            RequestId = request,
            FamilyId = r.FamilyId,
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            ActorKind = "System",
            State = "Accepted",
            AcceptedAtUtc = f.Clock.GetUtcNow(),
            CreatedAtUtc = f.Clock.GetUtcNow(),
            WorkCompletionId = end.Id,
            DeclarationId = declaration.Id,
            RequestVersion = 1,
            FamilyVersion = 1
        };
        if (fault == "human-missing-receipt") row.ActorKind = "Human";
        if (fault == "system-human-actor") row.ProposedBy = AuthorId;
        if (fault == "foreign-source") row.WorkCompletionId = (await db.Set<WorkCompletion>().SingleAsync(x => x.RequestId == other)).Id;
        db.Add(row); var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(fault == "foreign-source" ? "23503" : "23514", Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [Fact]
    public async Task Concurrent_closures_commit_only_one_record_and_one_system_audit()
    {
        await using var f = await Ready(postgres); var id = await Eligible(f);
        await using var firstDb = f.CreateContext(); await using var secondDb = f.CreateContext();
        var gate = new FieldAdvancePaymentRaceTests.Barrier();
        var first = CloseService(f, firstDb, gate.Audit(f, true, firstDb)).TryCloseAsync(id, default);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var second = CloseService(f, secondDb).TryCloseAsync(id, default);
        try { await FieldAdvancePaymentRaceTests.Waiting(f, second); } finally { gate.Release.TrySetResult(); }
        Assert.True(await first); Assert.False(await second);
        await using var check = f.CreateContext(); Assert.Single(await check.Set<ClosureRecord>().ToArrayAsync());
        Assert.Single(await check.AuditEvents.Where(x => x.EventType == "field-advance.settlement.close").ToArrayAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Refund_correction_and_close_follow_database_lock_order(bool correctionFirst)
    {
        await using var f = await Ready(postgres); var s = await BalancedRefund(f);
        var gate = new FieldAdvancePaymentRaceTests.Barrier();
        await using var closeDb = f.CreateContext(); await using var correctionDb = f.CreateContext();
        using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        var command = await Meta(f, s.Request);
        var correction = new RefundCorrectionService(correctionDb, await Access(f, correctionDb, ReviewerId),
            await Executor(f, correctionDb, ReviewerId, gate.Audit(f, correctionFirst, correctionDb)), f.Clock, files.Options);
        var close = CloseService(f, closeDb, gate.Audit(f, !correctionFirst, closeDb));
        Task<FinanceResult<SettlementCommandResult>> pending; Task<bool> closing;
        if (correctionFirst)
        {
            pending = correction.ProposeAsync(ReviewerId, s.Request, new(s.Refund, 900m, [s.Evidence]), command, default);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); closing = close.TryCloseAsync(s.Request, default);
        }
        else
        {
            closing = close.TryCloseAsync(s.Request, default); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            pending = correction.ProposeAsync(ReviewerId, s.Request, new(s.Refund, 900m, [s.Evidence]), command, default);
        }
        try { await FieldAdvancePaymentRaceTests.Waiting(f, correctionFirst ? closing : pending); } finally { gate.Release.TrySetResult(); }
        Assert.Equal(!correctionFirst, await closing); Assert.Equal(correctionFirst ? 201 : 409, (await pending).Status);
        if (!correctionFirst)
        {
            Assert.Equal(201, (await correction.ProposeAsync(ReviewerId, s.Request, new(s.Refund, 900m, [s.Evidence]), await Meta(f, s.Request), default)).Status);
            Assert.Single(await correctionDb.Set<SettlementAdjustmentCase>().ToArrayAsync());
            Assert.Equal("Closed", (await correctionDb.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == s.Request)).State);
        }
        Assert.False(await close.TryCloseAsync(s.Request, default));
    }

    [Fact]
    public async Task Refund_acceptance_racing_close_rechecks_committed_balance_and_late_refund_is_denied()
    {
        await using var f = await Ready(postgres); var s = await BalancedRefund(f, accept: false);
        await using var closeDb = f.CreateContext();
        Assert.False(await CloseService(f, closeDb).TryCloseAsync(s.Request, default));
        var gate = new FieldAdvancePaymentRaceTests.Barrier();
        var acceptance = FieldAdvanceRefundTests.RefundCall(f, s.Request, "Approve", s.Refund, actor: ReviewerId, barrier: gate);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var closing = CloseService(f, closeDb).TryCloseAsync(s.Request, default);
        try { await FieldAdvancePaymentRaceTests.Waiting(f, closing); } finally { gate.Release.TrySetResult(); }
        Assert.Equal(200, (await acceptance).Status); Assert.True(await closing);
        Assert.Equal(403, (await FieldAdvanceRefundTests.RefundCall(f, s.Request, "Propose", input: FieldAdvanceRefundTests.RefundInputFor(1m, s.Evidence))).Status);
        var summary = await (await Reader(f, closeDb, AuthorId)).ReadAsync(AuthorId, s.Request, default);
        Assert.Equal(1000m, summary.Value!.Totals.ConfirmedRefund); Assert.Equal(0m, summary.Value.Totals.OwnerOutstanding);
        Assert.Equal(new[] { SettlementActions.Read }, summary.Value.AllowedActions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Shortfall_prepare_and_close_cannot_commit_against_stale_family(bool shortfallFirst)
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready();
        using var files = new FieldAdvancePaymentRecoveryTests.Files();
        var s = await FieldAdvancePaymentIncidentTests.Incident(f, files, true, 8000, 0);
        await FieldAdvanceIncidentPaymentBridgeTests.Recovery(f, files, s, 8000, "Owner");
        Assert.Equal(200, (await FieldAdvanceIncidentPaymentBridgeTests.Bridge(f, files, s, "Release")).Status);
        await Eligible(f, s.Request);
        var gate = new FieldAdvancePaymentRaceTests.Barrier();
        await using var closeDb = f.CreateContext(); await using var bridgeDb = f.CreateContext();
        var command = await Meta(f, s.Request);
        var bridge = new IncidentPaymentBridge(bridgeDb, await Access(f, bridgeDb, RecipientId),
            await Executor(f, bridgeDb, RecipientId, gate.Audit(f, shortfallFirst, bridgeDb)), f.Clock, files.Options);
        var close = CloseService(f, closeDb, gate.Audit(f, !shortfallFirst, closeDb));
        Task<FinanceResult<SettlementCommandResult>> pending; Task<bool> closing;
        if (shortfallFirst)
        {
            pending = bridge.PrepareShortfallAsync(RecipientId, s.Request, s.Id, 2000m, command, default);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); closing = close.TryCloseAsync(s.Request, default);
        }
        else
        {
            closing = close.TryCloseAsync(s.Request, default); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            pending = bridge.PrepareShortfallAsync(RecipientId, s.Request, s.Id, 2000m, command, default);
        }
        try { await FieldAdvancePaymentRaceTests.Waiting(f, shortfallFirst ? closing : pending); } finally { gate.Release.TrySetResult(); }
        Assert.Equal(!shortfallFirst, await closing);
        Assert.Equal(shortfallFirst ? 201 : 403, (await pending).Status);
        if (shortfallFirst)
        {
            Assert.Equal("PendingReview", (await bridgeDb.Set<AdvanceInstallment>().AsNoTracking().SingleAsync(x => x.Id != s.Installment)).State);
            Assert.Empty(await bridgeDb.Set<ClosureRecord>().ToArrayAsync());
        }
    }

    [Fact]
    public async Task Sibling_request_hold_is_not_a_family_hold_or_visible_balance_case()
    {
        await using var f = await Ready(postgres); await Grant(f, RecipientId, RecipientPersonId, "request.delegate");
        var root = await Eligible(f, await Request(f, delegatePerson: RecipientPersonId));
        await using var db = f.CreateContext(); var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == root);
        var sibling = await Request(f, r.FamilyId, root);
        db.Add(new AdvancePaymentHold
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            FamilyId = r.FamilyId,
            RequestId = sibling,
            Code = "PaymentTermination",
            Reason = "เฉพาะคำขอซ่อน",
            CreatedBy = RecipientId,
            CreatedByPersonId = RecipientPersonId,
            CreatedAtUtc = f.Clock.GetUtcNow()
        });
        await db.SaveChangesAsync();
        var summary = await (await Reader(f, db, RecipientId)).ReadFamilyAsync(RecipientId, root, default);
        Assert.False(summary.Value!.Complete); Assert.False(summary.Value.Totals.HasOpenCase); Assert.Empty(summary.Value.Requests[0].Holds);
        Assert.True(await CloseService(f, db).TryCloseAsync(root, default));
    }

    private static async Task<(Guid Request, Guid Refund, Guid Evidence)> BalancedRefund(FieldAdvanceFixture f, bool accept = true)
    {
        var request = await FieldAdvanceSettlementDraftTests.Approved(f);
        await FieldAdvanceSettlementDraftTests.Document(f, request);
        await FieldAdvanceRefundTests.SeedPaid(f, request, 1000m);
        await Grant(f, ReviewerId, ReviewerPersonId, "finance.check");
        await using var db = f.CreateContext();
        var account = await db.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == request);
        account.Paid = 1000m; account.EverPaid = true;
        (await db.Set<AdvanceInstallment>().SingleAsync(x => x.RequestId == request)).State = "Paid";
        (await db.Set<AdvancePaymentAttempt>().SingleAsync(x => x.RequestId == request)).State = "Matched";
        await db.SaveChangesAsync();
        var evidence = (await db.Set<AdvanceEvidence>().SingleAsync(x => x.RequestId == request)).Id;
        var proposed = await FieldAdvanceRefundTests.RefundCall(f, request, "Propose", input: FieldAdvanceRefundTests.RefundInputFor(1000m, evidence));
        Assert.Equal(201, proposed.Status); var refund = proposed.Value!.EntityId!.Value;
        Assert.Equal(200, (await FieldAdvanceRefundTests.RefundCall(f, request, "Confirm", refund)).Status);
        if (accept) Assert.Equal(200, (await FieldAdvanceRefundTests.RefundCall(f, request, "Approve", refund, actor: ReviewerId)).Status);
        await Eligible(f, request); return (request, refund, evidence);
    }

    [Fact]
    public async Task Worker_retries_failed_commit_without_duplicate_history_and_does_not_starve_next_candidate()
    {
        await using var f = await Ready(postgres); var first = await Eligible(f); var second = await Eligible(f);
        await using var db = f.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE SEQUENCE task14_attempts;
            CREATE FUNCTION task14_fail_once() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF nextval('task14_attempts')=1 THEN RAISE EXCEPTION 'simulated crash before commit' USING ERRCODE='40001'; END IF;
                RETURN NEW;
            END $$;
            CREATE CONSTRAINT TRIGGER task14_commit_failure AFTER INSERT ON field_advance_closure_records
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION task14_fail_once();
            """);
        await RunWorker(f, async () => await db.Set<ClosureRecord>().CountAsync() == 2);
        Assert.Equal(2, await db.Set<ClosureRecord>().CountAsync());
        Assert.Equal(3L, await db.Database.SqlQueryRaw<long>("SELECT last_value AS \"Value\" FROM task14_attempts").SingleAsync());
        Assert.Equal(2, await db.AuditEvents.CountAsync(x => x.EventType == "field-advance.settlement.close"));
        Assert.All(await db.Set<AdvanceRequest>().Where(x => x.Id == first || x.Id == second).ToArrayAsync(), x => Assert.Equal("Closed", x.State));
        var originalClosures = await db.Set<ClosureRecord>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync();
        var originalAudits = await db.AuditEvents.AsNoTracking()
            .Where(x => x.EventType == "field-advance.settlement.close").OrderBy(x => x.Id).ToArrayAsync();
        // The first worker has stopped. Commit a new candidate before constructing its replacement.
        var pending = await Eligible(f);
        Assert.Equal("Approved", await db.Set<AdvanceRequest>().Where(x => x.Id == pending).Select(x => x.State).SingleAsync());
        Assert.False(await db.Set<ClosureRecord>().AnyAsync(x => x.RequestId == pending));
        await RunWorker(f, async () => await db.Set<ClosureRecord>().AnyAsync(x => x.RequestId == pending));
        Assert.Equal("Closed", await db.Set<AdvanceRequest>().Where(x => x.Id == pending).Select(x => x.State).SingleAsync());
        Assert.Equal(3, await db.Set<ClosureRecord>().CountAsync());
        Assert.Equal(3, await db.AuditEvents.CountAsync(x => x.EventType == "field-advance.settlement.close"));
        Assert.Equal(4L, await db.Database.SqlQueryRaw<long>("SELECT last_value AS \"Value\" FROM task14_attempts").SingleAsync());
        var originalClosureIds = originalClosures.Select(x => x.Id).ToArray();
        var originalAuditIds = originalAudits.Select(x => x.Id).ToArray();
        Assert.Equal(JsonSerializer.Serialize(originalClosures), JsonSerializer.Serialize(await db.Set<ClosureRecord>()
            .AsNoTracking().Where(x => originalClosureIds.Contains(x.Id)).OrderBy(x => x.Id).ToArrayAsync()));
        Assert.Equal(JsonSerializer.Serialize(originalAudits), JsonSerializer.Serialize(await db.AuditEvents
            .AsNoTracking().Where(x => originalAuditIds.Contains(x.Id)).OrderBy(x => x.Id).ToArrayAsync()));
    }

    [Theory]
    [InlineData("end")]
    [InlineData("declaration")]
    public async Task Zero_balance_without_owner_declaration_or_pm_end_is_not_closable(string missing)
    {
        await using var f = await Ready(postgres); var id = await Request(f);
        if (missing == "declaration")
        {
            await using var seed = f.CreateContext(); var r = await seed.Set<AdvanceRequest>().SingleAsync(x => x.Id == id);
            var end = await C.Row<WorkCompletion>(f, seed, r, RecipientId, RecipientPersonId);
            end.CompletedOn = end.ClearingDueOn = end.RefundDueOn = new(2026, 10, 30);
            end.State = "Accepted"; end.AcceptedAtUtc = end.ReviewedAtUtc = f.Clock.GetUtcNow();
            end.ReviewedBy = RecipientId; end.ReviewedByPersonId = RecipientPersonId; seed.Add(end); await seed.SaveChangesAsync();
        }
        await using var db = f.CreateContext(); Assert.False(await CloseService(f, db).TryCloseAsync(id, default));
        Assert.Empty(await db.Set<ClosureRecord>().ToArrayAsync());
    }

    private static async Task RunWorker(FieldAdvanceFixture f, Func<Task<bool>> finished, Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor? interceptor = null)
    {
        var services = new ServiceCollection(); services.AddLogging();
        services.AddSingleton<TimeProvider>(f.Clock);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FieldAdvances:SettlementClosure:IntervalSeconds"] = "0.05",
            ["FieldAdvances:SettlementClosure:BatchSize"] = "1"
        }).Build());
        services.AddDbContext<Tpr10DbContext>(o => { o.UseNpgsql(f.Database.ConnectionString); if (interceptor is not null) o.AddInterceptors(interceptor); });
        services.AddScoped<IAuditEventWriter>(sp => new AuditEventWriter(sp.GetRequiredService<Tpr10DbContext>(), new CorrelationContext(), f.Clock));
        services.AddFieldAdvances(); await using var provider = services.BuildServiceProvider();
        var worker = provider.GetServices<IHostedService>().OfType<SettlementClosureWorker>().Single();
        await worker.StartAsync(default);
        try
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            do { if (await finished()) return; await Task.Delay(25); } while (DateTime.UtcNow < until);
            Assert.True(await finished(), "worker must retry durable candidates and finish within the test deadline");
        }
        finally { await worker.StopAsync(default); }
    }

    [Fact]
    public async Task Explicit_7C_termination_allows_closure_but_retains_payment_stop_history()
    {
        await using var f = await new FieldAdvanceSupplementTests(postgres).Ready();
        var request = await L.Submit(f, await L.Create(f));
        var first = await L.Decide(f, request, "Approve"); Assert.Equal(200, first.Status);
        var second = await L.Decide(f, first.Value!, "Approve", RecipientId); Assert.Equal(200, second.Status);
        var director = Guid.Parse("71000000-0000-0000-0000-000000000051");
        var third = await L.Decide(f, second.Value!, "Approve", director); Assert.Equal(200, third.Status); Assert.Equal("Approved", third.Value!.State);
        await Eligible(f, request.Id);
        var checker = Guid.NewGuid(); await FieldAdvanceSupplementTests.Actor(f, checker, "finance.check");
        await using var db = f.CreateContext(); Assert.False(await CloseService(f, db).TryCloseAsync(request.Id, default));
        Assert.Equal(200, (await Disposition(f, request.Id, checker, "Propose")).Status);
        var proposal = (await db.Set<AdvancePaymentProposal>().SingleAsync(x => x.RequestId == request.Id)).Id;
        Assert.Equal(200, (await Disposition(f, request.Id, director, "Confirm", proposal)).Status);
        Assert.False(await CloseService(f, db).TryCloseAsync(request.Id, default));
        Assert.Equal(200, (await Disposition(f, request.Id, director, "Finalize", proposal)).Status);
        Assert.True(await CloseService(f, db).TryCloseAsync(request.Id, default));
        var hold = await db.Set<AdvancePaymentHold>().SingleAsync(x => x.RequestId == request.Id);
        Assert.Equal("PaymentTerminated", hold.Code); Assert.Null(hold.ReleasedAtUtc);
        var summary = await (await Reader(f, db, AuthorId)).ReadFamilyAsync(AuthorId, request.Id, default);
        Assert.True(summary.Value!.Settled);
    }

    private static async Task<FinanceResult<PaymentView>> Disposition(FieldAdvanceFixture f, Guid id, Guid actor, string operation, Guid? proposal = null)
    {
        await using var db = f.CreateContext(); var session = await f.SessionAsync(actor);
        var finance = FieldAdvanceAccessTests.Access(db, session, f.Clock); var people = new TPR10.Api.FieldAdvances.Access.FinancePersonResolver(db, f.Clock);
        var paymentAccess = new PaymentAccess(db, finance, people, new(db, finance, people, f.Clock), f.Clock);
        var executor = new PaymentCommandExecutor(db, paymentAccess, new(db, session, finance, people, f.Clock), new AuditEventWriter(db, new CorrelationContext(), f.Clock), f.Clock);
        var service = new PaymentDispositionService(db, executor, people, f.Clock);
        var meta = await Meta(f, id); var command = new PaymentCommand(meta.CommandId, meta.FamilyVersion, meta.RequestVersion, null, meta.Reason);
        return operation switch
        {
            "Propose" => await service.ProposeAsync(actor, id, "TerminateRemaining", command, default),
            "Confirm" => await service.ConfirmAsync(actor, proposal!.Value, command, default),
            _ => await service.FinalizeAsync(actor, proposal!.Value, command, default)
        };
    }

    [Fact]
    public async Task Accepted_resubmission_closes_with_released_old_document_reservations_retained()
    {
        await using var f = await Ready(postgres);
        var (request, round) = await FieldAdvanceSettlementReviewTests.Draft(f);
        await using var db = f.CreateContext(); var doc = await db.Set<ExpenseDocument>().SingleAsync();
        Assert.Equal(200, (await FieldAdvanceSettlementDraftTests.Save(f, request, round, FieldAdvanceSettlementDraftTests.Input(1000m, doc.Id))).Status);
        await FieldAdvanceRefundTests.SeedPaid(f, request, 1000m);
        var account = await db.Set<AdvancePaymentAccount>().SingleAsync(); account.Paid = 1000m; account.EverPaid = true;
        (await db.Set<AdvanceInstallment>().SingleAsync()).State = "Paid"; (await db.Set<AdvancePaymentAttempt>().SingleAsync()).State = "Matched";
        await db.SaveChangesAsync();
        await FieldAdvanceSettlementReviewTests.ActivateCorrectionPolicy(f, 3);
        Assert.Equal(200, (await FieldAdvanceSettlementReviewTests.Submit(f, AuthorId, request, round, await FieldAdvanceSettlementReviewTests.ConfirmCommand(f, request, round))).Status);
        Assert.Equal(200, (await FieldAdvanceSettlementReviewTests.Review(f, ReviewerId, request, round, "Return")).Status);
        Assert.Equal(200, (await FieldAdvanceSettlementDraftTests.Save(f, request, round, FieldAdvanceSettlementDraftTests.Input(1000m, doc.Id))).Status);
        Assert.Equal(200, (await FieldAdvanceSettlementReviewTests.Submit(f, AuthorId, request, round, await FieldAdvanceSettlementReviewTests.ConfirmCommand(f, request, round))).Status);
        foreach (var actor in new[] { ReviewerId, RecipientId, AliasAccountId })
            Assert.Equal(200, (await FieldAdvanceSettlementReviewTests.Review(f, actor, request, round)).Status);
        await Eligible(f, request);
        Assert.True(await db.Set<ExpenseAllocation>().AnyAsync(x => x.State == "Released"));
        Assert.False(await SettlementClosureService.HasUnfinishedAsync(db, request, default),
            JsonSerializer.Serialize(await db.Set<ExpenseAllocation>().Select(x => new { x.State, x.AcceptedAtUtc }).ToArrayAsync()));
        Assert.Equal(0m, (await FieldAdvanceRefundTests.Totals(f, request)).OwnerOutstanding);
        Assert.True(await CloseService(f, db).TryCloseAsync(request, default));
        Assert.True(await db.Set<ExpenseAllocation>().AnyAsync(x => x.State == "Released"));
        var summary = await (await Reader(f, db, AuthorId)).ReadAsync(AuthorId, request, default);
        Assert.Equal(1000m, summary.Value!.Totals.ApprovedExpense); Assert.Equal(0m, summary.Value.Totals.OwnerOutstanding);
    }

    [Fact]
    public async Task Consumed_shortfall_and_accepted_owner_returns_are_terminal_financial_facts()
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready();
        using var files = new FieldAdvancePaymentRecoveryTests.Files();
        var s = await FieldAdvancePaymentIncidentTests.Incident(f, files, true, 8000, 0);
        await FieldAdvanceIncidentPaymentBridgeTests.Recovery(f, files, s, 8000, "Owner");
        Assert.Equal(200, (await FieldAdvanceIncidentPaymentBridgeTests.Bridge(f, files, s, "Release")).Status);
        var prepared = await FieldAdvanceIncidentPaymentBridgeTests.Bridge(f, files, s, "Prepare", 2000m);
        Assert.Equal(201, prepared.Status); var installment = prepared.Value!.EntityId!.Value;
        Assert.Equal(200, (await FieldAdvancePaymentRecoveryTests.Prepare(f, s.Request, installment, "Approve")).Status);
        Assert.Equal(200, (await FieldAdvancePaymentRecoveryTests.Call(f, s.Request, installment, "Start", files)).Status);
        Assert.Equal(200, (await FieldAdvancePaymentRecoveryTests.Call(f, s.Request, installment, "Record", files, s.Actual with { Amount = "2000" })).Status);
        await Eligible(f, s.Request);
        await using var db = f.CreateContext(); var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == s.Request);
        var refund = await C.Row<Refund>(f, db, r); refund.Amount = 2000m; refund.Channel = "Cash"; refund.RefundedOn = new(2026, 10, 30);
        refund.EvidenceId = (await FieldAdvancePaymentRecoveryTests.Actual(f, files, s.Request, [7, 9, 1])).EvidenceIds[0];
        refund.State = "Accepted"; refund.AcceptedAtUtc = refund.ReviewedAtUtc = f.Clock.GetUtcNow();
        refund.ReviewedBy = FieldAdvancePaymentRecoveryTests.Checker; refund.ReviewedByPersonId = FieldAdvancePaymentRecoveryTests.CheckerPerson;
        db.Add(refund); await db.SaveChangesAsync();
        Assert.Equal("Consumed", (await db.Set<PaymentShortfallClaim>().SingleAsync()).State);
        Assert.True(await CloseService(f, db).TryCloseAsync(s.Request, default));
    }

    [Fact]
    public async Task Superseded_case_payment_history_does_not_reopen_terminal_family()
    {
        await using var f = await Ready(postgres); await Grant(f, AuthorId, AuthorPersonId, "request.create");
        var id = await Eligible(f); await using var db = f.CreateContext();
        Assert.True(await CloseService(f, db).TryCloseAsync(id, default));
        var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == id);
        var adjustment = await C.Row<SettlementAdjustmentCase>(f, db, r);
        adjustment.ClosureRecordId = (await db.Set<ClosureRecord>().SingleAsync()).Id; adjustment.State = "Rejected";
        db.Add(adjustment); await db.SaveChangesAsync();
        await FieldAdvanceSettlementDraftTests.Document(f, id);
        var payment = await C.Row<SettlementCasePayment>(f, db, r); payment.CaseId = adjustment.Id; payment.Amount = 1m;
        payment.PaidOn = new(2026, 10, 30); payment.State = "Superseded"; payment.EvidenceId = (await db.Set<AdvanceEvidence>().SingleAsync()).Id;
        db.Add(payment); await db.SaveChangesAsync();
        var result = await (await Reader(f, db, AuthorId)).ReadFamilyAsync(AuthorId, id, default);
        Assert.Equal(200, result.Status); Assert.True(result.Value!.Settled);
        Assert.Single(await db.Set<ClosureRecord>().ToArrayAsync());
    }

    [Fact]
    public async Task Summary_offers_authorized_refund_correction_before_and_after_closure_but_never_worker_close()
    {
        await using var f = await Ready(postgres); var s = await BalancedRefund(f);
        await using var db = f.CreateContext(); var finance = await Reader(f, db, ReviewerId);
        var before = await finance.ReadAsync(ReviewerId, s.Request, default);
        Assert.Equal(200, before.Status); Assert.Contains(SettlementActions.CorrectionCreate, before.Value!.AllowedActions);
        Assert.DoesNotContain(SettlementActions.Close, before.Value.AllowedActions);
        Assert.True(await CloseService(f, db).TryCloseAsync(s.Request, default));
        var after = await finance.ReadAsync(ReviewerId, s.Request, default);
        Assert.Contains(SettlementActions.CorrectionCreate, after.Value!.AllowedActions);
        var owner = await (await Reader(f, db, AuthorId)).ReadAsync(AuthorId, s.Request, default);
        Assert.DoesNotContain(SettlementActions.CorrectionCreate, owner.Value!.AllowedActions);
    }

    private static SettlementClosureService CloseService(FieldAdvanceFixture f, Tpr10DbContext db, IAuditEventWriter? audit = null)
        => new(db, audit ?? new AuditEventWriter(db, new CorrelationContext(), f.Clock), f.Clock);
    private static async Task<SettlementReadService> Reader(FieldAdvanceFixture f, Tpr10DbContext db, Guid actor)
        => new(db, await Access(f, db, actor));

    private static async Task<Guid> Eligible(FieldAdvanceFixture f, Guid? existing = null)
    {
        var id = existing ?? await Request(f);
        await using var db = f.CreateContext();
        var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == id);
        var completion = await C.Row<WorkCompletion>(f, db, r, RecipientId, RecipientPersonId);
        completion.ClearingDays = 7; completion.CompletedOn = new(2026, 10, 25); completion.ClearingDueOn = new(2026, 11, 1); completion.RefundDueOn = completion.ClearingDueOn;
        completion.State = "Accepted"; completion.AcceptedAtUtc = f.Clock.GetUtcNow(); completion.ReviewedAtUtc = f.Clock.GetUtcNow();
        completion.ReviewedBy = RecipientId; completion.ReviewedByPersonId = RecipientPersonId;
        db.Add(completion); await db.SaveChangesAsync();
        var declaration = await C.Row<SubmissionDeclaration>(f, db, r);
        declaration.WorkCompletionId = completion.Id; declaration.State = "Accepted";
        declaration.AcceptedAtUtc = f.Clock.GetUtcNow(); declaration.ReviewedAtUtc = f.Clock.GetUtcNow();
        declaration.ReviewedBy = AuthorId; declaration.ReviewedByPersonId = AuthorPersonId;
        db.Add(declaration); await db.SaveChangesAsync();
        return id;
    }
}
