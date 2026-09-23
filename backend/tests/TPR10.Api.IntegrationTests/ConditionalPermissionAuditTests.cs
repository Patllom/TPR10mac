using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Identity.Accounts;
using TPR10.Api.Identity.Data;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class ConditionalPermissionAuditTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task Conditional_role_denial_is_audited_and_never_mutates_accounts(bool patch, bool emptyRoles, bool auditFails)
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await driver.SeedUserAsync("manager", MfaTests.Password, ["users:manage"], requiresMfa: true);
        var target = await driver.SeedUserAsync("target", MfaTests.Password, []);
        Assert.Equal(HttpStatusCode.OK, (await driver.LoginAsync("manager", MfaTests.Password)).StatusCode);
        await AuthorizationTests.ConfirmAsync(driver);
        await using var db = driver.Database.CreateContext();
        var rolesBefore = await db.Set<UserRole>().Where(x => x.UserId == target).Select(x => x.RoleId).ToArrayAsync();
        var usersBefore = await db.Set<IdentityUser>().CountAsync();
        var token = await IdentityTestDriver.TokenAsync(driver.Client);
        if (auditFails)
            await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION reject_conditional_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'audit fault'; END $$; CREATE TRIGGER reject_conditional_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION reject_conditional_audit();");
        var correlation = Guid.NewGuid().ToString("D");
        using var request = IdentityTestDriver.Mutation(token, patch ? $"/api/v1/users/{target}" : "/api/v1/users");
        request.Method = patch ? HttpMethod.Patch : HttpMethod.Post;
        request.Headers.Add("X-Correlation-ID", correlation);
        var roleIds = emptyRoles ? Array.Empty<Guid>() : new[] { IdentityCatalog.AdministratorRoleId };
        request.Content = JsonContent.Create(patch ? (object)new { isActive = false, roleIds } : new { username = "unauthorized-new", password = MfaTests.Password, roleIds });
        using var response = await driver.Client.SendAsync(request);
        Assert.Equal(auditFails ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(usersBefore, await db.Set<IdentityUser>().CountAsync());
        var user = await db.Set<IdentityUser>().SingleAsync(x => x.Id == target);
        Assert.True(user.IsActive);
        Assert.Equal(0, user.SecurityVersion);
        Assert.Equal(rolesBefore, await db.Set<UserRole>().Where(x => x.UserId == target).Select(x => x.RoleId).ToArrayAsync());
        var events = await db.AuditEvents.Where(x => x.CorrelationId == correlation).ToArrayAsync();
        if (auditFails) { Assert.Empty(events); return; }
        var denied = Assert.Single(events);
        Assert.Equal("identity.authorization.denied", denied.EventType);
        Assert.Equal(actor, denied.ActorId);
        Assert.Equal("user", denied.TargetType);
        Assert.Equal(patch ? target : (Guid?)null, denied.TargetId);
        Assert.Equal("denied", denied.Outcome);
        var metadata = await db.AuditMetadata.Where(x => x.AuditEventId == denied.Id).ToDictionaryAsync(x => x.Key, x => x.Value);
        Assert.Equal("roles:manage", metadata["capability"]);
        Assert.Equal("permission-denied", metadata["reason"]);
    }
}
