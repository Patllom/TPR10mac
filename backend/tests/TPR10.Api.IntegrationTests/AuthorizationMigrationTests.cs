using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using TPR10.Api.Identity.Data;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class AuthorizationMigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Upgrade_keeps_historical_audit_immutable_and_adds_catalog_without_restoring_grants()
    {
        await using var database = new IdentityDatabase(postgres.ConnectionString);
        await database.InitializeAsync();
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync("20260923013836_AddMfaAttemptStates");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO roles(id,name,role_class) VALUES ('10000000-0000-0000-0000-000000000001','Admin','system-administration');
            INSERT INTO permissions(id,capability) VALUES ('20000000-0000-0000-0000-000000000001','users:manage');
            INSERT INTO role_permissions(role_id,permission_id) VALUES ('10000000-0000-0000-0000-000000000001','20000000-0000-0000-0000-000000000001');
            INSERT INTO audit_events(id,event_type,occurred_at_utc,correlation_id) VALUES ('30000000-0000-0000-0000-000000000001','historical',now(),'30000000-0000-0000-0000-000000000002');
            """);
        await db.Database.MigrateAsync();
        Assert.Equal(12, await db.Set<IdentityPermission>().CountAsync());
        var grants = await (from rp in db.Set<RolePermission>()
                            join p in db.Set<IdentityPermission>() on rp.PermissionId equals p.Id
                            select p.Capability).OrderBy(x => x).ToArrayAsync();
        Assert.Equal(new[] { "organization:manage", "scope-assignments:manage", "users:manage" }, grants);
        var old = await db.AuditEvents.SingleAsync();
        Assert.Equal("historical", old.EventType);
        Assert.Null(old.ActingRoleId); Assert.Null(old.Outcome); Assert.Null(old.TargetType);
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE audit_events SET outcome='success'"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM audit_events"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("TRUNCATE audit_events CASCADE"));
    }
}
