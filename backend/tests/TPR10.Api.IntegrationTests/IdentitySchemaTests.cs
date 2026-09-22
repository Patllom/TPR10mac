using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class IdentitySchemaTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Migration_creates_identity_tables_and_preserves_audit(bool upgrade)
    {
        await using var database = new IdentityDatabase(postgres.ConnectionString);
        await database.InitializeAsync();
        await using var db = database.CreateContext();
        if (upgrade)
        {
            await db.GetService<IMigrator>().MigrateAsync("20260922164503_InitialFoundation");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO audit_events(id,event_type,occurred_at_utc,correlation_id) VALUES ('00000000-0000-0000-0000-000000000001','before-upgrade',now(),'schema-test')");
        }
        await db.Database.MigrateAsync();
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM information_schema.tables WHERE table_schema='public' AND table_name IN ('users','local_credentials','external_identities','roles','permissions','role_permissions','user_roles','mfa_factors','sessions','password_reset_requests','pre_auth_flows','mfa_recovery_codes','identity_delivery_outbox')", connection);
        Assert.Equal(13L, await command.ExecuteScalarAsync());
        if (upgrade)
            Assert.Equal("before-upgrade", (await db.AuditEvents.SingleAsync()).EventType);
        if (!upgrade)
            await db.Database.ExecuteSqlRawAsync("INSERT INTO audit_events(id,event_type,occurred_at_utc,correlation_id) VALUES ('00000000-0000-0000-0000-000000000001','fresh-database',now(),'schema-test')");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO audit_event_metadata(id,audit_event_id,key,value) VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000001','source','migration-test')");
        foreach (var table in new[] { "audit_events", "audit_event_metadata" })
        {
            foreach (var sql in new[] { $"UPDATE {table} SET id=id", $"DELETE FROM {table}", $"TRUNCATE {table} CASCADE" })
            {
                await using var mutation = new NpgsqlCommand(sql, connection);
                var error = await Assert.ThrowsAsync<PostgresException>(() => mutation.ExecuteNonQueryAsync());
                Assert.Equal("P0001", error.SqlState);
            }
        }
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData("INSERT INTO users(id,username,normalized_username) VALUES (gen_random_uuid(),'duplicate','STAFF')", "23505")]
    [InlineData("DELETE FROM users WHERE normalized_username='STAFF'", "23503")]
    [InlineData("INSERT INTO local_credentials(user_id,password_hash) VALUES (gen_random_uuid(),'test-only')", "23503")]
    [InlineData("INSERT INTO user_roles(user_id,role_id) SELECT user_id,role_id FROM user_roles", "23505")]
    [InlineData("INSERT INTO role_permissions(role_id,permission_id) SELECT role_id,permission_id FROM role_permissions", "23505")]
    [InlineData("INSERT INTO external_identities(id,user_id,provider,subject) SELECT gen_random_uuid(),user_id,provider,subject FROM external_identities", "23505")]
    public async Task Identity_constraints_prevent_duplicates_or_dangling_links(string sql, string sqlState)
    {
        await using var database = new IdentityDatabase(postgres.ConnectionString);
        await database.InitializeAsync();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO users(id,username,normalized_username) VALUES ('00000000-0000-0000-0000-000000000011','staff','STAFF');
            INSERT INTO local_credentials(user_id,password_hash) VALUES ('00000000-0000-0000-0000-000000000011','test-only');
            INSERT INTO roles(id,name,role_class) VALUES ('00000000-0000-0000-0000-000000000012','staff','staff');
            INSERT INTO permissions(id,capability) VALUES ('00000000-0000-0000-0000-000000000013','system:probe');
            INSERT INTO user_roles(user_id,role_id) VALUES ('00000000-0000-0000-0000-000000000011','00000000-0000-0000-0000-000000000012');
            INSERT INTO role_permissions(role_id,permission_id) VALUES ('00000000-0000-0000-0000-000000000012','00000000-0000-0000-0000-000000000013');
            INSERT INTO external_identities(id,user_id,provider,subject) VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000011','test','external-subject');
            """);
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Equal(sqlState, error.SqlState);
    }
}
