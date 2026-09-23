using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Identity.Accounts;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Sessions;
using static TPR10.Api.IntegrationTests.MfaTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class AuthorizationRaceTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Concurrent_grant_removal_leaves_one_effective_administrator()
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await RoleAuthorizationTests.AdminAsync(driver);
        var second = await driver.SeedUserAsync("second", Password, ["users:manage", "roles:manage"], requiresMfa: true);
        using var client = driver.NewClient();
        using var login = IdentityTestDriver.Mutation(await IdentityTestDriver.TokenAsync(client), "/api/v1/auth/login");
        login.Content = JsonContent.Create(new { username = "second", password = Password });
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(login)).StatusCode);
        using var enroll = await MfaSecurityTests.PostAsync(client, "enroll", "");
        var secret = Secret(await enroll.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await MfaSecurityTests.PostAsync(client, "confirm", Code(secret, driver.Clock.GetUtcNow()))).StatusCode);
        await using var db = driver.Database.CreateContext();
        var secondRole = (await db.Set<UserRole>().SingleAsync(x => x.UserId == second)).RoleId;
        async Task<int> Remove(HttpClient actorClient, Guid role)
        {
            using var request = IdentityTestDriver.Mutation(await IdentityTestDriver.TokenAsync(actorClient), $"/api/v1/roles/{role}/permissions");
            request.Method = HttpMethod.Put;
            request.Content = JsonContent.Create(new { permissionIds = Array.Empty<Guid>() });
            return (int)(await actorClient.SendAsync(request)).StatusCode;
        }
        Assert.Equal(new[] { 204, 409 }, (await Task.WhenAll(Remove(driver.Client, IdentityCatalog.AdministratorRoleId), Remove(client, secondRole))).OrderBy(x => x).ToArray());
        Assert.True(await TPR10.Api.Identity.Authorization.PermissionMutationGuard.HasManagingAdminAsync(db, default));
        Assert.Equal(1, await db.Set<IdentityUser>().CountAsync(x => x.SecurityVersion == 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Account_mutation_rechecks_actor_session_after_authorization_snapshot(bool update)
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(driver);
        var target = await driver.SeedUserAsync("target", Password, []);
        await using var db = driver.Database.CreateContext();
        var session = await db.Set<IdentitySession>().AsNoTracking().SingleAsync(x => x.UserId == actor && x.RevokedAtUtc == null);
        using var scope = driver.Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<RequestSession>().Entity = session;
        // Revoke after request authentication, before the mutation acquires its identity lock.
        await db.Set<IdentitySession>().Where(x => x.Id == session.Id).ExecuteUpdateAsync(x => x.SetProperty(s => s.RevokedAtUtc, driver.Clock.GetUtcNow()));
        var service = scope.ServiceProvider.GetRequiredService<AccountProvisioning>();
        var result = update ? await service.UpdateAsync(actor, target, new(IsActive: false), default)
            : await service.CreateAsync(actor, new("intruder", Password), default);
        Assert.Equal(403, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Equal(2, await db.Set<IdentityUser>().CountAsync());
        Assert.True((await db.Set<IdentityUser>().SingleAsync(x => x.Id == target)).IsActive);
    }

    [Fact]
    public async Task Patch_user_roles_cannot_bypass_last_effective_admin_invariant()
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(driver);
        await driver.SeedUserAsync("unprivileged-admin-class", Password, [], requiresMfa: true);
        using var response = await AuthorizationTests.SendAsync(driver, HttpMethod.Patch, $"/api/v1/users/{actor}", new { roleIds = new[] { IdentityCatalog.StaffRoleId } });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Role_assignment_preserves_last_admin_class_even_if_management_is_delegated_to_staff()
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var target = await driver.SeedUserAsync("admin-class", Password, [], requiresMfa: true);
        await driver.SeedUserAsync("operator", Password, ["roles:manage"]);
        Assert.Equal(HttpStatusCode.OK, (await driver.LoginAsync("operator", Password)).StatusCode);
        await AuthorizationTests.ConfirmAsync(driver);
        using var response = await AuthorizationTests.SendAsync(driver, HttpMethod.Put, $"/api/v1/users/{target}/roles", new { roleIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
