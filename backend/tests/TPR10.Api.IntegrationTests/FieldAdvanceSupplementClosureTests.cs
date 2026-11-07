using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using System.Text.Json;
using System.Net.Http.Json;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Settlements;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Requests;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvanceSupplementTests;
using C = TPR10.Api.IntegrationTests.FieldAdvanceSettlementCommandTests;
using L = TPR10.Api.IntegrationTests.FieldAdvanceRequestLifecycleTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceSupplementClosureTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Existing_returned_supplement_continues_but_new_creation_is_denied()
    {
        await using var f = await new FieldAdvanceSupplementTests(postgres).Ready();
        var root = await PaidRoot(f);
        var pending = await L.Submit(f, await CreateSupplement(f, root, 30));
        var returned = await L.Decide(f, pending, "Return"); Assert.Equal(200, returned.Status);
        var oldApprovalBasis = await L.Frozen(f, root.RevisionId);
        var oldSupplement = await L.Frozen(f, pending.RevisionId);
        await Close(f, root.Id);
        var revised = await L.Command(f, returned.Value!, "ReviseAsync"); Assert.Equal(200, revised.Status);
        var resubmitExisting = await Confirm(f, revised.Value!, await Quote(f, revised.Value!));
        await using var db = f.CreateContext();
        var createAfterClosure = await (await Supplements(f, db, AuthorId)).CreateAsync(AuthorId, root.Id, Content(20), L.Meta(0), default);
        var sourceHistoricalBasis = await L.Frozen(f, root.RevisionId);
        Assert.Equal(200, resubmitExisting.Status);
        Assert.Equal(409, createAfterClosure.Status);
        Assert.Equal(oldApprovalBasis, sourceHistoricalBasis);
        Assert.Equal(oldSupplement, await L.Frozen(f, pending.RevisionId));
        Assert.Equal("Closed", (await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == root.Id)).State);
        Assert.Equal(2, await db.Set<AdvanceRequest>().CountAsync());
        Assert.Equal(pending.Id, (await db.Set<AdvanceFamily>().SingleAsync()).PendingSupplementId);
    }

    [Fact]
    public async Task Legacy_7C_draft_gets_truthful_baseline_and_continues_after_future_closure()
    {
        var legacy = await LegacyDraft(); await using var f = legacy.Fixture;
        var root = legacy.Root; var id = legacy.Supplement;
        await using var db = f.CreateContext();
        await db.Database.MigrateAsync();
        var supplement = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == id);
        Assert.Null(supplement.FamilyVersionAtCreation);
        Assert.Equal((await db.Set<AdvanceFamily>().SingleAsync()).Version, supplement.LegacyFamilyVersionBaseline);
        var down = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync("20261102010154_AddIncidentPaymentBridge"));
        Assert.Equal("supplement-order-downgrade-blocked", down.MessageText);
        await PrepareLegacyPolicy(f);
        await Close(f, root);
        var session = await f.SessionAsync(AuthorId);
        var submission = L.Submission(f, db, session);
        var quote = await submission.QuoteAsync(AuthorId, id, default); Assert.Equal(200, quote.Status);
        var result = await submission.ConfirmAsync(AuthorId, id, quote.Value!.ConfirmationHash, L.Meta(1), default);
        Assert.Equal(200, result.Status);
        Assert.Equal(12000m, quote.Value.Basis.Amount);
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("closure-record")]
    [InlineData("missing-event")]
    [InlineData("wrong-receipt")]
    public async Task Unprovable_legacy_aborts_upgrade_atomically(string fault)
    {
        var legacy = await LegacyDraft(fault); await using var f = legacy.Fixture;
        var id = legacy.Supplement;
        await using var db = f.CreateContext();
        // Bring only the existing 7D migrations forward; ordering must be the failing migration.
        await db.GetService<IMigrator>().MigrateAsync("20261102010154_AddIncidentPaymentBridge");
        if (fault == "closure-record")
        {
            var familyId = await db.Set<AdvanceRequest>().Where(x => x.Id == legacy.Root).Select(x => x.FamilyId).SingleAsync();
            var r = new AdvanceRequest { Id = legacy.Root, FamilyId = familyId, WorkspaceId = WorkspaceId, ProjectId = ProjectId };
            var completion = await C.Row<WorkCompletion>(f, db, r);
            completion.CompletedOn = new(2026, 10, 25); completion.ClearingDueOn = completion.CompletedOn; completion.RefundDueOn = completion.CompletedOn;
            db.Add(completion); await db.SaveChangesAsync();
            var declaration = await C.Row<SubmissionDeclaration>(f, db, r); declaration.WorkCompletionId = completion.Id;
            db.Add(declaration); await db.SaveChangesAsync();
            var closure = await C.Row<ClosureRecord>(f, db, r);
            closure.WorkCompletionId = completion.Id; closure.DeclarationId = declaration.Id; closure.RequestVersion = 1; closure.FamilyVersion = 1;
            closure.State = "Accepted"; closure.AcceptedAtUtc = f.Clock.GetUtcNow(); closure.ReviewedAtUtc = f.Clock.GetUtcNow();
            closure.ReviewedBy = ReviewerId; closure.ReviewedByPersonId = ReviewerPersonId;
            // This fixture intentionally runs before Task14: insert only the historical
            // human columns, not the current model's system-attribution discriminator.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO field_advance_closure_records(id,workspace_id,project_id,family_id,request_id,version,command_id,
                    proposed_by,proposed_by_person_id,created_at_utc,reviewed_by,reviewed_by_person_id,reviewed_at_utc,accepted_at_utc,
                    state,reason,snapshot_json,request_version,family_version,work_completion_id,declaration_id)
                VALUES ({closure.Id},{closure.WorkspaceId},{closure.ProjectId},{closure.FamilyId},{closure.RequestId},{closure.Version},
                    {closure.CommandId},{closure.ProposedBy},{closure.ProposedByPersonId},{closure.CreatedAtUtc},{closure.ReviewedBy},
                    {closure.ReviewedByPersonId},{closure.ReviewedAtUtc},{closure.AcceptedAtUtc},{closure.State},{closure.Reason},
                    CAST({closure.SnapshotJson} AS jsonb),{closure.RequestVersion},{closure.FamilyVersion},{closure.WorkCompletionId},{closure.DeclarationId})
                """);
        }
        var before = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
        Assert.Equal("23514", ex.SqlState); Assert.Equal("supplement-order-legacy-unproven", ex.MessageText);
        Assert.Equal(before, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_name='field_advance_requests' AND column_name='family_version_at_creation') AS \"Value\"").SingleAsync());
        Assert.Equal(1, await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM field_advance_requests WHERE id={id}").SingleAsync());
    }

    [Theory]
    [InlineData("20261102010154_AddIncidentPaymentBridge")]
    [InlineData("20261031221353_AllowMultipleDocumentDuplicateCases")]
    public async Task Downgrade_with_normal_request_preserves_latest_schema_before_lower_history_guard(string target)
    {
        await using var f = await new FieldAdvanceSupplementTests(postgres).Ready();
        var root = await PaidRoot(f);
        await using var db = f.CreateContext();
        Assert.False(await db.Set<AdvanceRequest>().AnyAsync(x => x.RelationKind == "Supplement"));
        var before = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        var triggersBefore = await db.Database.SqlQueryRaw<string>("""SELECT tgname::text AS "Value" FROM pg_trigger WHERE NOT tgisinternal ORDER BY tgname""").ToArrayAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(target));
        Assert.Equal("23514", error.SqlState);
        Assert.Equal(before, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Equal(triggersBefore, await db.Database.SqlQueryRaw<string>("""SELECT tgname::text AS "Value" FROM pg_trigger WHERE NOT tgisinternal ORDER BY tgname""").ToArrayAsync());
        var retained = await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == root.Id);
        Assert.Null(retained.FamilyVersionAtCreation);
        Assert.Null(retained.LegacyFamilyVersionBaseline);
        // The current model and real supplement creation must remain usable after rejection.
        var supplement = await CreateSupplement(f, root, 20);
        Assert.True((await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == supplement.Id)).FamilyVersionAtCreation > 0);
    }

    [Fact]
    public async Task Downgrade_cannot_erase_order_provenance()
    {
        await using var f = await new FieldAdvanceSupplementTests(postgres).Ready();
        var root = await PaidRoot(f); var supplement = await CreateSupplement(f, root, 20);
        await using var db = f.CreateContext();
        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync("20261102010154_AddIncidentPaymentBridge"));
        Assert.Equal("supplement-order-downgrade-blocked", ex.MessageText);
        Assert.True((await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == supplement.Id)).FamilyVersionAtCreation > 0);
    }

    [Fact]
    public async Task Ordering_proof_is_immutable_and_closure_must_be_strictly_later()
    {
        await using var f = await new FieldAdvanceSupplementTests(postgres).Ready();
        var root = await PaidRoot(f); var supplement = await CreateSupplement(f, root, 20);
        await using var db = f.CreateContext();
        var order = (await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == supplement.Id)).FamilyVersionAtCreation;
        Assert.Equal((await db.Set<AdvanceFamily>().SingleAsync()).Version, order);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE field_advance_requests SET family_version_at_creation=family_version_at_creation+1 WHERE id={supplement.Id}"));
        Assert.Equal("23514", ex.SqlState);
        var invalidClosure = await Assert.ThrowsAsync<DbUpdateException>(() => Close(f, root.Id, false));
        Assert.Equal("supplement-order-closure-not-later", Assert.IsType<PostgresException>(invalidClosure.InnerException).MessageText);
        db.ChangeTracker.Clear();
        Assert.Equal("Approved", (await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == root.Id)).State);
        Assert.Empty(await db.Set<ClosureRecord>().ToArrayAsync());
        await Close(f, root.Id);
    }

    [Fact]
    public async Task Closure_cannot_claim_an_uncommitted_future_family_version()
    {
        await using var f = await new FieldAdvanceSupplementTests(postgres).Ready(); var root = await PaidRoot(f);
        await CreateSupplement(f, root, 20);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Close(f, root.Id, offset: 100));
        Assert.Equal("supplement-order-closure-commit-mismatch", ex.MessageText);
        await using var db = f.CreateContext(); Assert.Empty(await db.Set<ClosureRecord>().ToArrayAsync());
        Assert.Equal("Approved", (await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == root.Id)).State);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Create_and_close_follow_committed_lock_order(bool createFirst, bool rollbackCreate)
    {
        await using var f = await new FieldAdvanceSupplementTests(postgres).Ready();
        var root = await PaidRoot(f);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Pause()
        {
            entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (rollbackCreate) throw new InvalidOperationException("rollback-create");
        }
        await using var db = f.CreateContext();
        var session = await f.SessionAsync(AuthorId); var access = FieldAdvanceAccessTests.Access(db, session, f.Clock);
        var people = new FinancePersonResolver(db, f.Clock);
        var audit = new FieldAdvanceRequestRaceTests.CallbackAudit(new AuditEventWriter(db, new CorrelationContext(), f.Clock),
            createFirst ? Pause : () => Task.CompletedTask);
        var service = new SupplementService(db, new(db, access, people, f.Clock), L.Draft(f, db, session), audit, f.Clock);
        Task<TPR10.Api.FieldAdvances.FinanceResult<RequestView>> create;
        Task close;
        if (createFirst)
        {
            create = service.CreateAsync(AuthorId, root.Id, Content(20), L.Meta(0), default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); close = Close(f, root.Id);
        }
        else
        {
            close = Close(f, root.Id, pause: Pause);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            create = service.CreateAsync(AuthorId, root.Id, Content(20), L.Meta(0), default);
        }
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var probe = f.CreateContext();
            while (!await probe.Database.SqlQueryRaw<bool>("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock') AS \"Value\"").SingleAsync(deadline.Token))
                await Task.Delay(20, deadline.Token);
        }
        finally { release.TrySetResult(); }
        if (rollbackCreate) await Assert.ThrowsAsync<InvalidOperationException>(() => create);
        else Assert.Equal(createFirst ? 201 : 409, (await create).Status);
        await close.WaitAsync(TimeSpan.FromSeconds(15));
        await using var check = f.CreateContext();
        var supplements = await check.Set<AdvanceRequest>().Where(x => x.RelationKind == "Supplement").ToArrayAsync();
        Assert.Equal(createFirst && !rollbackCreate ? 1 : 0, supplements.Length);
        Assert.Equal(supplements.Length, await check.Set<AdvanceRequestEvent>().CountAsync(x => x.Kind == "SupplementCreated"));
        Assert.Equal(supplements.Length, await check.Set<FinanceCommandReceipt>().CountAsync(x => x.Action == "supplement.create"));
        if (supplements.Length == 1)
            Assert.True(supplements[0].FamilyVersionAtCreation < (await check.Set<ClosureRecord>().SingleAsync()).FamilyVersion);
        Assert.Equal("Closed", (await check.Set<AdvanceRequest>().SingleAsync(x => x.Id == root.Id)).State);
    }

    [Theory]
    [InlineData("Reject")]
    [InlineData("Withdraw")]
    public async Task Terminal_unpaid_supplement_releases_slot_without_fake_settlement(string outcome)
    {
        await using var f = await new FieldAdvanceSupplementTests(postgres).Ready(); var root = await PaidRoot(f);
        var pending = await L.Submit(f, await CreateSupplement(f, root, 20));
        await Close(f, root.Id);
        var result = outcome == "Reject" ? await L.Decide(f, pending, outcome) : await L.Command(f, pending, "WithdrawAsync");
        Assert.Equal(200, result.Status);
        await using var db = f.CreateContext();
        Assert.Null((await db.Set<AdvanceFamily>().SingleAsync()).PendingSupplementId);
        Assert.Empty(await db.Set<SettlementRound>().Where(x => x.RequestId == pending.Id).ToArrayAsync());
        Assert.Empty(await db.Set<ClosureRecord>().Where(x => x.RequestId == pending.Id).ToArrayAsync());
        Assert.Equal(0m, (await db.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == pending.Id)).Paid);
    }

    [Fact]
    public async Task Closed_root_UI_preserves_continue_actions_and_hides_create()
    {
        await using var f = await new FieldAdvanceSupplementTests(postgres).Ready(); var root = await PaidRoot(f);
        var pending = await CreateSupplement(f, root, 20);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var client = await FieldAdvancePolicyHttpTests.Client(factory, f, AuthorId);
        var before = await client.GetFromJsonAsync<JsonElement>($"/api/v1/field-advances/requests/{root.Id}/payment-context");
        Assert.Contains("supplements", before.GetProperty("allowedActions").EnumerateArray().Select(x => x.GetString()));
        await Close(f, root.Id);
        var screen = await client.GetFromJsonAsync<JsonElement>($"/api/v1/field-advances/requests/{pending.Id}/screen");
        Assert.Contains("confirm", screen.GetProperty("actions").EnumerateArray().Select(x => x.GetString()));
        var context = await client.GetFromJsonAsync<JsonElement>($"/api/v1/field-advances/requests/{root.Id}/payment-context");
        Assert.DoesNotContain("supplements", context.GetProperty("allowedActions").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task Continuation_rechecks_rights_cumulative_limit_and_single_pending_slot()
    {
        await using var f = await new FieldAdvanceSupplementTests(postgres).Ready(); var root = await PaidRoot(f);
        var first = await CreateSupplement(f, root, 20); var second = await CreateSupplement(f, root, 30);
        await Close(f, root.Id); var quote = await Quote(f, first);
        Assert.Equal(12000m, quote.Basis.Amount);
        await using var db = f.CreateContext();
        await db.Set<GrantProposal>().Where(x => x.RecipientId == AuthorId && x.ProjectId == ProjectId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "report.read" }));
        Assert.Equal(403, (await Confirm(f, first, quote)).Status);
        Assert.Null((await db.Set<AdvanceFamily>().SingleAsync()).PendingSupplementId);
        await db.Set<GrantProposal>().Where(x => x.RecipientId == AuthorId && x.ProjectId == ProjectId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "request.create" }));
        await db.Set<GrantProposal>().Where(x => x.RecipientId == ReviewerId && x.ProjectId == ProjectId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.ApprovalLimit, 11999m));
        var confirmed = await Confirm(f, first, await Quote(f, first));
        Assert.Equal(200, confirmed.Status); Assert.Equal("Held", confirmed.Value!.State);
        Assert.Equal(409, (await Confirm(f, second, await Quote(f, second))).Status);
        db.ChangeTracker.Clear(); Assert.Equal(first.Id, (await db.Set<AdvanceFamily>().SingleAsync()).PendingSupplementId);
        Assert.Equal(403, (await L.Decide(f, confirmed.Value, "Approve")).Status);
        Assert.Equal(10000m, (await db.Set<AdvanceRequestRevision>().SingleAsync(x => x.Id == root.RevisionId)).ApprovalBasisAmount);
    }

    [Fact]
    public async Task Existing_settlement_executor_commits_strict_closure_order_and_retains_history()
    {
        await using var f = await new FieldAdvanceSupplementTests(postgres).Ready(); var root = await PaidRoot(f);
        var supplement = await CreateSupplement(f, root, 20);
        var checker = Guid.NewGuid(); await Actor(f, checker, "finance.check");
        await using var db = f.CreateContext();
        var r = await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == root.Id);
        var completion = await C.Row<WorkCompletion>(f, db, r);
        completion.CompletedOn = new(2026, 10, 25); completion.ClearingDueOn = completion.CompletedOn; completion.RefundDueOn = completion.CompletedOn;
        db.Add(completion); await db.SaveChangesAsync();
        var declaration = await C.Row<SubmissionDeclaration>(f, db, r); declaration.WorkCompletionId = completion.Id;
        db.Add(declaration); await db.SaveChangesAsync();
        var meta = await FieldAdvanceSettlementAccessTests.Meta(f, root.Id);
        var executor = await FieldAdvanceSettlementAccessTests.Executor(f, db, checker);
        var result = await executor.ExecuteAsync(checker, root.Id, SettlementActions.Close, meta, new('a', 64), async ct =>
        {
            var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == root.Id, ct);
            var closure = new ClosureRecord
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                ProjectId = ProjectId,
                FamilyId = r.FamilyId,
                RequestId = root.Id,
                CommandId = meta.CommandId,
                ProposedBy = AuthorId,
                ProposedByPersonId = AuthorPersonId,
                CreatedAtUtc = f.Clock.GetUtcNow(),
                ReviewedBy = checker,
                ReviewedByPersonId = checker,
                ReviewedAtUtc = f.Clock.GetUtcNow(),
                AcceptedAtUtc = f.Clock.GetUtcNow(),
                State = "Accepted",
                Reason = "ทดสอบสัญญาธุรกรรมเดิม",
                WorkCompletionId = completion.Id,
                DeclarationId = declaration.Id,
                RequestVersion = meta.RequestVersion + 1,
                FamilyVersion = meta.FamilyVersion + 1
            };
            db.Add(closure); request.State = "Closed";
            return new(200, "ok", new(root.Id, closure.Id, "Closed", 0, 0, 1));
        }, default);
        Assert.Equal(200, result.Status);
        Assert.Equal(result.Value!.FamilyVersion, (await db.Set<ClosureRecord>().SingleAsync()).FamilyVersion);
        Assert.Contains(await db.Set<AdvancePaymentParticipation>().ToArrayAsync(), x => x.PersonId == checker && x.Role == "Checker");
        Assert.Contains(await db.Set<AdvanceRequestParticipant>().ToArrayAsync(), x => x.PersonId == ReviewerPersonId && x.Role == "Approver");
        Assert.Equal(200, (await Confirm(f, supplement, await Quote(f, supplement))).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Database_rejects_forged_creation_order_even_with_old_timestamp(bool closed)
    {
        await using var f = await new FieldAdvanceSupplementTests(postgres).Ready(); var root = await PaidRoot(f);
        if (closed) await Close(f, root.Id);
        await using var db = f.CreateContext(); var id = Guid.NewGuid();
        var error = await Record.ExceptionAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == root.Id);
            var family = await db.Set<AdvanceFamily>().SingleAsync(x => x.Id == r.FamilyId);
            db.Add(new AdvanceRequest
            {
                Id = id,
                WorkspaceId = r.WorkspaceId,
                ProjectId = r.ProjectId,
                OwnerPersonId = r.OwnerPersonId,
                FamilyId = r.FamilyId,
                RelationKind = "Supplement",
                SupplementOfRequestId = root.Id,
                FamilyVersionAtCreation = family.Version + 100,
                CreatedBy = AuthorId,
                CreatedAtUtc = f.Clock.GetUtcNow().AddYears(-1)
            });
            await db.SaveChangesAsync(); await tx.CommitAsync();
        });
        var pg = Assert.IsType<PostgresException>(error is DbUpdateException update ? update.InnerException : error);
        Assert.Equal(closed ? "supplement-order-create-denied" : "supplement-order-commit-mismatch", pg.MessageText);
        db.ChangeTracker.Clear();
        Assert.False(await db.Set<AdvanceRequest>().AnyAsync(x => x.Id == id));
        Assert.False(await db.Set<AdvancePaymentAccount>().AnyAsync(x => x.RequestId == id));
    }

    private async Task<(FieldAdvanceFixture Fixture, Guid Root, Guid Supplement)> LegacyDraft(string? fault = null)
    {
        var database = new IdentityDatabase(postgres.ConnectionString); await database.InitializeAsync();
        var f = new FieldAdvanceFixture(database);
        await using var db = f.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync("20261024075718_AddFieldAdvancePayments");
        await FieldAdvanceFixture.SeedDirectoryAsync(db); await f.SeedPoliciesAsync();
        var root = Guid.NewGuid(); var rootRevision = Guid.NewGuid();
        var rootContent = JsonSerializer.Serialize(Content(100));
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO field_advance_requests(id,workspace_id,project_id,owner_person_id,state,version,created_by)
            VALUES ({root},{WorkspaceId},{ProjectId},{AuthorPersonId},'Approved',1,{AuthorId});
            INSERT INTO field_advance_request_revisions(id,workspace_id,project_id,request_id,revision_number,is_current,content_json,content_hash,total,created_by,created_by_person_id,submitted_at_utc,confirmed_by_person_id,company_policy_revision_id,policy_snapshot_json,policy_hash)
            VALUES ({rootRevision},{WorkspaceId},{ProjectId},{root},1,true,{rootContent}::jsonb,repeat('c',64),10000,{AuthorId},{AuthorPersonId},now(),{AuthorPersonId},{CompanyRevisionId},jsonb_build_object(),repeat('d',64));
            UPDATE field_advance_payment_accounts SET approved_revision_id={rootRevision},approved=10000,spendable=10000,paid=2000,ever_paid=true WHERE request_id={root};
            """);
        var id = Guid.NewGuid(); var revision = Guid.NewGuid(); var command = Guid.NewGuid();
        var content = JsonSerializer.Serialize(Content(20));
        var receipt = JsonSerializer.Serialize(new { Status = 201, Value = new { Id = fault == "wrong-receipt" ? Guid.NewGuid() : id, RevisionId = revision } });
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO field_advance_requests(id,workspace_id,project_id,owner_person_id,family_id,relation_kind,supplement_of_request_id,state,version,created_by,created_at_utc)
            SELECT {id},workspace_id,project_id,owner_person_id,family_id,'Supplement',id,'Draft',1,created_by,created_at_utc FROM field_advance_requests WHERE id={root};
            INSERT INTO field_advance_request_revisions(id,workspace_id,project_id,request_id,revision_number,is_current,content_json,content_hash,total,approval_basis_amount,approval_basis_json,created_by,created_by_person_id,created_at_utc)
            SELECT {revision},workspace_id,project_id,{id},1,true,{content}::jsonb,repeat('a',64),2000,2000,jsonb_build_object('Amount',2000,'Items',jsonb_build_array()),created_by,owner_person_id,created_at_utc FROM field_advance_requests WHERE id={id};
            INSERT INTO field_advance_command_receipts(command_id,workspace_id,actor_id,action,payload_sha256,status,result_json)
            VALUES ({command},{WorkspaceId},{AuthorId},'supplement.create',repeat('b',64),201,{receipt}::jsonb);
            UPDATE field_advance_families SET version=version+1 WHERE id=(SELECT family_id FROM field_advance_requests WHERE id={id});
            """);
        if (fault != "missing-event")
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO field_advance_request_events(id,workspace_id,project_id,request_id,revision_id,command_id,actor_id,actor_person_id,kind,data_json,reason,aggregate_version)
                VALUES ({Guid.NewGuid()},{WorkspaceId},{ProjectId},{id},{revision},{command},{AuthorId},{AuthorPersonId},'SupplementCreated',jsonb_build_object('ContentHash',repeat('a',64),'Total',2000),'legacy fixture',1);
                """);
        if (fault == "closed") await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE field_advance_requests SET state='Closed' WHERE id={root}");
        return (f, root, id);
    }

    private static async Task PrepareLegacyPolicy(FieldAdvanceFixture f)
    {
        foreach (var (actor, person) in new[] { (AuthorId, AuthorPersonId), (ReviewerId, ReviewerPersonId) })
        {
            await FieldAdvanceGrantTests.AuthorityAsync(f, actor, person, company: true);
            await using var db = f.CreateContext();
            await db.Set<GrantProposal>().Where(x => x.RecipientId == actor).ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "policy.edit.company", "policy.review" }));
        }
        var content = FieldAdvancePolicyTests.Content() with { Bands = [new(1, "director", 100000m)] };
        var created = await FieldAdvancePolicyTests.Run(f, AuthorId, service => service.CreateDraftAsync(AuthorId,
            new(WorkspaceId, null), null, content, new(Guid.NewGuid(), 1, "กติกาหลังอัปเกรด")
            { Policy = new("Company", CompanyRevisionId, null, null, 0) }, default));
        Assert.Equal(201, created.Status);
        await FieldAdvancePolicyTests.Review(f, await FieldAdvancePolicyTests.Submit(f, created.Value!));
        await FieldAdvanceGrantTests.AuthorityAsync(f, AuthorId, AuthorPersonId);
        await using var context = f.CreateContext();
        await context.Set<GrantProposal>().Where(x => x.RecipientId == AuthorId && x.ProjectId == ProjectId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "request.create" }));
    }

    // Task14 is not implemented: exercise its existing transaction/lock contract with
    // explicit completion/declaration/closure fixtures, never a new auto-close service.
    internal static async Task Close(FieldAdvanceFixture f, Guid id, bool advance = true, Func<Task>? pause = null, long offset = 0)
    {
        await using var db = f.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == id);
        var family = await db.Set<AdvanceFamily>().FromSqlInterpolated($"SELECT * FROM field_advance_families WHERE id={r.FamilyId} FOR UPDATE").SingleAsync();
        var completion = await C.Row<WorkCompletion>(f, db, r);
        completion.ClearingDays = 7; completion.CompletedOn = new(2026, 10, 25); completion.ClearingDueOn = new(2026, 11, 1); completion.RefundDueOn = completion.ClearingDueOn;
        db.Add(completion); await db.SaveChangesAsync();
        var declaration = await C.Row<SubmissionDeclaration>(f, db, r); declaration.WorkCompletionId = completion.Id;
        db.Add(declaration); await db.SaveChangesAsync();
        var closure = await C.Row<ClosureRecord>(f, db, r);
        closure.WorkCompletionId = completion.Id; closure.DeclarationId = declaration.Id;
        closure.FamilyVersion = (advance ? ++family.Version : family.Version) + offset; closure.RequestVersion = ++r.Version;
        closure.State = "Accepted"; closure.AcceptedAtUtc = f.Clock.GetUtcNow(); closure.ReviewedAtUtc = f.Clock.GetUtcNow();
        closure.ReviewedBy = ReviewerId; closure.ReviewedByPersonId = ReviewerPersonId;
        db.Add(closure); r.State = "Closed"; await db.SaveChangesAsync(); if (pause is not null) await pause(); await tx.CommitAsync();
    }
}
