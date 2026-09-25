using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using TPR10.Api.Data;
using TPR10.Api.Identity.Accounts;

// SQL identifiers/expressions are fixed test literals, never user input.
#pragma warning disable EF1002

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class AttendanceDirectorySchemaTests(PostgresFixture postgres)
{
    private const string Previous = "20260923173517_AddOrganizationScopeFoundation";

    [Theory]
    [InlineData("employee_memberships")]
    [InlineData("reporting_lines")]
    [InlineData("hr_assignments")]
    public async Task Closed_history_cannot_be_rewritten_reopened_or_deleted(string table)
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await using var db = d.Database.CreateContext();
        await SeedAsync(db);
        await db.Database.ExecuteSqlRawAsync($"UPDATE {table} SET valid_to_utc='2026-09-26T00:00:00Z',ended_by=created_by,version=2");
        var before = await JsonRowsAsync(db, table);
        foreach (var assignment in new[] { "reason='เปลี่ยนย้อนหลัง'", "valid_to_utc=NULL", "valid_from_utc=valid_from_utc-interval '1 day'", "version=3" })
        {
            Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync($"UPDATE {table} SET {assignment}"))).SqlState);
            Assert.Equal(before, await JsonRowsAsync(db, table));
        }
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync($"DELETE FROM {table}"))).SqlState);
        Assert.Equal(before, await JsonRowsAsync(db, table));
    }

    [Fact]
    public async Task Migration_creates_all_three_directory_tables_and_matches_model()
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await using var db = d.Database.CreateContext();
        var names = await db.Database.SqlQueryRaw<string>("SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname='public'").ToArrayAsync();
        foreach (var name in new[] { "employee_memberships", "reporting_lines", "hr_assignments" })
            Assert.Contains(name, names);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData("employee_memberships", "workspace_id='00000000-0000-0000-0000-000000000002'", "23503")]
    [InlineData("hr_assignments", "workspace_id='00000000-0000-0000-0000-000000000002'", "23503")]
    [InlineData("reporting_lines", "employee_user_id='00000000-0000-0000-0000-000000000033'", "23503")]
    [InlineData("reporting_lines", "supervisor_user_id=employee_user_id", "23514")]
    [InlineData("reporting_lines", "supervisor_user_id=gen_random_uuid()", "23503")]
    [InlineData("employee_memberships", "user_id=gen_random_uuid()", "23503")]
    [InlineData("hr_assignments", "user_id=gen_random_uuid()", "23503")]
    [InlineData("employee_memberships", "valid_to_utc=valid_from_utc", "23514")]
    [InlineData("hr_assignments", "valid_to_utc=valid_from_utc-interval '1 second'", "23514")]
    [InlineData("reporting_lines", "valid_to_utc=valid_from_utc", "23514")]
    [InlineData("employee_memberships", "version=0", "23514")]
    [InlineData("reporting_lines", "version=0", "23514")]
    [InlineData("hr_assignments", "version=0", "23514")]
    [InlineData("employee_memberships", "reason=''", "23514")]
    [InlineData("reporting_lines", "reason='   '", "23514")]
    [InlineData("hr_assignments", "reason=E'bad\\nreason'", "23514")]
    [InlineData("employee_memberships", "reason=repeat('x',501)", "22001")]
    [InlineData("reporting_lines", "created_by=gen_random_uuid()", "23503")]
    [InlineData("hr_assignments", "ended_by=gen_random_uuid()", "23503")]
    public async Task Database_rejects_invalid_directory_references_and_metadata(string table, string assignment, string state)
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await using var db = d.Database.CreateContext();
        await SeedAsync(db);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync($"UPDATE {table} SET {assignment}"));
        Assert.Equal(state, ex.SqlState);
    }

    [Theory]
    [InlineData("employee_memberships")]
    [InlineData("reporting_lines")]
    [InlineData("hr_assignments")]
    public async Task Overlap_rejected_but_adjacent_intervals_preserve_two_history_rows(string table)
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await using var db = d.Database.CreateContext();
        await SeedAsync(db);
        var copy = CopySql(table);
        Assert.Equal("23P01", (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(copy))).SqlState);
        await db.Database.ExecuteSqlRawAsync($"UPDATE {table} SET valid_to_utc='2026-09-26T00:00:00Z',ended_by=created_by,version=2");
        await db.Database.ExecuteSqlRawAsync(CopySql(table, "'2026-09-26T00:00:00Z'::timestamptz"));
        Assert.Equal(2, await CountAsync(db, table));
        Assert.Equal(1, await CountAsync(db, table, "valid_to_utc IS NULL"));
        Assert.Equal(1, await CountAsync(db, table, "valid_to_utc='2026-09-26T00:00:00Z' AND version=2"));
        // Extending the old interval over the new one must also be rejected.
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            $"UPDATE {table} SET valid_to_utc='2026-09-27T00:00:00Z' WHERE valid_to_utc IS NOT NULL"))).SqlState);
    }

    [Fact]
    public async Task Primary_membership_is_unique_across_workspaces_but_hr_may_cover_multiple_units()
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await using var db = d.Database.CreateContext();
        await SeedAsync(db);
        const string second = "'00000000-0000-0000-0000-000000000002'::uuid,'00000000-0000-0000-0000-000000000012'::uuid";
        var copy = CopySql("employee_memberships").Replace("SELECT gen_random_uuid(),user_id,workspace_id,department_id,", $"SELECT gen_random_uuid(),user_id,{second},");
        Assert.Equal("23P01", (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(copy))).SqlState);
        await db.Database.ExecuteSqlRawAsync(CopySql("hr_assignments").Replace("SELECT gen_random_uuid(),user_id,workspace_id,department_id,", $"SELECT gen_random_uuid(),user_id,{second},"));
        Assert.Equal(2, await CountAsync(db, "hr_assignments"));
    }

    [Theory]
    [InlineData("employee_memberships")]
    [InlineData("reporting_lines")]
    [InlineData("hr_assignments")]
    public async Task Concurrent_overlapping_inserts_allow_only_one_commit(string table)
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await using var db = d.Database.CreateContext();
        await SeedAsync(db);
        await db.Database.ExecuteSqlRawAsync($"UPDATE {table} SET valid_to_utc='2026-09-26T00:00:00Z',ended_by=created_by");
        await using var first = new NpgsqlConnection(d.Database.ConnectionString);
        await using var second = new NpgsqlConnection(d.Database.ConnectionString);
        await first.OpenAsync(); await second.OpenAsync();
        await using var tx1 = await first.BeginTransactionAsync();
        await using var tx2 = await second.BeginTransactionAsync();
        var sql = CopySql(table, "'2026-09-26T00:00:00Z'::timestamptz");
        await using var cmd1 = new NpgsqlCommand(sql, first, tx1);
        await cmd1.ExecuteNonQueryAsync();
        await using var cmd2 = new NpgsqlCommand(sql, second, tx2);
        var contender = cmd2.ExecuteNonQueryAsync();
        var firstCommitted = false;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!contender.IsCompleted)
            {
                var waiting = await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE pid={second.ProcessID} AND wait_event_type='Lock'").SingleAsync(deadline.Token);
                if (waiting == 1) break;
                await Task.Delay(10, deadline.Token);
            }
            Assert.False(contender.IsCompleted); // Observed DB lock, not guessed by sleeping.
            await tx1.CommitAsync();
            firstCommitted = true;
            Assert.Equal("23P01", (await Assert.ThrowsAsync<PostgresException>(async () => await contender)).SqlState);
            await tx2.RollbackAsync();
            Assert.Equal(1, await CountAsync(db, table, "valid_to_utc IS NULL"));
        }
        finally
        {
            if (!firstCommitted) await tx1.RollbackAsync();
            if (!contender.IsCompleted) { try { await contender; } catch (PostgresException) { } }
        }
    }

    [Fact]
    public async Task Upgrade_downgrade_upgrade_preserves_all_legacy_rows_and_grants()
    {
        await using var d = new IdentityDatabase(postgres.ConnectionString);
        await d.InitializeAsync();
        await using var db = d.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(Previous);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO users(id,username,normalized_username,email,security_version) VALUES ('00000000-0000-0000-0000-000000000031','legacy','LEGACY','synthetic@example.invalid',7);
            INSERT INTO roles(id,name,role_class) VALUES ('10000000-0000-0000-0000-000000000001','System Administrator','system-administration');
            INSERT INTO permissions(id,capability) VALUES ('20000000-0000-0000-0000-000000000001','users:manage');
            INSERT INTO role_permissions(role_id,permission_id) VALUES ('10000000-0000-0000-0000-000000000001','20000000-0000-0000-0000-000000000001');
            INSERT INTO user_roles(user_id,role_id) VALUES ('00000000-0000-0000-0000-000000000031','10000000-0000-0000-0000-000000000001');
            INSERT INTO sessions(id,user_id,token_hash,stage,created_at_utc,last_seen_at_utc,expires_at_utc,security_version) VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000031',decode(repeat('ab',32),'hex'),'Active',now(),now(),now()+interval '1 hour',7);
            INSERT INTO audit_events(id,event_type,occurred_at_utc,correlation_id,actor_id,outcome) VALUES ('00000000-0000-0000-0000-000000000051','before-attendance',now(),'synthetic','00000000-0000-0000-0000-000000000031','success');
            INSERT INTO audit_event_metadata(id,audit_event_id,key,value) VALUES(gen_random_uuid(),'00000000-0000-0000-0000-000000000051','reason','synthetic');
            """);
        var tables = new[] { "users", "roles", "user_roles", "role_permissions", "sessions", "audit_events", "audit_event_metadata" };
        var before = new Dictionary<string, string>();
        foreach (var table in tables) before[table] = await JsonRowsAsync(db, table);
        for (var i = 0; i < 2; i++)
        {
            await migrator.MigrateAsync();
            Assert.Equal(7, await CountAsync(db, "permissions", "capability LIKE 'attendance:%'"));
            foreach (var table in tables) Assert.Equal(before[table], await JsonRowsAsync(db, table));
            foreach (var table in new[] { "audit_events", "audit_event_metadata" })
                Assert.Equal("P0001", (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync($"UPDATE {table} SET id=id"))).SqlState);
            await migrator.MigrateAsync(Previous);
            Assert.Equal(0, await CountAsync(db, "permissions", "capability LIKE 'attendance:%'"));
            foreach (var table in tables) Assert.Equal(before[table], await JsonRowsAsync(db, table));
        }
        await migrator.MigrateAsync();
        await IdentityCatalog.SeedAsync(db, DateTimeOffset.UtcNow, default);
        await db.SaveChangesAsync();
        Assert.Equal(19, await CountAsync(db, "permissions"));
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData("attendance:hr-read")]
    [InlineData("attendance:directory-manage")]
    [InlineData("attendance:record")]
    public async Task Downgrade_with_new_grants_is_refused_without_erasing_grant_or_schema(string capability)
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await using var db = d.Database.CreateContext();
        // ตรวจ rollback ของ 6A จากรุ่น 6A ไม่สมมติว่า migration ล่าสุดยังเป็น 6A เสมอ
        await db.GetService<IMigrator>().MigrateAsync("20260924193737_ProtectAttendanceDirectoryHistory");
        var migrationsBefore = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        await IdentityCatalog.SeedAsync(db, d.Clock.GetUtcNow(), default);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO role_permissions(role_id,permission_id) SELECT '10000000-0000-0000-0000-000000000003'::uuid,id FROM permissions WHERE capability={capability}");
        var before = await JsonRowsAsync(db, "role_permissions");
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(Previous));
        Assert.Equal("P0001", error.SqlState);
        Assert.Contains("attendance", error.MessageText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await JsonRowsAsync(db, "role_permissions"));
        Assert.Equal(migrationsBefore, await db.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task Downgrade_refuses_to_discard_directory_history()
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await using var db = d.Database.CreateContext();
        await SeedAsync(db);
        var before = await JsonRowsAsync(db, "employee_memberships");
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(Previous));
        Assert.Equal("P0001", error.SqlState);
        Assert.Equal(before, await JsonRowsAsync(db, "employee_memberships"));
    }

    [Theory]
    [InlineData(System.Data.IsolationLevel.RepeatableRead)]
    [InlineData(System.Data.IsolationLevel.Serializable)]
    public async Task Directory_writes_refuse_snapshot_isolation_that_could_hide_a_concurrent_interval(System.Data.IsolationLevel isolation)
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await using var db = d.Database.CreateContext();
        await SeedAsync(db);
        await using var tx = await db.Database.BeginTransactionAsync(isolation);
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE hr_assignments SET reason='เปลี่ยนเหตุผล'"));
        Assert.Equal("25001", error.SqlState);
    }

    private static Task<int> CountAsync(Tpr10DbContext db, string table, string predicate = "true") =>
        db.Database.SqlQueryRaw<int>($"SELECT count(*)::int AS \"Value\" FROM {table} WHERE {predicate}").SingleAsync();
    private static Task<string> JsonRowsAsync(Tpr10DbContext db, string table) =>
        db.Database.SqlQueryRaw<string>($"SELECT coalesce(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text),'[]'::jsonb)::text AS \"Value\" FROM {table} t").SingleAsync();
    private static string CopySql(string table, string from = "valid_from_utc")
    {
        var keys = table == "reporting_lines" ? "employee_membership_id,employee_user_id,supervisor_user_id" : "user_id,workspace_id,department_id";
        return $"INSERT INTO {table}(id,{keys},valid_from_utc,created_by,reason) SELECT gen_random_uuid(),{keys},{from},created_by,reason FROM {table} WHERE valid_to_utc IS NULL OR valid_to_utc='2026-09-26T00:00:00Z'";
    }
    private static async Task SeedAsync(Tpr10DbContext db)
    {
        var names = await db.Database.SqlQueryRaw<string>("SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname='public'").ToArrayAsync();
        Assert.Contains("employee_memberships", names);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO users(id,username,normalized_username) VALUES ('00000000-0000-0000-0000-000000000031','employee','EMPLOYEE'),('00000000-0000-0000-0000-000000000032','manager','MANAGER');
            INSERT INTO users(id,username,normalized_username) VALUES ('00000000-0000-0000-0000-000000000033','unrelated','UNRELATED');
            INSERT INTO workspaces(id,code,name,created_by) VALUES ('00000000-0000-0000-0000-000000000001','W1','หนึ่ง','00000000-0000-0000-0000-000000000031'),('00000000-0000-0000-0000-000000000002','W2','สอง','00000000-0000-0000-0000-000000000031');
            INSERT INTO departments(id,workspace_id,code,name,created_by) VALUES ('00000000-0000-0000-0000-000000000011','00000000-0000-0000-0000-000000000001','D1','หนึ่ง','00000000-0000-0000-0000-000000000031'),('00000000-0000-0000-0000-000000000012','00000000-0000-0000-0000-000000000002','D2','สอง','00000000-0000-0000-0000-000000000031');
            INSERT INTO employee_memberships(id,user_id,workspace_id,department_id,valid_from_utc,created_by,reason) VALUES ('00000000-0000-0000-0000-000000000041','00000000-0000-0000-0000-000000000031','00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000011','2026-09-25T00:00:00Z','00000000-0000-0000-0000-000000000031','ทดสอบ');
            INSERT INTO reporting_lines(id,employee_membership_id,employee_user_id,supervisor_user_id,valid_from_utc,created_by,reason) VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000041','00000000-0000-0000-0000-000000000031','00000000-0000-0000-0000-000000000032','2026-09-25T00:00:00Z','00000000-0000-0000-0000-000000000031','ทดสอบ');
            INSERT INTO hr_assignments(id,user_id,workspace_id,department_id,valid_from_utc,created_by,reason) VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000032','00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000011','2026-09-25T00:00:00Z','00000000-0000-0000-0000-000000000031','ทดสอบ');
            """);
    }
}
