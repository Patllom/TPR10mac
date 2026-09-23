using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Identity.Data;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class LoginTests(PostgresFixture postgres)
{
    private const string Password = "รหัสทดสอบยาวพอ-123456";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Identifier_capacity_is_bounded_and_expired_entries_reclaimed(bool expired)
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await using var db = driver.Database.CreateContext();
        var now = driver.Clock.GetUtcNow();
        var expiry = expired ? now.AddMinutes(-1) : now.AddMinutes(30);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO login_attempt_windows(identifier_hash,failed_attempts,window_started_at_utc,expires_at_utc) SELECT decode(lpad(to_hex(i),64,'0'),'hex'),0,{now},{expiry} FROM generate_series(1,10000) AS i");
        using var response = await driver.LoginAsync("unknown", Password);
        Assert.Equal(expired ? HttpStatusCode.Unauthorized : HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(expired ? 1 : 10000, await db.Set<LoginAttemptWindow>().CountAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Five_failures_lock_both_existing_and_unknown_identifiers_then_expire(bool exists)
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString,
            new() { ["Identity:Csrf:PerIpPermitLimit"] = "100" });
        if (exists) await driver.SeedUserAsync("staff", Password, []);
        var csrf = await IdentityTestDriver.TokenAsync(driver.Client);
        for (var i = 0; i < 5; i++)
        {
            using var request = IdentityTestDriver.Mutation(csrf, "/api/v1/auth/login");
            request.Content = JsonContent.Create(new { username = "staff", password = Password + "wrong" });
            using var response = await driver.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains("ไม่สามารถเข้าสู่ระบบได้", await response.Content.ReadAsStringAsync());
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
        using var locked = await driver.LoginAsync(" STAFF ", Password);
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.NotNull(locked.Headers.RetryAfter);
        Assert.True(locked.Headers.CacheControl?.NoStore);
        driver.Advance(TimeSpan.FromMinutes(16));
        using var retry = await driver.LoginAsync("staff", Password);
        Assert.Equal(exists ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, retry.StatusCode);
    }

    [Fact]
    public async Task Failures_are_audited_without_password_or_username_and_disabled_is_generic()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await driver.SeedUserAsync("staff", Password, []);
        await using var db = driver.Database.CreateContext();
        (await db.Set<IdentityUser>().SingleAsync()).IsActive = false;
        await db.SaveChangesAsync();
        using var failed = await driver.LoginAsync("staff", Password);
        Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        Assert.Empty(await db.Set<IdentitySession>().ToListAsync());
        Assert.Contains(await db.AuditEvents.Select(x => x.EventType).ToListAsync(), x => x == "identity.login.failed");
        Assert.DoesNotContain(await db.AuditMetadata.Select(x => x.Value).ToListAsync(), x => x.Contains(Password) || x.Contains("staff"));
    }

    [Fact]
    public async Task Successful_login_audit_has_actor()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var user = await driver.SeedUserAsync("staff", Password, []);
        using var login = await driver.LoginAsync("staff", Password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await using var db = driver.Database.CreateContext();
        Assert.Equal(user, (await db.AuditEvents.SingleAsync(x => x.EventType == "identity.login")).ActorId);
    }

    [Fact]
    public async Task Concurrent_failures_are_counted_and_lock_survives_host_restart()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await driver.SeedUserAsync("staff", Password, []);
        var csrf = await IdentityTestDriver.TokenAsync(driver.Client);
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            using var request = IdentityTestDriver.Mutation(csrf, "/api/v1/auth/login");
            request.Content = JsonContent.Create(new { username = "staff", password = Password + "wrong" });
            return await driver.Client.SendAsync(request);
        }));
        foreach (var response in responses) { Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); response.Dispose(); }
        await using var db = driver.Database.CreateContext();
        Assert.Equal(5, (await db.Set<LocalCredential>().SingleAsync()).FailedAttempts);
        await using var restart = new ApiFactory(driver.Database.ConnectionString, clock: driver.Clock);
        using var client = restart.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443") });
        using var login = IdentityTestDriver.Mutation(await IdentityTestDriver.TokenAsync(client), "/api/v1/auth/login");
        login.Content = JsonContent.Create(new { username = "staff", password = Password });
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(login)).StatusCode);
    }

    [Fact]
    public async Task Same_pre_auth_flow_cannot_issue_two_sessions_concurrently()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await driver.SeedUserAsync("staff", Password, []);
        var csrf = await IdentityTestDriver.TokenAsync(driver.Client);
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            using var request = IdentityTestDriver.Mutation(csrf, "/api/v1/auth/login");
            request.Content = JsonContent.Create(new { username = "staff", password = Password });
            return await driver.Client.SendAsync(request);
        }));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Forbidden);
        foreach (var response in responses) response.Dispose();
        await using var db = driver.Database.CreateContext();
        Assert.Single(await db.Set<IdentitySession>().ToListAsync());
    }

    [Fact]
    public async Task Login_audit_failure_rolls_back_session_and_pre_auth_consumption()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await driver.SeedUserAsync("staff", Password, []);
        await using var db = driver.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_login_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
              IF NEW.event_type = 'identity.login' THEN RAISE EXCEPTION 'test audit unavailable'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER reject_login_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION reject_login_audit();
            """);
        using var response = await driver.LoginAsync("staff", Password);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Empty(await db.Set<IdentitySession>().ToListAsync());
        Assert.Null((await db.Set<PreAuthFlow>().SingleAsync()).ConsumedAtUtc);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(x => x.StartsWith("__Host-tpr10_session=")));
    }
}
