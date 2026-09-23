using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Identity.Accounts;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Passwords;
using static TPR10.Api.IntegrationTests.AccountProvisioningTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class AccountBoundaryTests(PostgresFixture postgres)
{
    private const string Password = "รหัสทดสอบยาวพอ-123456";

    [Fact]
    public async Task Adapter_defaults_to_25_no_store_and_requires_actor()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var actor = await BootstrapAsync(driver);
        await using var db = driver.Database.CreateContext();
        var context = new DefaultHttpContext();
        Assert.Equal(401, Status(await AccountEndpoints.ListAsync(context, Provisioning(driver, db), null, null, default)));
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor.ToString())], "test"));
        var result = await AccountEndpoints.ListAsync(context, Provisioning(driver, db), null, null, default);
        var page = Assert.IsType<AccountPage>(((IValueHttpResult)result).Value);
        Assert.Equal(1, page.Page);
        Assert.Equal(25, page.PageSize);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        using var scope = driver.Factory.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AccountProvisioning>());
    }

    [Fact]
    public async Task Adapter_routes_carry_named_policy_but_are_not_mapped_in_real_hosts()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddScoped<AccountProvisioning>();
        await using var app = builder.Build();
        app.MapAccountEndpoints();
        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(x => x.Endpoints).ToArray();
        Assert.Equal(3, routes.Length);
        Assert.All(routes, route => Assert.Contains(route.Metadata.GetOrderedMetadata<IAuthorizeData>(), x => x.Policy == "users:manage"));
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        using var keys = new TestKeyMaterial();
        foreach (var environment in new[] { "Testing", "Production" })
        {
            await using var factory = new ApiFactory(driver.Database.ConnectionString, environment, settings: keys.Settings);
            using var client = await factory.CreateCsrfClientAsync();
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/users")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/v1/users", new { username = "intruder", password = Password })).StatusCode);
        }
    }

    [Theory]
    [InlineData("bootstrap")]
    [InlineData("create")]
    [InlineData("update")]
    public async Task Audit_failure_rolls_back_entire_account_transaction(string operation)
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var actor = operation == "bootstrap" ? Guid.Empty : await BootstrapAsync(driver);
        var target = operation == "update" ? await driver.SeedUserAsync("staff", Password, []) : Guid.Empty;
        if (operation == "update") Assert.Equal(HttpStatusCode.OK, (await driver.LoginAsync("staff", Password)).StatusCode);
        await using (var db = driver.Database.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION fail_account_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'test audit unavailable'; END $$; CREATE TRIGGER fail_account_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION fail_account_audit();");
            await Assert.ThrowsAsync<DbUpdateException>(async () =>
            {
                if (operation == "bootstrap") await new BootstrapService(db, new ArgonPasswordHasher(), Audit(driver, db), driver.Clock).CreateAsync("admin", Password, default);
                else if (operation == "create") await Provisioning(driver, db).CreateAsync(actor, new("staff", Password), default);
                else await Provisioning(driver, db).UpdateAsync(actor, target, new(IsActive: false), default);
            });
        }
        await using var verify = driver.Database.CreateContext();
        Assert.Equal(operation == "bootstrap" ? 0 : operation == "create" ? 1 : 2, await verify.Set<IdentityUser>().CountAsync());
        if (operation == "bootstrap") Assert.Empty(await verify.Set<IdentityRole>().ToListAsync());
        if (operation == "update")
        {
            var user = await verify.Set<IdentityUser>().SingleAsync(x => x.Id == target);
            Assert.True(user.IsActive);
            Assert.Equal(0, user.SecurityVersion);
            Assert.Null((await verify.Set<IdentitySession>().SingleAsync()).RevokedAtUtc);
            Assert.Equal(HttpStatusCode.OK, (await driver.Client.GetAsync("/api/v1/auth/session")).StatusCode);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Concurrent_self_disable_or_demotion_preserves_one_admin(bool disable)
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var first = await BootstrapAsync(driver);
        await using var setup = driver.Database.CreateContext();
        var created = await Provisioning(driver, setup).CreateAsync(first, new("second", Password, RoleIds: [IdentityCatalog.AdministratorRoleId]), default);
        var second = Assert.IsType<AccountView>(((IValueHttpResult)created).Value).Id;
        var request = disable ? new UpdateAccountRequest(IsActive: false) : new UpdateAccountRequest(RoleIds: [IdentityCatalog.StaffRoleId]);
        async Task<int?> Change(Guid id)
        {
            await using var db = driver.Database.CreateContext();
            return Status(await Provisioning(driver, db).UpdateAsync(id, id, request, default));
        }
        var results = await Task.WhenAll(Change(first), Change(second));
        Assert.Equal(new int?[] { 200, 409 }, results.OrderBy(x => x).ToArray());
        await using var verify = driver.Database.CreateContext();
        Assert.Equal(1, await (from u in verify.Set<IdentityUser>()
                               join ur in verify.Set<UserRole>() on u.Id equals ur.UserId
                               where u.IsActive && ur.RoleId == IdentityCatalog.AdministratorRoleId
                               select u.Id).CountAsync());
    }

    [Fact]
    public async Task Concurrent_normalized_creates_have_one_winner()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var actor = await BootstrapAsync(driver);
        async Task<int?> Create(string name)
        {
            await using var db = driver.Database.CreateContext();
            return Status(await Provisioning(driver, db).CreateAsync(actor, new(name, Password), default));
        }
        Assert.Equal(new int?[] { 201, 409 }, (await Task.WhenAll(Create("staff"), Create(" ＳＴＡＦＦ "))).OrderBy(x => x).ToArray());
        Assert.Equal(2, await driver.CountAsync("users"));
    }

    [Fact]
    public async Task Role_change_revokes_session_and_updates_version()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var actor = await BootstrapAsync(driver);
        var target = await driver.SeedUserAsync("staff", Password, []);
        Assert.Equal(HttpStatusCode.OK, (await driver.LoginAsync("staff", Password)).StatusCode);
        await using var db = driver.Database.CreateContext();
        Assert.Equal(200, Status(await Provisioning(driver, db).UpdateAsync(actor, target, new(RoleIds: [IdentityCatalog.StaffRoleId]), default)));
        Assert.Equal(1, (await db.Set<IdentityUser>().SingleAsync(x => x.Id == target)).SecurityVersion);
        Assert.NotNull((await db.Set<IdentitySession>().SingleAsync()).RevokedAtUtc);
        Assert.Equal(HttpStatusCode.Unauthorized, (await driver.Client.GetAsync("/api/v1/auth/session")).StatusCode);
    }
}
