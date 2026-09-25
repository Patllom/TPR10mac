using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using TPR10.Api.Data;

// All dynamic SQL fragments below are fixed test literals, not request input.
#pragma warning disable EF1002

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class EvidenceSchemaTests(PostgresFixture postgres)
{
    private const string Previous = "20260924193737_ProtectAttendanceDirectoryHistory";
    private const string ObjectInsert = """
        INSERT INTO evidence_objects(id,operation_id,owner_id,membership_id,workspace_id,department_id,snapshot_at_utc,occurred_at_utc,action,storage_id,storage_version,object_key,state,version)
        VALUES ('00000000-0000-0000-0000-000000000201','00000000-0000-0000-0000-000000000301','00000000-0000-0000-0000-000000000031','00000000-0000-0000-0000-000000000041','00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000011','2026-09-25T01:00:00Z','2026-09-25T01:00:00Z','CheckIn','00000000-0000-0000-0000-000000000101',1,'objects/00/00000000000000000000000000000201','Reserved',1)
        """;
    private const string Prepare = """
        UPDATE evidence_objects SET state='Prepared',sha256=repeat('a',64),length=1024,width=800,height=600,
            thumbnail_sha256=repeat('b',64),thumbnail_length=512,thumbnail_width=640,thumbnail_height=480,version=2;
        INSERT INTO evidence_locations(id,evidence_id,storage_id,object_key,variant,sha256,length,state,verified_at_utc)
            VALUES ('00000000-0000-0000-0000-000000000401','00000000-0000-0000-0000-000000000201','00000000-0000-0000-0000-000000000101','objects/00/00000000000000000000000000000201/full.jpg','full',repeat('a',64),1024,'Verified',now()),
                   ('00000000-0000-0000-0000-000000000402','00000000-0000-0000-0000-000000000201','00000000-0000-0000-0000-000000000101','objects/00/00000000000000000000000000000201/thumbnail.jpg','thumbnail',repeat('b',64),512,'Verified',now());
        """;
    private const string Publish = """
        UPDATE evidence_locations SET state='Active',version=2;
        INSERT INTO evidence_bindings(evidence_id,event_id,published_at_utc) VALUES ('00000000-0000-0000-0000-000000000201','00000000-0000-0000-0000-000000000501',now());
        UPDATE evidence_objects SET state='Published',version=3;
        """;
    [Fact]
    public async Task Migration_creates_evidence_storage_and_manifest_tables_matching_model()
    {
        await using var database = new IdentityDatabase(postgres.ConnectionString);
        await database.InitializeAsync();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var tables = await db.Database.SqlQueryRaw<string>("SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname='public'").ToArrayAsync();
        foreach (var name in new[] { "evidence_objects", "evidence_locations", "evidence_bindings", "attendance_storage_locations", "attendance_storage_write_target", "evidence_migration_jobs", "evidence_migration_items" })
            Assert.Contains(name, tables);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000031", "00000000-0000-0000-0000-000000000032")]
    [InlineData("00000000-0000-0000-0000-000000000041", "00000000-0000-0000-0000-000000000099")]
    [InlineData("00000000-0000-0000-0000-000000000001", "00000000-0000-0000-0000-000000000002")]
    [InlineData("00000000-0000-0000-0000-000000000011", "00000000-0000-0000-0000-000000000012")]
    public async Task Snapshot_cannot_reference_another_owner_or_unit(string original, string forged)
    {
        await using var database = await CreateAsync();
        await using var db = database.CreateContext();
        await RejectAsync(db, ObjectInsert.Replace(original, forged), "23503");
        Assert.Equal(0, await CountAsync(db, "evidence_objects"));
    }

    [Theory]
    [InlineData("'2026-09-25T01:00:00Z'", "'2026-09-24T00:00:00Z'", "Evidence snapshot outside membership period")]
    [InlineData("'CheckIn'", "'Unknown'", "ck_evidence_action")]
    [InlineData("'Reserved'", "'Anything'", "Evidence must start reserved")]
    [InlineData("'Reserved',1)", "'Reserved',0)", "ck_evidence_objects_version")]
    [InlineData("',1,'objects/", "',0,'objects/", "ck_evidence_pin")]
    [InlineData("objects/00/00000000000000000000000000000201", "../escape", "ck_evidence_key")]
    public async Task Invalid_reservation_metadata_is_rejected_on_insert(string original, string invalid, string expectedGuard)
    {
        await using var database = await CreateAsync();
        await using var db = database.CreateContext();
        var sql = ObjectInsert.Replace(original, invalid);
        Assert.NotEqual(ObjectInsert, sql);
        var error = await RejectAsync(db, sql, "23514");
        Assert.Equal(expectedGuard, error.ConstraintName ?? error.MessageText);
        Assert.Equal(0, await CountAsync(db, "evidence_objects"));
    }

    [Theory]
    [InlineData("sha256=repeat('x',64)")]
    [InlineData("length=0")]
    [InlineData("length=10485761")]
    [InlineData("width=0")]
    [InlineData("height=2147483647")]
    [InlineData("thumbnail_sha256=NULL")]
    public async Task Prepared_metadata_requires_valid_bounded_full_and_thumbnail(string change)
    {
        await using var database = await CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.ExecuteSqlRawAsync(ObjectInsert);
        var sql = "UPDATE evidence_objects SET state='Prepared',sha256=repeat('a',64),length=1024,width=800,height=600,thumbnail_sha256=repeat('b',64),thumbnail_length=512,thumbnail_width=640,thumbnail_height=480,version=2";
        var field = change.Split('=')[0];
        sql = System.Text.RegularExpressions.Regex.Replace(sql, $@"(?<![a-z_]){field}=(?:repeat\('[ab]',64\)|\d+)(?=,|$)", change);
        await RejectAsync(db, sql, "23514");
    }

    [Theory]
    [InlineData("width=801")]
    [InlineData("thumbnail_width=641")]
    [InlineData("sha256=repeat('c',64)")]
    [InlineData("thumbnail_sha256=repeat('c',64)")]
    [InlineData("length=1025")]
    [InlineData("sha256=NULL,length=NULL,width=NULL,height=NULL,thumbnail_sha256=NULL,thumbnail_length=NULL,thumbnail_width=NULL,thumbnail_height=NULL")]
    public async Task Prepared_metadata_stays_immutable_after_orphan_and_copy_quarantine(string change)
    {
        await using var database = await CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.ExecuteSqlRawAsync(ObjectInsert);
        await db.Database.ExecuteSqlRawAsync(Prepare);
        await db.Database.ExecuteSqlRawAsync("UPDATE evidence_objects SET state='Orphan',version=3; UPDATE evidence_locations SET state='Quarantined',version=2");
        var before = await RowsAsync(db, "evidence_objects");
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync($"UPDATE evidence_objects SET {change},version=4"));
        Assert.Equal("23514", error.SqlState);
        Assert.Equal("Prepared evidence bytes are immutable", error.MessageText);
        Assert.Equal(before, await RowsAsync(db, "evidence_objects"));
    }

    [Theory]
    [InlineData("owner_id='00000000-0000-0000-0000-000000000032'", "Evidence identity and stamp are immutable")]
    [InlineData("sha256=repeat('c',64)", "Prepared evidence bytes are immutable")]
    [InlineData("thumbnail_sha256=repeat('c',64)", "Prepared evidence bytes are immutable")]
    [InlineData("occurred_at_utc=occurred_at_utc+interval '1 second'", "Evidence identity and stamp are immutable")]
    [InlineData("state='Prepared'", "Invalid evidence transition")]
    [InlineData("action='CheckOut'", "Evidence identity and stamp are immutable")]
    public async Task Published_evidence_cannot_be_rewritten(string change, string expectedGuard)
    {
        await using var database = await CreateAsync();
        await using var db = database.CreateContext();
        await PublishedAsync(db);
        var before = await RowsAsync(db, "evidence_objects");
        var error = await RejectAsync(db, $"UPDATE evidence_objects SET {change},version=4", "23514");
        Assert.Equal(expectedGuard, error.MessageText);
        Assert.Equal(before, await RowsAsync(db, "evidence_objects"));
    }

    [Theory]
    [InlineData("evidence_objects")]
    [InlineData("evidence_locations")]
    [InlineData("evidence_bindings")]
    public async Task Evidence_history_cannot_be_deleted_or_truncated(string table)
    {
        await using var database = await CreateAsync();
        await using var db = database.CreateContext();
        await PublishedAsync(db);
        var before = await RowsAsync(db, table);
        await RejectAsync(db, $"DELETE FROM {table}", "23514");
        await RejectAsync(db, $"TRUNCATE {table} CASCADE", "23514");
        Assert.Equal(before, await RowsAsync(db, table));
    }

    [Theory]
    [InlineData("UPDATE evidence_locations SET state='Verified',version=version+1 WHERE variant='thumbnail'")]
    [InlineData("UPDATE evidence_locations SET sha256=repeat('c',64),version=version+1 WHERE variant='full'")]
    [InlineData("UPDATE evidence_locations SET object_key='objects/00/00000000000000000000000000000999/full.jpg',version=version+1 WHERE variant='full'")]
    [InlineData("UPDATE evidence_bindings SET event_id=gen_random_uuid()")]
    public async Task Published_references_and_verified_bytes_cannot_be_changed(string sql)
    {
        await using var database = await CreateAsync();
        await using var db = database.CreateContext();
        await PublishedAsync(db);
        await RejectAsync(db, sql, "23514");
    }

    [Theory]
    [InlineData("UPDATE evidence_objects SET state='Published',version=3")]
    [InlineData("UPDATE evidence_locations SET state='Active',version=2; UPDATE evidence_objects SET state='Published',version=3")]
    [InlineData("INSERT INTO evidence_bindings(evidence_id,event_id,published_at_utc) SELECT id,gen_random_uuid(),now() FROM evidence_objects")]
    public async Task Publication_requires_binding_and_both_verified_active_variants_at_commit(string sql)
    {
        await using var database = await CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.ExecuteSqlRawAsync(ObjectInsert);
        await db.Database.ExecuteSqlRawAsync(Prepare);
        await RejectAsync(db, sql, "23514");
        Assert.Equal(0, await CountAsync(db, "evidence_bindings"));
    }

    [Fact]
    public async Task Deferred_publication_commits_together_and_copy_cutover_preserves_reference()
    {
        await using var database = await CreateAsync();
        await using var db = database.CreateContext();
        await PublishedAsync(db);
        var before = await RowsAsync(db, "evidence_objects");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO evidence_locations(id,evidence_id,storage_id,object_key,variant,sha256,length,state,verified_at_utc)
                SELECT gen_random_uuid(),evidence_id,'00000000-0000-0000-0000-000000000102',object_key,variant,sha256,length,'Verified',now() FROM evidence_locations;
            UPDATE evidence_locations SET state='Fallback',version=version+1 WHERE storage_id='00000000-0000-0000-0000-000000000101';
            UPDATE evidence_locations SET state='Active',version=version+1 WHERE storage_id='00000000-0000-0000-0000-000000000102';
            """);
        Assert.Equal(before, await RowsAsync(db, "evidence_objects"));
        Assert.Equal(4, await CountAsync(db, "evidence_locations"));
        Assert.Equal(1, await CountAsync(db, "evidence_bindings"));
    }

    [Fact]
    public async Task Duplicate_operation_event_binding_and_copy_are_rejected()
    {
        await using var database = await CreateAsync();
        await using var db = database.CreateContext();
        await PublishedAsync(db);
        var second = ObjectInsert.Replace("00000000-0000-0000-0000-000000000201", "00000000-0000-0000-0000-000000000202").Replace("00000000000000000000000000000201", "00000000000000000000000000000202");
        await RejectAsync(db, second, "23505");
        await RejectAsync(db, "INSERT INTO evidence_bindings(evidence_id,event_id,published_at_utc) SELECT evidence_id,gen_random_uuid(),now() FROM evidence_bindings", "23505");
        await db.Database.ExecuteSqlRawAsync(second.Replace("00000000-0000-0000-0000-000000000301", "00000000-0000-0000-0000-000000000302"));
        await RejectAsync(db, "INSERT INTO evidence_bindings(evidence_id,event_id,published_at_utc) SELECT '00000000-0000-0000-0000-000000000202',event_id,now() FROM evidence_bindings", "23505");
        await RejectAsync(db, "INSERT INTO evidence_locations(id,evidence_id,storage_id,object_key,variant,sha256,length,state,verified_at_utc) SELECT gen_random_uuid(),evidence_id,storage_id,object_key,variant,sha256,length,'Verified',now() FROM evidence_locations", "23505");
    }

    [Theory]
    [InlineData("alias='different'")]
    [InlineData("kind='nas-mounted-folder'")]
    [InlineData("config_fingerprint=repeat('f',64)")]
    public async Task Registered_storage_identity_is_immutable(string change)
    {
        await using var database = await CreateAsync();
        await using var db = database.CreateContext();
        var error = await RejectAsync(db, $"UPDATE attendance_storage_locations SET {change},version=2 WHERE id='00000000-0000-0000-0000-000000000101'", "23514");
        Assert.Equal("Registered storage identity cannot be repointed", error.MessageText);
    }

    [Fact]
    public async Task Valid_versioned_operational_updates_do_not_rewrite_evidence()
    {
        await using var database = await CreateAsync();
        await using var db = database.CreateContext();
        await PublishedAsync(db);
        await db.Database.ExecuteSqlRawAsync("UPDATE evidence_objects SET version=4,fencing_version=2; UPDATE attendance_storage_locations SET health='ready',accept_writes=true,version=2");
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM evidence_objects WHERE state='Published' AND version=4 AND fencing_version=2").SingleAsync());
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM attendance_storage_locations WHERE health='ready' AND accept_writes AND version=2").SingleAsync());
    }

    [Fact]
    public async Task Singleton_write_target_and_manifest_uniqueness_are_enforced()
    {
        await using var database = await CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.ExecuteSqlRawAsync(ObjectInsert);
        await db.Database.ExecuteSqlRawAsync("INSERT INTO attendance_storage_write_target(id,storage_id,version) VALUES(1,'00000000-0000-0000-0000-000000000101',1)");
        await RejectAsync(db, "INSERT INTO attendance_storage_write_target(id,version) VALUES(2,1)", "23514");
        await RejectAsync(db, "UPDATE attendance_storage_write_target SET storage_id='00000000-0000-0000-0000-000000000102'", "23514");
        await db.Database.ExecuteSqlRawAsync("UPDATE attendance_storage_write_target SET storage_id='00000000-0000-0000-0000-000000000102',version=2");
        await db.Database.ExecuteSqlRawAsync(JobInsert);
        const string item = "INSERT INTO evidence_migration_items(id,job_id,evidence_id,variant,expected_sha256,expected_length,source_id,target_id,attempts) VALUES(gen_random_uuid(),'00000000-0000-0000-0000-000000000601','00000000-0000-0000-0000-000000000201','full',repeat('a',64),1024,'00000000-0000-0000-0000-000000000101','00000000-0000-0000-0000-000000000102',0)";
        await db.Database.ExecuteSqlRawAsync(item);
        await RejectAsync(db, item, "23505");
        await RejectAsync(db, "UPDATE evidence_migration_items SET attempts=-1", "23514");
        await RejectAsync(db, "UPDATE evidence_migration_items SET target_id=source_id", "23503");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Downgrade_with_evidence_or_job_preserves_schema_history_and_guards(bool job)
    {
        await using var database = await CreateAsync();
        await using var db = database.CreateContext();
        // This tests Task1's frozen migration. Later empty migrations may downgrade independently.
        await db.GetService<IMigrator>().MigrateAsync("20260925031404_AddAttendanceEvidenceStorage");
        await db.Database.ExecuteSqlRawAsync(job ? JobInsert : ObjectInsert);
        var before = await db.Database.GetAppliedMigrationsAsync();
        Assert.Equal("P0001", (await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(Previous))).SqlState);
        Assert.Equal(before, await db.Database.GetAppliedMigrationsAsync());
        await RejectAsync(db, "UPDATE audit_events SET id=id", "P0001");
        await RejectAsync(db, "UPDATE attendance_storage_locations SET alias='repoint'", "23514");
    }

    [Fact]
    public async Task Empty_upgrade_downgrade_upgrade_preserves_6a_grants_and_history()
    {
        await using var database = new IdentityDatabase(postgres.ConnectionString);
        await database.InitializeAsync();
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(Previous);
        await SeedDirectoryAsync(db);
        await db.Database.ExecuteSqlRawAsync("INSERT INTO roles(id,name,role_class) VALUES ('10000000-0000-0000-0000-000000000099','Synthetic HR','approval'); INSERT INTO role_permissions(role_id,permission_id) VALUES ('10000000-0000-0000-0000-000000000099','20000000-0000-0000-0000-000000000015')");
        var memberships = await RowsAsync(db, "employee_memberships");
        var grants = await RowsAsync(db, "role_permissions");
        for (var i = 0; i < 2; i++)
        {
            await db.Database.MigrateAsync();
            Assert.Equal(memberships, await RowsAsync(db, "employee_memberships"));
            Assert.Equal(grants, await RowsAsync(db, "role_permissions"));
            await db.GetService<IMigrator>().MigrateAsync(Previous);
            Assert.Equal(memberships, await RowsAsync(db, "employee_memberships"));
            Assert.Equal(grants, await RowsAsync(db, "role_permissions"));
        }
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
    }

    private const string JobInsert = """
        INSERT INTO evidence_migration_jobs(id,request_id,source_id,target_id,source_version,target_version,requested_by,reason)
        VALUES('00000000-0000-0000-0000-000000000601',gen_random_uuid(),'00000000-0000-0000-0000-000000000101','00000000-0000-0000-0000-000000000102',1,1,'00000000-0000-0000-0000-000000000031','ทดสอบ');
        """;

    private async Task<IdentityDatabase> CreateAsync()
    {
        var database = new IdentityDatabase(postgres.ConnectionString);
        try
        {
            await database.InitializeAsync();
            await using var db = database.CreateContext();
            await db.Database.MigrateAsync();
            var tables = await db.Database.SqlQueryRaw<string>("SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname='public'").ToArrayAsync();
            Assert.Contains("evidence_objects", tables);
            await SeedDirectoryAsync(db);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO attendance_storage_locations(id,alias,kind,config_fingerprint,accept_writes) VALUES
                    ('00000000-0000-0000-0000-000000000101','local-a','local-folder',repeat('a',64),false),
                    ('00000000-0000-0000-0000-000000000102','nas-b','nas-mounted-folder',repeat('b',64),false);
                """);
            return database;
        }
        catch { await database.DisposeAsync(); throw; }
    }

    private static Task SeedDirectoryAsync(Tpr10DbContext db) => db.Database.ExecuteSqlRawAsync("""
        INSERT INTO users(id,username,normalized_username) VALUES ('00000000-0000-0000-0000-000000000031','employee','EMPLOYEE'),('00000000-0000-0000-0000-000000000032','other','OTHER');
        INSERT INTO workspaces(id,code,name,created_by) VALUES ('00000000-0000-0000-0000-000000000001','W1','หนึ่ง','00000000-0000-0000-0000-000000000031'),('00000000-0000-0000-0000-000000000002','W2','สอง','00000000-0000-0000-0000-000000000031');
        INSERT INTO departments(id,workspace_id,code,name,created_by) VALUES ('00000000-0000-0000-0000-000000000011','00000000-0000-0000-0000-000000000001','D1','หนึ่ง','00000000-0000-0000-0000-000000000031'),('00000000-0000-0000-0000-000000000012','00000000-0000-0000-0000-000000000002','D2','สอง','00000000-0000-0000-0000-000000000031');
        INSERT INTO employee_memberships(id,user_id,workspace_id,department_id,valid_from_utc,created_by,reason) VALUES ('00000000-0000-0000-0000-000000000041','00000000-0000-0000-0000-000000000031','00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000011','2026-09-25T00:00:00Z','00000000-0000-0000-0000-000000000031','ทดสอบ');
        INSERT INTO audit_events(id,event_type,occurred_at_utc,correlation_id,outcome) VALUES(gen_random_uuid(),'synthetic',now(),'synthetic','success');
        """);

    private static async Task PublishedAsync(Tpr10DbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(ObjectInsert);
        await db.Database.ExecuteSqlRawAsync(Prepare);
        await db.Database.ExecuteSqlRawAsync(Publish);
    }
    private static async Task<PostgresException> RejectAsync(Tpr10DbContext db, string sql, string expected)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Equal(expected, error.SqlState);
        return error;
    }
    private static Task<int> CountAsync(Tpr10DbContext db, string table) => db.Database.SqlQueryRaw<int>($"SELECT count(*)::int AS \"Value\" FROM {table}").SingleAsync();
    private static Task<string> RowsAsync(Tpr10DbContext db, string table) => db.Database.SqlQueryRaw<string>($"SELECT coalesce(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text),'[]'::jsonb)::text AS \"Value\" FROM {table} t").SingleAsync();
}
