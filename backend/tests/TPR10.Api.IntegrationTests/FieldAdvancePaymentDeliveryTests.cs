using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Evidence;
using TPR10.Api.FieldAdvances.Payments;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvancePaymentRaceTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvancePaymentDeliveryTests(PostgresFixture postgres)
{
    // หาก commit บันทึกได้เพียงบางตาราง snapshot จะต่าง และ replay จะใช้วงเงินซ้ำ
    [Theory]
    [InlineData("Start")]
    [InlineData("Record")]
    public async Task Deferred_commit_failure_then_same_command_replay_has_one_effect(string operation)
    {
        await using var f = await Ready(postgres); var (request, id) = await Reviewed(f); using var files = new Files();
        if (operation == "Record") Value(await Call(f, request, id, "Start", files));
        var input = await Actual(f, files, request); var meta = await Meta(f, request, id); var before = await Snapshot(f);
        await using (var db = f.CreateContext()) await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION task15_fail() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'task15-commit'; END $$;
            CREATE CONSTRAINT TRIGGER task15_fail AFTER INSERT ON field_advance_payment_events
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION task15_fail();
            """);
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Call(f, request, id, operation, files, input, meta));
        Assert.Equal("P0001", error.SqlState); Assert.Equal("task15-commit", error.MessageText);
        Assert.Equal(before, await Snapshot(f));
        await using (var db = f.CreateContext()) await db.Database.ExecuteSqlRawAsync("DROP FUNCTION task15_fail() CASCADE");
        var result = await Call(f, request, id, operation, files, input, meta); Value(result);
        var after = await Snapshot(f);
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(await Call(f, request, id, operation, files, input, meta)));
        Assert.Equal(after, await Snapshot(f));
        await using var verify = f.CreateContext(); var account = await verify.Set<AdvancePaymentAccount>().SingleAsync();
        Assert.Equal(operation == "Record" ? 3000m : 0m, account.Paid);
        Assert.Equal(operation == "Record" ? 0m : 3000m, account.Reserved);
        Assert.Single(await verify.Set<AdvancePaymentAttempt>().ToArrayAsync());
        Assert.Equal(operation == "Record" ? 1 : 0, await verify.Set<AdvancePaymentResult>().CountAsync());
        Assert.Equal(operation == "Record" ? 1 : 0, await verify.Set<AdvanceReservationEntry>().CountAsync(x => x.Action == "Consume"));
        Assert.Single(await verify.Set<FinanceCommandReceipt>().Where(x => x.CommandId == meta.CommandId).ToArrayAsync());
    }

    // restore ต้องคง receipt/lock และ grant ที่ถอนแล้ว ไม่ทำให้การเริ่มจ่ายครั้งเก่ากลายเป็นครั้งใหม่
    [Fact]
    public async Task Restored_open_attempt_preserves_command_and_revoked_authority()
    {
        await using var f = await Ready(postgres); var (request, id) = await Reviewed(f); using var files = new Files();
        var meta = await Meta(f, request, id); Value(await Call(f, request, id, "Start", files, meta: meta));
        await Revoke(f); var before = await Snapshot(f);
        await using var restored = new IdentityDatabase(postgres.ConnectionString); await restored.InitializeAsync();
        await postgres.BackupRestoreAsync(f.Database.ConnectionString, restored.ConnectionString);
        var copy = new FieldAdvanceFixture(restored) { Clock = f.Clock };
        Assert.Equal(before, await Snapshot(copy));
        Assert.Equal(403, (await Call(copy, request, id, "Start", files, meta: meta)).Status);
        await Unpaid(copy);
        await using var db = copy.CreateContext();
        Assert.Single(await db.Set<AdvancePaymentAttempt>().Where(x => x.State == "Open").ToArrayAsync());
        Assert.Single(await db.Set<FinanceCommandReceipt>().Where(x => x.CommandId == meta.CommandId).ToArrayAsync());
    }

    // สำเนา 7B ไม่ใช่ส่วนเพิ่ม: migrate ซ้ำต้องไม่สร้างเงิน grant หรือความสัมพันธ์ชุดใหม่ซ้ำ
    [Fact]
    public async Task Restored_7B_clone_migrates_twice_without_minting_money_or_merging_copy_family()
    {
        await using var source = await FieldAdvanceRequestFixture.CreateAsync(postgres);
        await using (var db = source.CreateContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(FieldAdvancePaymentSchemaTests.PreviousMigration);
            await db.Database.ExecuteSqlAsync($$"""
                INSERT INTO field_advance_requests(id,workspace_id,project_id,owner_person_id,created_by,state)
                VALUES ('77000000-0000-0000-0000-000000000001',{{WorkspaceId}},{{ProjectId}},{{AuthorPersonId}},{{AuthorId}},'Approved');
                INSERT INTO field_advance_requests(id,workspace_id,project_id,owner_person_id,created_by,source_request_id,state)
                VALUES ('77000000-0000-0000-0000-000000000002',{{WorkspaceId}},{{ProjectId}},{{AuthorPersonId}},{{AuthorId}},'77000000-0000-0000-0000-000000000001','Draft');
                INSERT INTO field_advance_request_revisions(id,workspace_id,project_id,request_id,revision_number,is_current,content_json,content_hash,total,created_by,created_by_person_id)
                SELECT gen_random_uuid(),workspace_id,project_id,id,1,true,'{}',repeat('a',64),10000,created_by,owner_person_id FROM field_advance_requests;
                """);
        }
        await using var target = new IdentityDatabase(postgres.ConnectionString); await target.InitializeAsync();
        await using var sourceContext = source.CreateContext();
        await postgres.BackupRestoreAsync(sourceContext.Database.GetConnectionString()!, target.ConnectionString);
        await using var migrated = target.CreateContext();
        var grants = await migrated.Set<GrantAssignment>().CountAsync();
        await migrated.Database.MigrateAsync();
        var copy = new FieldAdvanceFixture(target); var once = await Snapshot(copy);
        await migrated.Database.MigrateAsync(); Assert.Equal(once, await Snapshot(copy));
        var script = migrated.GetService<IMigrator>().GenerateScript(FieldAdvancePaymentSchemaTests.PreviousMigration,
            options: MigrationsSqlGenerationOptions.Idempotent);
        await using (var connection = new Npgsql.NpgsqlConnection(target.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new Npgsql.NpgsqlCommand(script, connection);
            await command.ExecuteNonQueryAsync(); await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(once, await Snapshot(copy));
        Assert.Equal(migrated.Database.GetMigrations(), await migrated.Database.GetAppliedMigrationsAsync());
        var rows = await migrated.Set<AdvanceRequest>().OrderBy(x => x.Id).ToArrayAsync();
        Assert.Equal(2, rows.Length); Assert.Equal(rows[0].Id, rows[1].SourceRequestId);
        Assert.NotEqual(rows[0].FamilyId, rows[1].FamilyId); Assert.All(rows, x => { Assert.Equal("Normal", x.RelationKind); Assert.Null(x.SupplementOfRequestId); });
        var accounts = await migrated.Set<AdvancePaymentAccount>().ToArrayAsync(); Assert.Equal(2, accounts.Length);
        Assert.Equal(10000m, accounts.Single(x => x.RequestId == rows[0].Id).Spendable);
        Assert.Equal(0m, accounts.Single(x => x.RequestId == rows[1].Id).Spendable);
        Assert.All(accounts, x => { Assert.Equal(0m, x.Paid); Assert.Equal(0m, x.Reserved); Assert.False(x.EverPaid); });
        Assert.All(await migrated.Set<AdvanceRequestRevision>().ToArrayAsync(), x => Assert.Equal(10000m, x.ApprovalBasisAmount));
        Assert.Equal(grants, await migrated.Set<GrantAssignment>().CountAsync());
        Assert.Empty(await migrated.Set<AdvancePaymentAttempt>().ToArrayAsync());
        Assert.Empty(await migrated.Set<AdvancePaymentResult>().ToArrayAsync());
        Assert.Empty(await migrated.Set<AdvanceReservationEntry>().ToArrayAsync());
        Assert.False(migrated.Database.HasPendingModelChanges());
    }

    // สลับตำแหน่งระหว่างอ่านต้องปฏิเสธ bytes เก่า แล้วอ่านใหม่จากตำแหน่งที่ตรวจ digest แล้ว
    [Fact]
    public async Task Location_switch_during_download_returns_no_stale_bytes_and_preserves_paid_result()
    {
        await using var f = await Ready(postgres); var (request, id) = await Reviewed(f); using var files = new Files();
        Value(await Call(f, request, id, "Start", files)); var input = await Actual(f, files, request);
        Value(await Call(f, request, id, "Record", files, input));
        var target = Guid.NewGuid(); var targetRoot = Path.Combine(files.Options.Roots.Values.Single(), "target");
        Directory.CreateDirectory(targetRoot); files.Options.Roots[target] = targetRoot;
        await using var reader = f.CreateContext(); var session = await f.SessionAsync(RecipientId);
        var access = FieldAdvanceAccessTests.Access(reader, session, f.Clock); var people = new FinancePersonResolver(reader, f.Clock);
        var pa = new PaymentAccess(reader, access, people, new(reader, access, people, f.Clock), f.Clock);
        var barrier = new ReadBarrier(files.Store);
        var service = new FinanceEvidenceService(reader, pa, barrier, new RejectPdf(), files.Options, f.Clock);
        var reading = service.ReadAsync(RecipientId, input.EvidenceIds[0], default);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            await using var mover = f.CreateContext();
            Assert.True(await new FinanceEvidenceMigration(mover, files.Store, f.Clock).CopyAndSwitchAsync(input.EvidenceIds[0], target, default));
        }
        finally { barrier.Release.TrySetResult(); }
        var stale = await reading; Assert.Equal(409, stale.Status); Assert.Null(stale.Value);
        var fresh = await service.ReadAsync(RecipientId, input.EvidenceIds[0], default);
        Assert.Equal(200, fresh.Status); Assert.Equal(new byte[] { 1, 2, 3 }, fresh.Value);
        await using var verify = f.CreateContext();
        var result = Assert.Single(await verify.Set<AdvancePaymentResult>().ToArrayAsync());
        Assert.Equal(3000m, result.Amount); Assert.Equal(input.EvidenceIds, JsonSerializer.Deserialize<Guid[]>(result.EvidenceIdsJson));
        Assert.Equal(2, await verify.Set<AdvanceEvidenceLocation>().CountAsync());
        Assert.Single(await verify.Set<AdvanceEvidenceLocation>().Where(x => x.IsCurrent).ToArrayAsync());
        Assert.Equal(409, (await Call(f, request, id, "Start", files)).Status);
    }

    private sealed class ReadBarrier(IFinanceEvidenceStore inner) : IFinanceEvidenceStore
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<EvidenceTicket> WriteImmutableAsync(Guid storage, string key, Stream stream, CancellationToken ct) => inner.WriteImmutableAsync(storage, key, stream, ct);
        public IEnumerable<EvidenceLocator> Enumerate() => inner.Enumerate();
        public async Task<byte[]> ReadVerifiedAsync(Guid storage, string key, string hash, long length, CancellationToken ct)
        {
            var bytes = await inner.ReadVerifiedAsync(storage, key, hash, length, ct);
            Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), ct); return bytes;
        }
    }
}
