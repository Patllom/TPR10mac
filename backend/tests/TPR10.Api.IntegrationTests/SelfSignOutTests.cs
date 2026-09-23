using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Identity.Data;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class SelfSignOutTests(PostgresFixture postgres)
{
    private const string Password = "self-signout-test-password";
    private const string Route = "/api/v1/auth/logout-all";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task User_can_revoke_own_sessions_only_without_admin_permissions(bool restricted)
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var actor = await driver.SeedUserAsync("actor", Password, [], mustChangePassword: restricted);
        var other = await driver.SeedUserAsync("other", Password, []);
        Assert.Equal(HttpStatusCode.OK, (await driver.LoginAsync("actor", Password)).StatusCode);
        using var second = driver.NewClient();
        using var otherClient = driver.NewClient();
        await LoginAsync(second, "actor");
        await LoginAsync(otherClient, "other");
        driver.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.NoContent, (await driver.PostAsync(Route, new { userId = other })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await driver.Client.GetAsync("/api/v1/auth/session")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/v1/auth/session")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await otherClient.GetAsync("/api/v1/auth/session")).StatusCode);
        await using var db = driver.Database.CreateContext();
        Assert.All(await db.Set<IdentitySession>().Where(s => s.UserId == actor).ToListAsync(), s => Assert.NotNull(s.RevokedAtUtc));
    }

    [Fact]
    public async Task Invalid_csrf_cannot_revoke_sessions()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await driver.SeedUserAsync("actor", Password, []);
        await driver.LoginAsync("actor", Password);
        using var invalid = IdentityTestDriver.Mutation("invalid", Route);
        Assert.Equal(HttpStatusCode.Forbidden, (await driver.Client.SendAsync(invalid)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await driver.Client.GetAsync("/api/v1/auth/session")).StatusCode);
    }

    [Fact]
    public async Task Audit_failure_rolls_back_all_revocations()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await driver.SeedUserAsync("actor", Password, []);
        await driver.LoginAsync("actor", Password);
        await using var db = driver.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_self_logout_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
              IF NEW.event_type = 'identity.logout-all' THEN RAISE EXCEPTION 'test audit unavailable'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER reject_self_logout_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION reject_self_logout_audit();
            """);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await driver.PostAsync(Route, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await driver.Client.GetAsync("/api/v1/auth/session")).StatusCode);
        Assert.Null((await db.Set<IdentitySession>().SingleAsync()).RevokedAtUtc);
        Assert.Equal(0, (await db.Set<IdentityUser>().SingleAsync()).SecurityVersion);
    }

    private static async Task LoginAsync(HttpClient client, string username)
    {
        using var request = IdentityTestDriver.Mutation(await IdentityTestDriver.TokenAsync(client), "/api/v1/auth/login");
        request.Content = JsonContent.Create(new { username, password = Password });
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
    }
}
