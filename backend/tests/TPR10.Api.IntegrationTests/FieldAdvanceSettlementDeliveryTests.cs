using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Evidence;
using TPR10.Api.FieldAdvances.Settlements;
using TPR10.Api.Identity.Sessions;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementAccessTests;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementCommandTests;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementDeliverySupport;
using static TPR10.Api.IntegrationTests.FieldAdvanceRefundTests;
using static TPR10.Api.IntegrationTests.FieldAdvancePolicyHttpTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceSettlementDeliveryTests(PostgresFixture postgres)
{
    // T17-M1: actual registered work-proof route, actual private file bytes. A removed
    // post-I/O or response-delivery guard must leak the PNG and fail this test.
    [Theory]
    [InlineData("none")]
    [InlineData("io")]
    [InlineData("delivery")]
    public async Task Work_proof_positive_bytes_and_revocation_at_each_delivery_boundary(string revokeAt)
    {
        await using var f = await Ready(postgres); using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        var (request, _) = await RefundReady(f);
        var bytes = FieldAdvanceSettlementEvidenceTests.Image();
        Guid proof;
        await using (var db = f.CreateContext())
        {
            var upload = await (await FieldAdvanceSettlementEvidenceTests.Service(f, db, files)).UploadAsync(AuthorId, request, new MemoryStream(bytes), "image/png", default);
            Assert.Equal(201, upload.Status); proof = upload.Value;
        }
        var proposed = await RefundCall(f, request, "Propose", input: RefundInputFor(12m, proof));
        Assert.Equal(201, proposed.Status); var refund = proposed.Value!.EntityId!.Value;
        Assert.Equal(200, (await RefundCall(f, request, "Confirm", refund)).Status);
        using var keys = new TestKeyMaterial();
        var settings = new Dictionary<string, string?>(keys.Settings) { ["FinanceEvidence:ActiveStorageId"] = files.Options.ActiveStorageId.ToString() };
        foreach (var (id, root) in files.Options.Roots) settings[$"FinanceEvidence:Roots:{id}"] = root;
        IFinanceEvidenceStore store = revokeAt == "io" ? new FieldAdvanceSettlementEvidenceTests.ObservedStore(files.Store,
            () => Revoke(f, ReviewerId), () => { }) : files.Store;
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, settings,
            configureServices: services => services.AddSingleton(store));
        if (revokeAt == "delivery")
        {
            await using var scope = factory.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<RequestSession>().Entity = (await f.SessionAsync(ReviewerId)).Entity;
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            context.Response.Body = new MemoryStream();
            var prepared = await scope.ServiceProvider.GetRequiredService<SettlementWorkService>().EvidenceAsync(ReviewerId, "refunds", request, refund, proof, context, default);
            await Revoke(f, ReviewerId);
            await prepared.ExecuteAsync(context);
            Assert.Equal(403, context.Response.StatusCode);
            Assert.Equal("private, no-store", context.Response.Headers.CacheControl.ToString());
            Assert.DoesNotContain(Convert.ToBase64String(bytes), Convert.ToBase64String(((MemoryStream)context.Response.Body).ToArray()));
            Assert.NotEqual("image/png", context.Response.ContentType);
        }
        else
        {
            using var client = await Client(factory, f, ReviewerId);
            var path = $"/api/v1/field-advances/settlement-work/refunds/{request}/{refund}/evidence/{proof}";
            var response = await client.GetAsync(path); var actual = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(revokeAt == "none" ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore); Assert.True(response.Headers.CacheControl.Private);
            if (revokeAt == "none")
            {
                Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType); Assert.Equal(bytes, actual);
                using var owner = await Client(factory, f, AuthorId);
                Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(path)).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path.Replace(proof.ToString(), Guid.NewGuid().ToString()))).StatusCode);
            }
            else { Assert.NotEqual(bytes, actual); Assert.NotEqual("image/png", response.Content.Headers.ContentType?.MediaType); }
        }
        Assert.Equal(0m, (await Totals(f, request)).ConfirmedRefund);
        await using var verify = f.CreateContext(); Assert.Null((await verify.Set<Refund>().SingleAsync()).AcceptedAtUtc);
    }

    // A lost response must not lead to a second accepted amount/receipt/closure.
    [Fact]
    public async Task Lost_final_review_response_recovers_exact_receipt_without_second_money_effect()
    {
        await using var f = await Ready(postgres);
        var s = await FieldAdvanceSettlementRaceTests.FinalReviewReady(f);
        var command = await FieldAdvanceSettlementReviewTests.ReviewCommand(f, s.Request, s.Round);
        // Execute and deliberately discard the response: this models transport loss AFTER commit.
        Assert.Equal(200, (await FieldAdvanceSettlementReviewTests.Review(f, AliasAccountId, s.Request, s.Round, command: command)).Status);
        var committed = await Ledger(f);
        var replay = await FieldAdvanceSettlementReviewTests.Review(f, AliasAccountId, s.Request, s.Round, command: command);
        Assert.Equal(200, replay.Status); Assert.Equal(committed, await Ledger(f));
        await using var db = f.CreateContext();
        var receipt = Assert.Single(await db.Set<FinanceCommandReceipt>().Where(x => x.CommandId == command.CommandId).ToArrayAsync());
        Assert.Equal(replay, JsonSerializer.Deserialize<TPR10.Api.FieldAdvances.FinanceResult<SettlementCommandResult>>(receipt.ResultJson));
        Assert.Equal(100m, (await Totals(f, s.Request)).ApprovedExpense);
        Assert.Single(await db.Set<SettlementRevision>().Where(x => x.AcceptedAtUtc != null).ToArrayAsync());
        await Revoke(f, AliasAccountId); var revoked = await Ledger(f);
        Assert.Equal(403, (await FieldAdvanceSettlementReviewTests.Review(f, AliasAccountId, s.Request, s.Round, command: command)).Status);
        Assert.Equal(revoked, await Ledger(f)); Assert.Equal(command.CommandId, receipt.CommandId);
    }

    // T11-M1: duplicate RETURN proof across correction and incident recovery, both
    // directions and distinct evidence IDs with equal bytes; not ordinary refund coverage.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Refund_correction_and_incident_recovery_reject_same_return_proof(bool recoveryFirst, bool copied)
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready();
        using var files = new FieldAdvancePaymentRecoveryTests.Files();
        await Grant(f, AuthorId, AuthorPersonId, "request.create");
        await Grant(f, ReviewerId, ReviewerPersonId, "finance.check");
        var incident = await FieldAdvancePaymentIncidentTests.Incident(f, files, true, 8000m, 0m);
        var original = (await FieldAdvancePaymentRecoveryTests.Actual(f, files, incident.Request, [31, 32])).EvidenceIds[0];
        var refund = await RefundCall(f, incident.Request, "Propose", input: RefundInputFor(1000m, original));
        Assert.Equal(201, refund.Status); var refundId = refund.Value!.EntityId!.Value;
        Assert.Equal(200, (await RefundCall(f, incident.Request, "Confirm", refundId)).Status);
        Assert.Equal(200, (await RefundCall(f, incident.Request, "Approve", refundId, actor: FieldAdvancePaymentRecoveryTests.Checker)).Status);
        var a = (await FieldAdvancePaymentRecoveryTests.Actual(f, files, incident.Request, [41, 42])).EvidenceIds[0];
        var b = copied ? (await FieldAdvancePaymentRecoveryTests.Actual(f, files, incident.Request, [41, 42])).EvidenceIds[0] : a;
        await using var db = f.CreateContext();
        Assert.Equal((await db.Set<AdvanceEvidence>().SingleAsync(x => x.Id == a)).Sha256, (await db.Set<AdvanceEvidence>().SingleAsync(x => x.Id == b)).Sha256);
        if (copied) Assert.NotEqual(a, b);
        async Task<TPR10.Api.FieldAdvances.FinanceResult<SettlementCommandResult>> Correction()
        {
            await using var context = f.CreateContext();
            return await new RefundCorrectionService(context, await Access(f, context, FieldAdvancePaymentRecoveryTests.Checker), await Executor(f, context, FieldAdvancePaymentRecoveryTests.Checker), f.Clock, files.Options)
                .ProposeAsync(FieldAdvancePaymentRecoveryTests.Checker, incident.Request, new(refundId, 900m, [b]), await Meta(f, incident.Request), default);
        }
        Task<TPR10.Api.FieldAdvances.FinanceResult<SettlementCommandResult>> Recovery() => FieldAdvancePaymentIncidentTests.Invoke(f, files, incident.Request, "Recovery", incident.Id, amount: 500m, evidence: [a]);
        var first = recoveryFirst ? await Recovery() : await Correction(); Assert.Equal(201, first.Status);
        var before = await Ledger(f);
        var rejected = recoveryFirst ? await Correction() : await Recovery();
        Assert.Equal(409, rejected.Status); Assert.Null(rejected.Value);
        Assert.Equal(recoveryFirst ? "refund-proof-used-in-incident" : "recovery-proof-already-used", rejected.Code);
        Assert.Equal(before, await Ledger(f));
        Assert.Equal(recoveryFirst ? 0 : 1, await db.Set<RefundCorrection>().CountAsync());
        Assert.Equal(recoveryFirst ? 1 : 0, await db.Set<PaymentIncidentRecovery>().CountAsync());
        Assert.Equal(7000m, (await FieldAdvancePaymentIncidentTests.Totals(f, incident.Request)).OwnerOutstanding);
        Assert.Equal(1000m, (await db.Set<Refund>().SingleAsync()).Amount);
    }

    // T4-M1: a duplicate pair/version must fail independently of command uniqueness;
    // Down must roll back DROP INDEX and migration history when multiple pairs share a command.
    [Fact]
    public async Task Duplicate_pair_version_rejection_and_populated_down_are_atomic()
    {
        const string beforeTask4 = "20261031105357_AddSettlementCorrectionDays";
        await using var f = await Ready(postgres); await using var db = f.CreateContext();
        // Isolate the historical Task4 migration: later migrations correctly refuse
        // any populated-request downgrade before Task4's own Down is reached.
        await db.GetService<IMigrator>().MigrateAsync("20261031221353_AllowMultipleDocumentDuplicateCases");
        var request = await new FieldAdvancePaymentFixture(f).SeedApprovedAsync(10000m, default, legacyPaymentAccount: true);
        var family = await db.Set<AdvanceRequest>().Where(x => x.Id == request).Select(x => x.FamilyId).SingleAsync();
        var source = new AdvanceRequest { Id = request, WorkspaceId = WorkspaceId, ProjectId = ProjectId, FamilyId = family };
        var evidence = new AdvanceEvidence
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            FamilyId = family,
            RequestId = request,
            Sha256 = new('a', 64),
            Length = 10,
            ContentType = "image/png",
            State = "Ready",
            CreatedBy = AuthorId,
            CreatedByPersonId = AuthorPersonId
        };
        db.Add(evidence); await db.SaveChangesAsync();
        var docs = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var doc = await Row<ExpenseDocument>(f, db, source);
            doc.EvidenceId = evidence.Id; doc.Issuer = "ร้านจำลอง"; doc.HasNoNumber = true; doc.IssuedOn = new(2026, 10, 30); doc.Amount = 1000m;
            db.Add(doc); await db.SaveChangesAsync(); docs.Add(doc.Id);
        }
        var first = await Row<ExpenseDuplicateCase>(f, db, source);
        first.OtherWorkspaceId = source.WorkspaceId; first.OtherProjectId = source.ProjectId;
        first.OtherFamilyId = source.FamilyId; first.OtherRequestId = source.Id;
        first.DocumentId = docs[0]; first.OtherDocumentId = docs[1]; first.DocumentVersion = first.OtherDocumentVersion = 1;
        db.Add(first); await db.SaveChangesAsync();
        var second = await Row<ExpenseDuplicateCase>(f, db, source);
        second.OtherWorkspaceId = source.WorkspaceId; second.OtherProjectId = source.ProjectId;
        second.OtherFamilyId = source.FamilyId; second.OtherRequestId = source.Id;
        second.CommandId = first.CommandId; second.DocumentId = docs[0]; second.OtherDocumentId = docs[2]; second.DocumentVersion = second.OtherDocumentVersion = 1;
        db.Add(second); await db.SaveChangesAsync();
        var duplicate = await Row<ExpenseDuplicateCase>(f, db, source);
        duplicate.OtherWorkspaceId = source.WorkspaceId; duplicate.OtherProjectId = source.ProjectId;
        duplicate.OtherFamilyId = source.FamilyId; duplicate.OtherRequestId = source.Id;
        duplicate.DocumentId = first.DocumentId; duplicate.OtherDocumentId = first.OtherDocumentId; duplicate.DocumentVersion = duplicate.OtherDocumentVersion = 1;
        Assert.NotEqual(first.CommandId, duplicate.CommandId);
        db.Add(duplicate);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var pg = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal("23505", pg.SqlState); Assert.Equal("IX_field_advance_expense_duplicate_cases_document_id_other_doc~", pg.ConstraintName);
        db.ChangeTracker.Clear();
        var before = await Ledger(f); var history = await db.Database.GetAppliedMigrationsAsync();
        var indexes = await DuplicateIndexes(db);
        var down = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(beforeTask4));
        Assert.Equal("23505", down.SqlState);
        Assert.Equal("IX_field_advance_expense_duplicate_cases_workspace_id_command_~", down.ConstraintName);
        Assert.Equal(before, await Ledger(f)); Assert.Equal(history, await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(indexes, await DuplicateIndexes(db));
        Assert.Equal(2, await db.Set<ExpenseDuplicateCase>().CountAsync(x => x.CommandId == first.CommandId));
        // A new version is allowed: unique rejection above was not a blanket pair ban.
        var version = await Row<ExpenseDuplicateCase>(f, db, source);
        version.OtherWorkspaceId = source.WorkspaceId; version.OtherProjectId = source.ProjectId;
        version.OtherFamilyId = source.FamilyId; version.OtherRequestId = source.Id;
        version.DocumentId = first.DocumentId; version.OtherDocumentId = first.OtherDocumentId; version.DocumentVersion = 2; version.OtherDocumentVersion = 1;
        db.Add(version); await db.SaveChangesAsync(); Assert.Equal(3, await db.Set<ExpenseDuplicateCase>().CountAsync());
    }

    private static async Task<string[]> DuplicateIndexes(TPR10.Api.Data.Tpr10DbContext db) => await db.Database.SqlQueryRaw<string>(
        "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE schemaname='public' AND tablename='field_advance_expense_duplicate_cases' ORDER BY indexname").ToArrayAsync();

    [Fact]
    public async Task SevenC_backup_clone_migrates_twice_and_idempotent_sql_twice_without_money_grants_or_policy()
    {
        const string sevenC = "20261024075718_AddFieldAdvancePayments";
        await using var source = await FieldAdvancePaymentFixture.CreateAsync(postgres);
        await using var original = source.CreateContext(); await original.GetService<IMigrator>().MigrateAsync(sevenC);
        var request = await source.SeedApprovedAsync(10000m, default, legacyPaymentAccount: true);
        await original.Database.ExecuteSqlInterpolatedAsync($"UPDATE field_advance_payment_accounts SET paid=2500, reserved=500, ever_paid=true WHERE request_id={request}");
        var oldTables = await original.Database.SqlQueryRaw<string>("SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname='public'").ToArrayAsync();
        var grants = await original.Set<GrantAssignment>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync();
        await using var target = new IdentityDatabase(postgres.ConnectionString); await target.InitializeAsync();
        await postgres.BackupRestoreAsync(original.Database.GetConnectionString()!, target.ConnectionString);
        var copy = new FieldAdvanceFixture(target); await using var migrated = target.CreateContext();
        await migrated.Database.MigrateAsync(); var once = await Ledger(copy);
        await migrated.Database.MigrateAsync(); Assert.Equal(once, await Ledger(copy));
        var script = migrated.GetService<IMigrator>().GenerateScript(sevenC, options: MigrationsSqlGenerationOptions.Idempotent);
        await using (var connection = new NpgsqlConnection(target.ConnectionString))
        {
            await connection.OpenAsync(); await using var command = new NpgsqlCommand(script, connection);
            for (var i = 0; i < 2; i++) { await command.ExecuteNonQueryAsync(); Assert.Equal(once, await Ledger(copy)); }
        }
        var account = await migrated.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == request);
        Assert.Equal(10000m, account.Approved); Assert.Equal(10000m, account.Spendable);
        Assert.Equal(2500m, account.Paid); Assert.Equal(500m, account.Reserved); Assert.True(account.EverPaid);
        Assert.Equal(2500m, account.EffectiveUsage); Assert.Equal(0m, account.ActivatedShortfall); Assert.Equal(0m, account.IncidentReservationUsage);
        Assert.Equal(JsonSerializer.Serialize(grants), JsonSerializer.Serialize(await migrated.Set<GrantAssignment>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync()));
        Assert.Empty(await migrated.Set<AdvancePaymentResult>().ToArrayAsync());
        Assert.Empty(await migrated.Set<Refund>().ToArrayAsync()); Assert.Empty(await migrated.Set<ClosureRecord>().ToArrayAsync());
        Assert.Equal(0, await migrated.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM field_advance_policy_revisions WHERE correction_days IS NOT NULL").SingleAsync());
        Assert.Equal(migrated.Database.GetMigrations(), await migrated.Database.GetAppliedMigrationsAsync());
        var newTables = await migrated.Database.SqlQueryRaw<string>("SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname='public'").ToArrayAsync();
        foreach (var table in newTables.Except(oldTables))
        {
            var sql = "SELECT count(*)::int AS \"Value\" FROM \"" + table.Replace("\"", "\"\"") + "\"";
            Assert.Equal(0, await migrated.Database.SqlQueryRaw<int>(sql).SingleAsync());
        }
        Assert.False(migrated.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Backup_restore_preserves_pending_refund_closed_case_and_revoked_rights()
    {
        await using var f = await Ready(postgres);
        var closed = await RefundReady(f);
        var refund = await RefundCall(f, closed.Request, "Propose", input: RefundInputFor(2000m, closed.Evidence));
        Assert.Equal(201, refund.Status); var refundId = refund.Value!.EntityId!.Value;
        Assert.Equal(200, (await RefundCall(f, closed.Request, "Confirm", refundId)).Status);
        Assert.Equal(200, (await RefundCall(f, closed.Request, "Approve", refundId, actor: ReviewerId)).Status);
        await Eligible(f, closed.Request);
        await using (var db = f.CreateContext())
        {
            var account = await db.Set<AdvancePaymentAccount>().SingleAsync(); account.Spendable = account.Paid = 2000m; account.Approved = 2000m; account.EverPaid = true;
            (await db.Set<AdvanceInstallment>().SingleAsync()).State = "Paid";
            (await db.Set<AdvancePaymentAttempt>().SingleAsync()).State = "Matched"; await db.SaveChangesAsync();
            Assert.True(await Closure(f, db).TryCloseAsync(closed.Request, default));
            using var files = new FieldAdvanceSettlementEvidenceTests.Files();
            var correction = await new RefundCorrectionService(db, await Access(f, db, ReviewerId), await Executor(f, db, ReviewerId), f.Clock, files.Options)
                .ProposeAsync(ReviewerId, closed.Request, new(refundId, 1500m, [closed.Evidence]), await Meta(f, closed.Request), default);
            Assert.Equal(201, correction.Status);
        }
        var pending = await RefundReady(f);
        var meta = await Meta(f, pending.Request);
        var proposed = await RefundCall(f, pending.Request, "Propose", input: RefundInputFor(500m, pending.Evidence), command: meta);
        Assert.Equal(201, proposed.Status);
        Assert.Equal(200, (await RefundCall(f, pending.Request, "Confirm", proposed.Value!.EntityId)).Status);
        await Revoke(f, AuthorId); var before = await Ledger(f);
        await using var restored = new IdentityDatabase(postgres.ConnectionString); await restored.InitializeAsync();
        await postgres.BackupRestoreAsync(f.Database.ConnectionString, restored.ConnectionString);
        var copy = new FieldAdvanceFixture(restored) { Clock = f.Clock };
        Assert.Equal(before, await Ledger(copy));
        Assert.Equal(403, (await RefundCall(copy, pending.Request, "Propose", input: RefundInputFor(500m, pending.Evidence), command: meta)).Status);
        Assert.Equal(before, await Ledger(copy));
        await using var verify = copy.CreateContext();
        Assert.Equal("Closed", (await verify.Set<AdvanceRequest>().SingleAsync(x => x.Id == closed.Request)).State);
        Assert.Single(await verify.Set<ClosureRecord>().ToArrayAsync()); Assert.Single(await verify.Set<SettlementAdjustmentCase>().ToArrayAsync());
        Assert.Null((await verify.Set<Refund>().SingleAsync(x => x.Id == proposed.Value!.EntityId)).AcceptedAtUtc);
        Assert.Equal(500m, (await verify.Set<Refund>().SingleAsync(x => x.Id == proposed.Value!.EntityId)).Amount);
        Assert.Equal("Pending", (await verify.Set<Refund>().SingleAsync(x => x.Id == proposed.Value!.EntityId)).State);
        Assert.True(await verify.Set<GrantAssignment>().AnyAsync(x => x.RevokedAtUtc != null));
    }

    [Fact]
    public async Task Local_evidence_move_checks_digest_and_retains_financial_references()
    {
        await using var f = await Ready(postgres); using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        var s = await RefundReady(f); var bytes = FieldAdvanceSettlementEvidenceTests.Image();
        await using var db = f.CreateContext(); var evidence = await FieldAdvanceSettlementEvidenceTests.Service(f, db, files);
        var upload = await evidence.UploadAsync(AuthorId, s.Request, new MemoryStream(bytes), "image/png", default);
        Assert.Equal(201, upload.Status); var id = upload.Value;
        var proposal = await RefundCall(f, s.Request, "Propose", input: RefundInputFor(500m, id)); Assert.Equal(201, proposal.Status);
        var before = await Rows<Refund>(f); var receipts = await Rows<FinanceCommandReceipt>(f);
        var target = Guid.NewGuid(); files.Options.Roots[target] = Path.Combine(files.Options.Roots.Values.Single(), "moved");
        Directory.CreateDirectory(files.Options.Roots[target]);
        Assert.True(await new FinanceEvidenceMigration(db, files.Store, f.Clock).CopyAndSwitchAsync(id, target, default));
        var read = await evidence.ReadAsync(AuthorId, id, default); Assert.Equal(200, read.Status); Assert.Equal(bytes, read.Value);
        var proof = await db.Set<AdvanceEvidence>().AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), proof.Sha256); Assert.Equal(bytes.Length, proof.Length);
        var locations = await db.Set<AdvanceEvidenceLocation>().Where(x => x.EvidenceId == id).ToArrayAsync();
        Assert.Equal(2, locations.Length); var current = Assert.Single(locations, x => x.IsCurrent);
        Assert.True(EvidenceLocator.TryParse(current.StorageKey, out var locator)); Assert.Equal(target, locator.StorageId);
        Assert.Equal(before, await Rows<Refund>(f)); Assert.Equal(receipts, await Rows<FinanceCommandReceipt>(f));
        // Corruption in the destination is detected, never silently delivered.
        var corrupted = bytes.ToArray(); corrupted[^1] ^= 1;
        await File.WriteAllBytesAsync(Path.Combine(files.Options.Roots[target], locator.Key), corrupted);
        var corrupt = await evidence.ReadAsync(AuthorId, id, default); Assert.Equal(409, corrupt.Status); Assert.Null(corrupt.Value);
    }
}
