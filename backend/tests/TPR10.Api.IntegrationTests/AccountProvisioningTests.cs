using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.Data;
using TPR10.Api.Identity.Accounts;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Passwords;
using TPR10.Api.Identity.Sessions;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class AccountProvisioningTests(PostgresFixture postgres)
{
    private const string Password = "รหัสทดสอบยาวพอ-123456";

    [Theory]
    [InlineData(25, 25)]
    [InlineData(1000, 100)]
    public async Task List_has_bounded_pagination_and_safe_projection(int requested, int expected)
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var actor = await BootstrapAsync(driver);
        await using var db = driver.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("INSERT INTO users(id,username,normalized_username) SELECT gen_random_uuid(),'user-' || i,'USER-' || i FROM generate_series(1,110) AS i");
        var result = await Provisioning(driver, db).ListAsync(actor, 1, requested, default);
        Assert.Equal(200, Status(result));
        var page = Assert.IsType<AccountPage>(((IValueHttpResult)result).Value);
        Assert.Equal(111, page.Total);
        Assert.Equal(expected, page.Items.Length);
        Assert.Equal(expected, page.PageSize);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(page.Items));
        Assert.All(json.RootElement.EnumerateArray(), item => Assert.Equal(new[] { "Email", "Id", "IsActive", "RoleIds", "Username" },
            item.EnumerateObject().Select(x => x.Name).OrderBy(x => x).ToArray()));
        var second = Assert.IsType<AccountPage>(((IValueHttpResult)await Provisioning(driver, db).ListAsync(actor, 2, requested, default)).Value);
        Assert.Empty(page.Items.Select(x => x.Id).Intersect(second.Items.Select(x => x.Id)));
    }

    [Theory]
    [InlineData(0, 25)]
    [InlineData(1, 0)]
    [InlineData(int.MaxValue, 100)]
    public async Task Invalid_pagination_is_rejected(int page, int size)
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var actor = await BootstrapAsync(driver);
        await using var db = driver.Database.CreateContext();
        Assert.Equal(400, Status(await Provisioning(driver, db).ListAsync(actor, page, size, default)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Users_manage_alone_cannot_assign_roles_on_create_or_update(bool create)
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await BootstrapAsync(driver);
        var actor = await driver.SeedUserAsync("operator", Password, ["users:manage"]);
        var target = await driver.SeedUserAsync("staff", Password, []);
        await using var db = driver.Database.CreateContext();
        var service = Provisioning(driver, db);
        var result = create ? await service.CreateAsync(actor, new("escalated", Password, RoleIds: [IdentityCatalog.AdministratorRoleId]), default)
            : await service.UpdateAsync(actor, target, new(RoleIds: [IdentityCatalog.AdministratorRoleId]), default);
        Assert.Equal(403, Status(result));
        Assert.Equal(3, await db.Set<IdentityUser>().CountAsync());
        Assert.DoesNotContain(await db.Set<UserRole>().Where(x => x.UserId == target).Select(x => x.RoleId).ToListAsync(), x => x == IdentityCatalog.AdministratorRoleId);
    }

    [Fact]
    public async Task Admin_created_account_requires_password_change_and_returns_no_secrets()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var actor = await BootstrapAsync(driver);
        await using var db = driver.Database.CreateContext();
        var result = await Provisioning(driver, db).CreateAsync(actor, new("Staff", Password, "staff@example.test"), default);
        Assert.Equal(201, Status(result));
        var account = Assert.IsType<AccountView>(((IValueHttpResult)result).Value);
        Assert.Equal("Staff", account.Username);
        Assert.True((await db.Set<LocalCredential>().SingleAsync(x => x.UserId == account.Id)).MustChangePassword);
        var body = JsonSerializer.Serialize(account);
        Assert.DoesNotContain(Password, body);
        Assert.DoesNotContain("PasswordHash", body);
        var audit = await db.AuditEvents.SingleAsync(x => x.EventType == "identity.user.created");
        Assert.Equal(actor, audit.ActorId);
        Assert.Equal(account.Id, audit.TargetId);
        using var login = await driver.LoginAsync("staff", Password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        Assert.Equal("PasswordChangeRequired", json.RootElement.GetProperty("stage").GetString());
    }

    [Fact]
    public async Task Normalized_duplicate_is_409_without_extra_user_or_audit()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var actor = await BootstrapAsync(driver);
        await using var db = driver.Database.CreateContext();
        var service = Provisioning(driver, db);
        Assert.Equal(201, Status(await service.CreateAsync(actor, new("Staff", Password), default)));
        Assert.Equal(409, Status(await service.CreateAsync(actor, new(" ｓｔａｆｆ ", Password), default)));
        Assert.Equal(2, await db.Set<IdentityUser>().CountAsync());
        Assert.Single(await db.AuditEvents.Where(x => x.EventType == "identity.user.created").ToListAsync());
    }

    [Theory]
    [InlineData("", Password, null)]
    [InlineData("staff", "short", null)]
    [InlineData("staff", Password, "not-an-email")]
    [InlineData("staff\nadmin", Password, null)]
    public async Task Invalid_fields_are_400_without_mutation(string username, string password, string? email)
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var actor = await BootstrapAsync(driver);
        await using var db = driver.Database.CreateContext();
        Assert.Equal(400, Status(await Provisioning(driver, db).CreateAsync(actor, new(username, password, email), default)));
        Assert.Equal(1, await db.Set<IdentityUser>().CountAsync());
        Assert.False(await db.AuditEvents.AnyAsync(x => x.EventType == "identity.user.created"));
    }

    [Fact]
    public async Task Disabled_account_revokes_sessions_and_cannot_login()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var actor = await BootstrapAsync(driver);
        var staff = await driver.SeedUserAsync("staff", Password, []);
        Assert.Equal(HttpStatusCode.OK, (await driver.LoginAsync("staff", Password)).StatusCode);
        await using var db = driver.Database.CreateContext();
        Assert.Equal(200, Status(await Provisioning(driver, db).UpdateAsync(actor, staff, new(IsActive: false), default)));
        Assert.False((await db.Set<IdentityUser>().SingleAsync(x => x.Id == staff)).IsActive);
        Assert.Equal(1, (await db.Set<IdentityUser>().SingleAsync(x => x.Id == staff)).SecurityVersion);
        Assert.NotNull((await db.Set<IdentitySession>().SingleAsync()).RevokedAtUtc);
        Assert.Equal(HttpStatusCode.Unauthorized, (await driver.Client.GetAsync("/api/v1/auth/session")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await driver.LoginAsync("staff", Password)).StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Last_active_administrator_cannot_be_disabled_or_demoted(bool disable)
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var actor = await BootstrapAsync(driver);
        await using var db = driver.Database.CreateContext();
        var request = disable ? new UpdateAccountRequest(IsActive: false) : new UpdateAccountRequest(RoleIds: [IdentityCatalog.StaffRoleId]);
        Assert.Equal(409, Status(await Provisioning(driver, db).UpdateAsync(actor, actor, request, default)));
        Assert.True((await db.Set<IdentityUser>().SingleAsync()).IsActive);
        Assert.Contains(await db.Set<UserRole>().Select(x => x.RoleId).ToListAsync(), id => id == IdentityCatalog.AdministratorRoleId);
    }

    [Fact]
    public async Task Actor_without_named_permission_is_denied()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await BootstrapAsync(driver);
        var staff = await driver.SeedUserAsync("staff", Password, []);
        await using var db = driver.Database.CreateContext();
        Assert.Equal(403, Status(await Provisioning(driver, db).CreateAsync(staff, new("intruder", Password), default)));
        Assert.Equal(2, await db.Set<IdentityUser>().CountAsync());
    }

    internal static int? Status(IResult result) => ((IStatusCodeHttpResult)result).StatusCode;
    internal static AccountProvisioning Provisioning(IdentityTestDriver driver, Tpr10DbContext db) =>
        new(db, new ArgonPasswordHasher(), new SessionService(db, driver.Clock, new RequestSession()), Audit(driver, db), driver.Clock);
    internal static AuditEventWriter Audit(IdentityTestDriver driver, Tpr10DbContext db)
    {
        var correlation = new CorrelationContext();
        correlation.Initialize(Guid.NewGuid());
        return new AuditEventWriter(db, correlation, driver.Clock);
    }
    internal static async Task<Guid> BootstrapAsync(IdentityTestDriver driver)
    {
        await using var db = driver.Database.CreateContext();
        Assert.Equal(0, await new BootstrapService(db, new ArgonPasswordHasher(), Audit(driver, db), driver.Clock).CreateAsync("admin", Password, default));
        return (await db.Set<IdentityUser>().SingleAsync()).Id;
    }
}
