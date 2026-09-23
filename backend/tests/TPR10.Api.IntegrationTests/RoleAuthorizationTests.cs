using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Identity.Accounts;
using TPR10.Api.Identity.Data;
using static TPR10.Api.IntegrationTests.MfaTests;
using static TPR10.Api.IntegrationTests.AuthorizationTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class RoleAuthorizationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Role_lifecycle_catalog_and_validation_are_available_to_authorized_admin()
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await AdminAsync(driver);
        using var permissions = await driver.Client.GetAsync("/api/v1/permissions");
        Assert.Equal(HttpStatusCode.OK, permissions.StatusCode);
        Assert.True(permissions.Headers.CacheControl?.NoStore);
        using var catalog = JsonDocument.Parse(await permissions.Content.ReadAsStringAsync());
        Assert.Equal(6, catalog.RootElement.GetArrayLength());
        using var created = await driver.PostAsync("/api/v1/roles", new { name = "Custom", roleClass = "staff" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(driver, HttpMethod.Patch, $"/api/v1/roles/{id}", new { name = "Renamed" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await driver.PostAsync("/api/v1/roles", new { name = "Renamed", roleClass = "staff" })).StatusCode);
        driver.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.BadRequest, (await driver.PostAsync("/api/v1/roles", new { name = "Bad", roleClass = "unknown" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(driver, HttpMethod.Put, $"/api/v1/roles/{id}/permissions", new { permissionIds = new[] { Guid.NewGuid() } })).StatusCode);
        using var roles = await driver.Client.GetAsync("/api/v1/roles");
        Assert.Equal(HttpStatusCode.OK, roles.StatusCode);
        Assert.Contains("Renamed", await roles.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("grants")]
    [InlineData("roles")]
    [InlineData("signout")]
    public async Task Mutation_revokes_existing_cookie_and_increments_user_version(string operation)
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await AdminAsync(driver);
        var target = await driver.SeedUserAsync("target", Password, ["system:probe"]);
        using var targetClient = driver.NewClient();
        using var login = IdentityTestDriver.Mutation(await IdentityTestDriver.TokenAsync(targetClient), "/api/v1/auth/login");
        login.Content = JsonContent.Create(new { username = "target", password = Password });
        Assert.Equal(HttpStatusCode.OK, (await targetClient.SendAsync(login)).StatusCode);
        await using var db = driver.Database.CreateContext();
        var role = await db.Set<UserRole>().SingleAsync(x => x.UserId == target);
        var path = operation == "grants" ? $"/api/v1/roles/{role.RoleId}/permissions" : operation == "roles" ? $"/api/v1/users/{target}/roles" : $"/api/v1/users/{target}/sign-out-everywhere";
        using var response = await SendAsync(driver, operation == "signout" ? HttpMethod.Post : HttpMethod.Put, path,
            operation == "grants" ? (object)new { permissionIds = Array.Empty<Guid>() } : new { roleIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, (await db.Set<IdentityUser>().SingleAsync(x => x.Id == target)).SecurityVersion);
        Assert.Equal(HttpStatusCode.Unauthorized, (await targetClient.GetAsync("/api/v1/auth/session")).StatusCode);
    }

    [Theory]
    [InlineData("roles")]
    [InlineData("grants")]
    [InlineData("disable")]
    public async Task Last_admin_cannot_be_stripped_of_management_authority(string operation)
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await AdminAsync(driver);
        var path = operation == "roles" ? $"/api/v1/users/{actor}/roles" : operation == "grants" ? $"/api/v1/roles/{IdentityCatalog.AdministratorRoleId}/permissions" : $"/api/v1/users/{actor}";
        using var response = await SendAsync(driver, operation == "disable" ? HttpMethod.Patch : HttpMethod.Put, path,
            operation == "roles" ? (object)new { roleIds = Array.Empty<Guid>() } : operation == "grants" ? new { permissionIds = Array.Empty<Guid>() } : new { isActive = false });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await driver.Client.GetAsync("/api/v1/users")).StatusCode);
    }

    [Fact]
    public async Task Operator_http_ignores_supplied_actor_and_requires_nonself_recovery()
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await AdminAsync(driver);
        using var response = await driver.PostAsync($"/api/v1/users/{actor}/mfa/recover", new { actorSessionId = Guid.NewGuid(), reason = "lost", evidenceReference = "CASE-1" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var target = await driver.SeedUserAsync("target", Password, []);
        Assert.Equal(HttpStatusCode.NoContent, (await driver.PostAsync($"/api/v1/users/{target}/mfa/recover", new { actorSessionId = Guid.Empty, reason = "lost", evidenceReference = "CASE-2" })).StatusCode);
        await using var db = driver.Database.CreateContext();
        Assert.Equal(actor, (await db.AuditEvents.SingleAsync(x => x.EventType == "identity.mfa.operator-recovery")).ActorId);
    }

    [Fact]
    public async Task Failed_audit_rolls_back_grants_and_session_revocation()
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await AdminAsync(driver);
        var target = await driver.SeedUserAsync("target", Password, ["system:probe"]);
        await using var db = driver.Database.CreateContext();
        var role = (await db.Set<UserRole>().SingleAsync(x => x.UserId == target)).RoleId;
        var token = await IdentityTestDriver.TokenAsync(driver.Client);
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION reject_role_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'audit fault'; END $$; CREATE TRIGGER reject_role_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION reject_role_audit();");
        using var request = IdentityTestDriver.Mutation(token, $"/api/v1/roles/{role}/permissions");
        request.Method = HttpMethod.Put;
        request.Content = JsonContent.Create(new { permissionIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await driver.Client.SendAsync(request)).StatusCode);
        Assert.Equal(0, (await db.Set<IdentityUser>().SingleAsync(x => x.Id == target)).SecurityVersion);
        Assert.Single(await db.Set<RolePermission>().Where(x => x.RoleId == role).ToListAsync());
    }

    internal static async Task<Guid> AdminAsync(IdentityTestDriver driver)
    {
        var id = await AccountProvisioningTests.BootstrapAsync(driver);
        Assert.Equal(HttpStatusCode.OK, (await driver.LoginAsync("admin", Password)).StatusCode);
        await ConfirmAsync(driver);
        return id;
    }
}
