using Microsoft.EntityFrameworkCore;
using Npgsql;
using TPR10.Api.Auditing;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class SecurityAuditTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Typed_security_event_persists_actor_role_target_outcome_and_nullable_scope()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var actor = Guid.NewGuid(); var role = Guid.NewGuid(); var target = Guid.NewGuid();
        await using var db = driver.Database.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        await AccountProvisioningTests.Audit(driver, db).WriteAsync(new SecurityAuditRequest(actor, role,
            null, null, null, "identity.role.updated", "role", target, "success", new Dictionary<string, string> { ["changed-fields"] = "name" }), default);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        await using var connection = new NpgsqlConnection(driver.Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT actor_id,acting_role_id,target_type,target_id,outcome,workspace_id,project_id,site_id FROM audit_events", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(actor, reader.GetGuid(0)); Assert.Equal(role, reader.GetGuid(1));
        Assert.Equal("role", reader.GetString(2)); Assert.Equal(target, reader.GetGuid(3));
        Assert.Equal("success", reader.GetString(4));
        Assert.True(reader.IsDBNull(5) && reader.IsDBNull(6) && reader.IsDBNull(7));
    }

    [Theory]
    [InlineData("password")]
    [InlineData("session-token")]
    [InlineData("mfa-secret")]
    public async Task Typed_audit_rejects_sensitive_metadata_keys(string key)
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await using var db = driver.Database.CreateContext();
        await Assert.ThrowsAsync<ArgumentException>(() => AccountProvisioningTests.Audit(driver, db).WriteAsync(
            new SecurityAuditRequest(null, null, null, null, null, "identity.test", "user", null, "denied",
                new Dictionary<string, string> { [key] = "do-not-persist" }), default));
        Assert.Empty(await db.AuditEvents.ToListAsync());
    }
}
