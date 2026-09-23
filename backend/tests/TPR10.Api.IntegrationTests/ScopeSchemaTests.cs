using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using TPR10.Api.Data;
using TPR10.Api.Scopes.Data;

namespace TPR10.Api.IntegrationTests;

// SQL identifiers/expressions below come only from fixed test literals, never requests.
#pragma warning disable EF1002

[Collection("database")]
public sealed class ScopeSchemaTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Nullable_levels_reject_dangling_workspace_or_cross_workspace_project(bool record, bool projectLevel)
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await using var db = d.Database.CreateContext();
        await SeedAsync(db);
        var table = record ? "scope_probe_records" : "user_scope_assignments";
        var constraint = projectLevel ? $"FK_{table}_projects_workspace_id_project_id" : $"FK_{table}_workspaces_workspace_id";
        var workspace = projectLevel ? "'00000000-0000-0000-0000-000000000002'::uuid" : "gen_random_uuid()";
        var project = projectLevel ? "'00000000-0000-0000-0000-000000000011'::uuid" : "NULL::uuid";
        var fields = record ? "note" : "user_id,role_id,reason";
        var sql = $"INSERT INTO {table}(id,workspace_id,project_id,site_id,created_by,{fields}) SELECT gen_random_uuid(),{workspace},{project},NULL,created_by,{fields} FROM {table}";
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Equal("23503", error.SqlState);
        Assert.Equal(constraint, error.ConstraintName);
    }

    [Fact]
    public async Task Ef_mapping_preserves_exact_nullable_levels_and_versions()
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var f = await ScopeFixture.CreateAsync(d);
        foreach (var key in new[] { f.Workspace, f.Project, f.Site })
        {
            var assignment = await f.GrantAsync(key, "staff", "scope-probe:read");
            var record = await f.RecordAsync(key, "สาธารณะ", "จำกัดสิทธิ์");
            await using var db = d.Database.CreateContext();
            var a = await db.Set<ScopeAssignment>().SingleAsync(x => x.Id == assignment);
            var r = await db.Set<ScopeProbeRecord>().SingleAsync(x => x.Id == record);
            Assert.Equal((key.WorkspaceId, key.ProjectId, key.SiteId), (a.WorkspaceId, a.ProjectId, a.SiteId));
            Assert.Equal((key.WorkspaceId, key.ProjectId, key.SiteId), (r.WorkspaceId, r.ProjectId, r.SiteId));
            Assert.Equal(1, a.Version); Assert.Equal(1, r.Version);
            Assert.Equal(d.Clock.GetUtcNow(), a.CreatedAtUtc);
            Assert.Equal(f.UserId, r.CreatedBy);
            Assert.Equal("จำกัดสิทธิ์", r.RestrictedNote);
            Assert.Null(a.RevokedAtUtc); Assert.Null(a.RevokedBy); Assert.Null(a.RevocationReason);
        }
    }

    [Fact]
    public async Task Assignment_table_is_present_after_migration()
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await using var db = d.Database.CreateContext();
        await AssertTablesAsync(db);
        Assert.False(db.Database.HasPendingModelChanges());
        foreach (var table in new[] { "workspaces", "departments", "projects", "sites", "user_scope_assignments", "scope_probe_records" })
            Assert.Equal(0, await db.Database.SqlQueryRaw<int>($"SELECT count(*)::int AS \"Value\" FROM {table}").SingleAsync());
    }

    [Theory]
    [InlineData("UPDATE projects SET workspace_id='00000000-0000-0000-0000-000000000002' WHERE id='00000000-0000-0000-0000-000000000011'", "23503")]
    [InlineData("UPDATE sites SET workspace_id='00000000-0000-0000-0000-000000000002'", "23503")]
    [InlineData("UPDATE user_scope_assignments SET workspace_id='00000000-0000-0000-0000-000000000002'", "23503")]
    [InlineData("UPDATE scope_probe_records SET workspace_id='00000000-0000-0000-0000-000000000002'", "23503")]
    [InlineData("UPDATE user_scope_assignments SET project_id='00000000-0000-0000-0000-000000000012'", "23503")]
    [InlineData("UPDATE scope_probe_records SET project_id='00000000-0000-0000-0000-000000000012'", "23503")]
    [InlineData("UPDATE user_scope_assignments SET project_id=NULL", "23514")]
    [InlineData("UPDATE scope_probe_records SET project_id=NULL", "23514")]
    [InlineData("UPDATE user_scope_assignments SET revoked_at_utc=now()", "23514")]
    [InlineData("UPDATE user_scope_assignments SET revoked_by=created_by", "23514")]
    [InlineData("UPDATE user_scope_assignments SET revocation_reason='เหตุผล'", "23514")]
    [InlineData("UPDATE user_scope_assignments SET role_id=gen_random_uuid()", "23503")]
    [InlineData("UPDATE user_scope_assignments SET user_id=gen_random_uuid()", "23503")]
    [InlineData("UPDATE departments SET workspace_id=gen_random_uuid()", "23503")]
    [InlineData("DELETE FROM workspaces", "23503")]
    [InlineData("DELETE FROM users", "23503")]
    [InlineData("UPDATE workspaces SET code=' lower '", "23514")]
    [InlineData("UPDATE sites SET name=E'bad\\nname'", "23514")]
    [InlineData("UPDATE user_scope_assignments SET version=0", "23514")]
    [InlineData("UPDATE scope_probe_records SET version=0", "23514")]
    [InlineData("UPDATE permissions SET domain='wildcard'", "23514")]
    public async Task Database_rejects_invalid_hierarchy_or_metadata(string sql, string state)
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await using var db = d.Database.CreateContext();
        await SeedAsync(db);
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Equal(state, error.SqlState);
    }

    [Theory]
    [InlineData("NULL", "NULL")]
    [InlineData("'00000000-0000-0000-0000-000000000011'", "NULL")]
    [InlineData("'00000000-0000-0000-0000-000000000011'", "'00000000-0000-0000-0000-000000000021'")]
    public async Task Active_assignment_is_unique_at_each_level_but_regrant_preserves_history(string project, string site)
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await using var db = d.Database.CreateContext();
        await SeedAsync(db);
        await db.Database.ExecuteSqlRawAsync($"UPDATE user_scope_assignments SET project_id={project}, site_id={site}");
        const string copy = "INSERT INTO user_scope_assignments(id,user_id,workspace_id,project_id,site_id,role_id,created_by,reason) SELECT gen_random_uuid(),user_id,workspace_id,project_id,site_id,role_id,created_by,reason FROM user_scope_assignments";
        Assert.Equal("23505", (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(copy))).SqlState);
        await db.Database.ExecuteSqlRawAsync("UPDATE user_scope_assignments SET revoked_at_utc=now(),revoked_by=created_by,revocation_reason='ถอนเพื่อทดสอบ',version=version+1");
        await db.Database.ExecuteSqlRawAsync(copy);
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM user_scope_assignments").SingleAsync());
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM user_scope_assignments WHERE revoked_at_utc IS NULL").SingleAsync());
    }

    [Fact]
    public async Task Upgrade_roundtrip_preserves_identity_session_and_immutable_audit()
    {
        await using var d = new IdentityDatabase(postgres.ConnectionString);
        await d.InitializeAsync();
        await using var db = d.CreateContext();
        const string previous = "20260923045713_AddTemporaryCredentialLifecycle";
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(previous);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO users(id,username,normalized_username) VALUES ('00000000-0000-0000-0000-000000000031','existing','EXISTING');
            INSERT INTO roles(id,name,role_class) VALUES ('10000000-0000-0000-0000-000000000001','System Administrator','system-administration');
            INSERT INTO permissions(id,capability) VALUES ('20000000-0000-0000-0000-000000000001','users:manage');
            INSERT INTO role_permissions(role_id,permission_id) VALUES ('10000000-0000-0000-0000-000000000001','20000000-0000-0000-0000-000000000001');
            INSERT INTO user_roles(user_id,role_id) VALUES ('00000000-0000-0000-0000-000000000031','10000000-0000-0000-0000-000000000001');
            INSERT INTO sessions(id,user_id,token_hash,stage,created_at_utc,last_seen_at_utc,expires_at_utc) VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000031',decode(repeat('ab',32),'hex'),'Active',now(),now(),now()+interval '1 hour');
            INSERT INTO audit_events(id,event_type,occurred_at_utc,correlation_id) VALUES ('00000000-0000-0000-0000-000000000041','before-scope',now(),'scope-migration');
            INSERT INTO audit_event_metadata(id,audit_event_id,key,value) VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000041','source','migration-test');
            """);
        for (var round = 0; round < 2; round++)
        {
            await migrator.MigrateAsync();
            await AssertTablesAsync(db);
            Assert.Equal("system", await db.Database.SqlQueryRaw<string>("SELECT domain AS \"Value\" FROM permissions WHERE capability='users:manage'").SingleAsync());
            Assert.Equal(3, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM role_permissions").SingleAsync());
            Assert.Equal("before-scope", (await db.AuditEvents.SingleAsync()).EventType);
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM sessions").SingleAsync());
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM user_roles").SingleAsync());
            foreach (var table in new[] { "audit_events", "audit_event_metadata" })
                Assert.Equal("P0001", (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync($"UPDATE {table} SET id=id"))).SqlState);
            await migrator.MigrateAsync(previous);
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM role_permissions").SingleAsync());
        }
        await migrator.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
    }

    private static async Task AssertTablesAsync(Tpr10DbContext db)
    {
        var names = await db.Database.SqlQueryRaw<string>("SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname='public'").ToArrayAsync();
        foreach (var table in new[] { "workspaces", "departments", "projects", "sites", "user_scope_assignments", "scope_probe_records" })
            Assert.Contains(table, names);
    }

    private static async Task SeedAsync(Tpr10DbContext db)
    {
        await AssertTablesAsync(db);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO users(id,username,normalized_username) VALUES ('00000000-0000-0000-0000-000000000031','actor','ACTOR');
            INSERT INTO roles(id,name,role_class) VALUES ('00000000-0000-0000-0000-000000000032','scope-role','staff');
            INSERT INTO workspaces(id,code,name,created_by) VALUES ('00000000-0000-0000-0000-000000000001','W1','หนึ่ง','00000000-0000-0000-0000-000000000031'),('00000000-0000-0000-0000-000000000002','W2','สอง','00000000-0000-0000-0000-000000000031');
            INSERT INTO departments(id,workspace_id,code,name,created_by) VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000001','D1','แผนก','00000000-0000-0000-0000-000000000031');
            INSERT INTO projects(id,workspace_id,code,name,created_by) VALUES ('00000000-0000-0000-0000-000000000011','00000000-0000-0000-0000-000000000001','P1','โครงการหนึ่ง','00000000-0000-0000-0000-000000000031'),('00000000-0000-0000-0000-000000000012','00000000-0000-0000-0000-000000000001','P2','โครงการสอง','00000000-0000-0000-0000-000000000031');
            INSERT INTO sites(id,workspace_id,project_id,code,name,created_by) VALUES ('00000000-0000-0000-0000-000000000021','00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000011','S1','ไซต์','00000000-0000-0000-0000-000000000031');
            INSERT INTO user_scope_assignments(id,user_id,workspace_id,project_id,site_id,role_id,created_by,reason) VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000031','00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000011','00000000-0000-0000-0000-000000000021','00000000-0000-0000-0000-000000000032','00000000-0000-0000-0000-000000000031','ทดสอบ');
            INSERT INTO scope_probe_records(id,workspace_id,project_id,site_id,note,created_by) VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000011','00000000-0000-0000-0000-000000000021','ทดสอบ','00000000-0000-0000-0000-000000000031');
            """);
    }
}
