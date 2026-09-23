using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Identity.Data;
using static TPR10.Api.IntegrationTests.MfaTests;
using static TPR10.Api.IntegrationTests.MfaSecurityTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class MfaResilienceTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(-30)]
    [InlineData(30)]
    public async Task Adjacent_time_step_is_accepted_once(int driftSeconds)
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var (secret, _) = await EnrollConfirmedAsync(driver);
        driver.Advance(TimeSpan.FromMinutes(2));
        var code = Code(secret, driver.Clock.GetUtcNow().AddSeconds(driftSeconds));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(driver.Client, "challenge", code)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(driver.Client, "challenge", code)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_confirmation_issues_one_recovery_batch_and_one_new_session()
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await driver.SeedUserAsync("admin", Password, [], requiresMfa: true);
        var login = await driver.LoginAsync("admin", Password);
        var cookie = SessionCookie(login);
        var secret = Secret(await (await driver.PostAsync("/api/v1/auth/mfa/enroll", new { })).Content.ReadAsStringAsync());
        using var one = driver.NewClient(); using var two = driver.NewClient();
        one.DefaultRequestHeaders.Add("Cookie", cookie); two.DefaultRequestHeaders.Add("Cookie", cookie);
        var replies = await Task.WhenAll(PostAsync(one, "confirm", Code(secret, driver.Clock.GetUtcNow())), PostAsync(two, "confirm", Code(secret, driver.Clock.GetUtcNow())));
        Assert.Single(replies, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(replies, x => x.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
        await using var db = driver.Database.CreateContext();
        Assert.Equal(10, await db.Set<MfaRecoveryCode>().CountAsync());
        Assert.Single(await db.Set<IdentitySession>().Where(x => x.RevokedAtUtc == null).ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_with_same_keys_works_but_lost_keys_fail_closed(bool lost)
    {
        using var keys = new TestKeyMaterial(); using var newKeys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var (secret, _) = await EnrollConfirmedAsync(driver);
        driver.Advance(TimeSpan.FromSeconds(30));
        await using var restarted = new ApiFactory(driver.Database.ConnectionString, clock: driver.Clock, settings: lost ? newKeys.Settings : keys.Settings);
        using var client = restarted.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443") });
        await LoginAsync(client);
        var response = await PostAsync(client, "challenge", Code(secret, driver.Clock.GetUtcNow()));
        Assert.Equal(lost ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task Ephemeral_keyring_cannot_persist_an_mfa_factor()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await driver.SeedUserAsync("admin", Password, [], requiresMfa: true);
        await driver.LoginAsync("admin", Password);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await driver.PostAsync("/api/v1/auth/mfa/enroll", new { })).StatusCode);
        await using var db = driver.Database.CreateContext();
        Assert.Empty(await db.Set<MfaFactor>().ToListAsync());
    }

    [Fact]
    public async Task Enrollment_is_bound_to_session_and_can_be_restarted_after_expiry()
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await driver.SeedUserAsync("admin", Password, [], requiresMfa: true);
        await driver.LoginAsync("admin", Password);
        var enrolled = await driver.PostAsync("/api/v1/auth/mfa/enroll", new { });
        var secret = Secret(await enrolled.Content.ReadAsStringAsync());
        using var other = driver.NewClient(); await LoginAsync(other);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(other, "confirm", Code(secret, driver.Clock.GetUtcNow()))).StatusCode);
        driver.Advance(TimeSpan.FromMinutes(11));
        using var fresh = driver.NewClient(); await LoginAsync(fresh);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(fresh, "enroll", "")).StatusCode);
        await using var db = driver.Database.CreateContext();
        Assert.Equal(1, await db.Set<MfaFactor>().CountAsync(x => x.RevokedAtUtc == null));
        Assert.Equal(1, await db.Set<MfaFactor>().CountAsync(x => x.RevokedAtUtc != null));
    }

    [Theory]
    [InlineData("confirm")]
    [InlineData("challenge")]
    [InlineData("recover")]
    public async Task Audit_failure_rolls_back_step_consumption_factor_and_session(string action)
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        string secret; string code;
        if (action == "confirm")
        {
            await driver.SeedUserAsync("admin", Password, [], requiresMfa: true);
            await driver.LoginAsync("admin", Password);
            secret = Secret(await (await driver.PostAsync("/api/v1/auth/mfa/enroll", new { })).Content.ReadAsStringAsync());
            code = Code(secret, driver.Clock.GetUtcNow());
        }
        else
        {
            var enrolled = await EnrollConfirmedAsync(driver);
            secret = enrolled.Secret;
            driver.Advance(TimeSpan.FromSeconds(30));
            code = action == "recover" ? enrolled.Codes[0] : Code(secret, driver.Clock.GetUtcNow());
        }
        await using var db = driver.Database.CreateContext();
        var previous = await db.Set<MfaFactor>().AsNoTracking().SingleAsync();
        var sessionCount = await db.Set<IdentitySession>().CountAsync();
        var userVersion = (await db.Set<IdentityUser>().AsNoTracking().SingleAsync()).SecurityVersion;
        var token = await IdentityTestDriver.TokenAsync(driver.Client);
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION fail_mfa_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'test audit unavailable'; END $$; CREATE TRIGGER fail_mfa_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION fail_mfa_audit();");
        using var request = IdentityTestDriver.Mutation(token, "/api/v1/auth/mfa/" + action);
        request.Content = JsonContent.Create(new { code });
        var response = await driver.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var after = await db.Set<MfaFactor>().AsNoTracking().SingleAsync();
        Assert.Equal(previous.LastUsedStep, after.LastUsedStep);
        Assert.Equal(previous.ConfirmedAtUtc, after.ConfirmedAtUtc);
        Assert.Null(after.RevokedAtUtc);
        Assert.Equal(sessionCount, await db.Set<IdentitySession>().CountAsync());
        Assert.Equal(userVersion, (await db.Set<IdentityUser>().AsNoTracking().SingleAsync()).SecurityVersion);
        Assert.False(await db.Set<MfaRecoveryCode>().AnyAsync(x => x.ConsumedAtUtc != null || x.RevokedAtUtc != null));
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_mfa_audit ON audit_events");
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(driver.Client, action, code)).StatusCode);
    }

    [Fact]
    public async Task Old_csrf_after_confirmation_and_missing_csrf_cannot_mutate()
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await driver.SeedUserAsync("admin", Password, [], requiresMfa: true);
        await driver.LoginAsync("admin", Password);
        var stale = await IdentityTestDriver.TokenAsync(driver.Client);
        using var missing = await driver.Client.PostAsJsonAsync("/api/v1/auth/mfa/enroll", new { });
        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        var secret = Secret(await (await driver.PostAsync("/api/v1/auth/mfa/enroll", new { })).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(driver.Client, "confirm", Code(secret, driver.Clock.GetUtcNow()))).StatusCode);
        driver.Advance(TimeSpan.FromSeconds(30));
        using var request = IdentityTestDriver.Mutation(stale, "/api/v1/auth/mfa/challenge");
        request.Content = JsonContent.Create(new { code = Code(secret, driver.Clock.GetUtcNow()) });
        Assert.Equal(HttpStatusCode.Forbidden, (await driver.Client.SendAsync(request)).StatusCode);
    }
}
