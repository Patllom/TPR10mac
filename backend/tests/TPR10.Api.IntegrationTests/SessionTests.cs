using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Identity.Data;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class SessionTests(PostgresFixture postgres)
{
    private const string Password = "รหัสทดสอบยาวพอ-123456";

    [Theory]
    [InlineData("password")]
    [InlineData("mandatory-mfa")]
    [InlineData("optional-mfa")]
    public async Task Newly_required_assurance_cannot_keep_old_active_session(string change)
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var user = await driver.SeedUserAsync("staff", Password, []);
        Assert.Equal(HttpStatusCode.OK, (await driver.LoginAsync("staff", Password)).StatusCode);
        await using var db = driver.Database.CreateContext();
        if (change == "password") (await db.Set<LocalCredential>().SingleAsync()).MustChangePassword = true;
        if (change == "mandatory-mfa") (await db.Set<IdentityRole>().SingleAsync()).RoleClass = "accounting";
        if (change == "optional-mfa") db.Add(new MfaFactor
        {
            Id = Guid.NewGuid(),
            UserId = user,
            ProtectedSecret = "test-protected-placeholder",
            CreatedAtUtc = driver.Clock.GetUtcNow(),
            ConfirmedAtUtc = driver.Clock.GetUtcNow()
        });
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await driver.Client.GetAsync("/api/v1/auth/session")).StatusCode);
    }

    [Fact]
    public async Task Invalid_csrf_does_not_extend_idle_timeout_and_logout_failure_preserves_session()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await driver.SeedUserAsync("staff", Password, []);
        Assert.Equal(HttpStatusCode.OK, (await driver.LoginAsync("staff", Password)).StatusCode);
        driver.Advance(TimeSpan.FromMinutes(29));
        using var invalid = IdentityTestDriver.Mutation("invalid", "/api/v1/auth/logout");
        Assert.Equal(HttpStatusCode.Forbidden, (await driver.Client.SendAsync(invalid)).StatusCode);
        driver.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await driver.Client.GetAsync("/api/v1/auth/session")).StatusCode);
    }

    [Fact]
    public async Task Logout_audit_failure_preserves_authoritative_session()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await driver.SeedUserAsync("staff", Password, []);
        Assert.Equal(HttpStatusCode.OK, (await driver.LoginAsync("staff", Password)).StatusCode);
        await using var db = driver.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_logout_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
              IF NEW.event_type = 'identity.logout' THEN RAISE EXCEPTION 'test audit unavailable'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER reject_logout_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION reject_logout_audit();
            """);
        using var response = await driver.PostAsync("/api/v1/auth/logout", new { });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Null((await db.Set<IdentitySession>().SingleAsync()).RevokedAtUtc);
        Assert.Equal(HttpStatusCode.OK, (await driver.Client.GetAsync("/api/v1/auth/session")).StatusCode);
    }

    [Theory]
    [InlineData(true, false, false, "PasswordChangeRequired")]
    [InlineData(false, true, false, "MfaEnrollmentRequired")]
    [InlineData(false, true, true, "MfaChallengeRequired")]
    [InlineData(false, false, true, "MfaChallengeRequired")]
    public async Task Restricted_stages_have_no_business_permissions(bool change, bool mandatory, bool confirmed, string expected)
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var user = await driver.SeedUserAsync("staff", Password, ["profile:read"], mandatory, change);
        if (confirmed)
        {
            await using var db = driver.Database.CreateContext();
            db.Add(new MfaFactor
            {
                Id = Guid.NewGuid(),
                UserId = user,
                ProtectedSecret = "test-protected-placeholder",
                CreatedAtUtc = driver.Clock.GetUtcNow(),
                ConfirmedAtUtc = driver.Clock.GetUtcNow()
            });
            await db.SaveChangesAsync();
        }
        using var response = await driver.LoginAsync("staff", Password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, body.RootElement.GetProperty("stage").GetString());
        Assert.Equal(0, body.RootElement.GetProperty("permissions").GetArrayLength());
    }

    [Fact]
    public async Task Activity_renews_idle_but_never_absolute_expiry()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await driver.SeedUserAsync("staff", Password, []);
        Assert.Equal(HttpStatusCode.OK, (await driver.LoginAsync("staff", Password)).StatusCode);
        for (var i = 0; i < 23; i++)
        {
            driver.Advance(TimeSpan.FromMinutes(20));
            Assert.Equal(HttpStatusCode.OK, (await driver.Client.GetAsync("/api/v1/auth/session")).StatusCode);
        }
        driver.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(HttpStatusCode.Unauthorized, (await driver.Client.GetAsync("/api/v1/auth/session")).StatusCode);
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("version")]
    [InlineData("revoke")]
    public async Task Authority_changes_in_database_reject_cookie(string change)
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await driver.SeedUserAsync("staff", Password, []);
        Assert.Equal(HttpStatusCode.OK, (await driver.LoginAsync("staff", Password)).StatusCode);
        await using var db = driver.Database.CreateContext();
        var user = await db.Set<IdentityUser>().SingleAsync();
        if (change == "disable") user.IsActive = false;
        if (change == "version") user.SecurityVersion++;
        if (change == "revoke") (await db.Set<IdentitySession>().SingleAsync()).RevokedAtUtc = driver.Clock.GetUtcNow();
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await driver.Client.GetAsync("/api/v1/auth/session")).StatusCode);
    }

    [Fact]
    public async Task Permissions_are_read_again_not_cached_in_cookie()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await driver.SeedUserAsync("staff", Password, ["profile:read"]);
        Assert.Equal(HttpStatusCode.OK, (await driver.LoginAsync("staff", Password)).StatusCode);
        await using var db = driver.Database.CreateContext();
        await db.Set<RolePermission>().ExecuteDeleteAsync();
        using var response = await driver.Client.GetAsync("/api/v1/auth/session");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, body.RootElement.GetProperty("permissions").GetArrayLength());
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB")]
    public async Task Malformed_cookie_is_unauthorized_without_database(string token)
    {
        await using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=unused;Username=test;Timeout=1");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443"), HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", "__Host-tpr10_session=" + token);
        using var response = await client.GetAsync("/api/v1/auth/session");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Old_pre_auth_and_cross_session_csrf_are_rejected()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await driver.SeedUserAsync("staff", Password, []);
        var old = await IdentityTestDriver.TokenAsync(driver.Client);
        Assert.Equal(HttpStatusCode.OK, (await driver.LoginAsync("staff", Password)).StatusCode);
        using var oldRequest = IdentityTestDriver.Mutation(old, "/api/v1/auth/logout");
        Assert.Equal(HttpStatusCode.Forbidden, (await driver.Client.SendAsync(oldRequest)).StatusCode);
        var first = await IdentityTestDriver.TokenAsync(driver.Client);
        using var second = driver.NewClient();
        using var login = IdentityTestDriver.Mutation(await IdentityTestDriver.TokenAsync(second), "/api/v1/auth/login");
        login.Content = System.Net.Http.Json.JsonContent.Create(new { username = "staff", password = Password });
        Assert.Equal(HttpStatusCode.OK, (await second.SendAsync(login)).StatusCode);
        using var cross = IdentityTestDriver.Mutation(first, "/api/v1/auth/logout");
        Assert.Equal(HttpStatusCode.Forbidden, (await second.SendAsync(cross)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/v1/auth/session")).StatusCode);
    }

    [Fact]
    public async Task Database_unavailable_fails_closed_without_cache_or_connection_details()
    {
        await using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=unused;Username=test;Timeout=1");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443"), HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", "__Host-tpr10_session=" + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)));
        using var response = await client.GetAsync("/api/v1/auth/session");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.DoesNotContain("127.0.0.1", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Logout_invalidates_the_old_cookie()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await driver.SeedUserAsync("staff", Password, []);
        using var login = await driver.LoginAsync("staff", Password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("__Host-tpr10_session=")).Split(';')[0];
        Assert.Equal(HttpStatusCode.OK, (await driver.Client.GetAsync("/api/v1/auth/session")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await driver.PostAsync("/api/v1/auth/logout", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await driver.Client.GetAsync("/api/v1/auth/session")).StatusCode);
        using var replay = driver.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443"), HandleCookies = false });
        replay.DefaultRequestHeaders.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.GetAsync("/api/v1/auth/session")).StatusCode);
        await using var db = driver.Database.CreateContext();
        Assert.NotNull((await db.Set<IdentitySession>().SingleAsync()).RevokedAtUtc);
        Assert.Contains(await db.AuditEvents.Select(x => x.EventType).ToListAsync(), x => x == "identity.logout");
    }

    [Fact]
    public async Task Login_returns_named_stage_and_secure_cookie_but_persists_only_hash()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var id = await driver.SeedUserAsync("staff", Password, ["profile:read"]);
        using var response = await driver.LoginAsync("staff", Password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("__Host-tpr10_session="));
        Assert.Contains("secure", cookie.ToLowerInvariant());
        Assert.Contains("httponly", cookie.ToLowerInvariant());
        Assert.Contains("samesite=lax", cookie.ToLowerInvariant());
        Assert.Contains("path=/", cookie);
        Assert.DoesNotContain("domain=", cookie.ToLowerInvariant());
        var token = cookie.Split(';')[0].Split('=')[1];
        Assert.Equal(32, WebEncoders.Base64UrlDecode(token).Length);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(token, body);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("Active", document.RootElement.GetProperty("stage").GetString());
        Assert.Equal(id, document.RootElement.GetProperty("userId").GetGuid());
        Assert.Equal("profile:read", document.RootElement.GetProperty("permissions")[0].GetString());
        await using var db = driver.Database.CreateContext();
        Assert.Equal(SHA256.HashData(WebEncoders.Base64UrlDecode(token)), (await db.Set<IdentitySession>().SingleAsync()).TokenHash);
        Assert.NotNull((await db.Set<PreAuthFlow>().SingleAsync()).ConsumedAtUtc);
        Assert.DoesNotContain(await db.AuditMetadata.Select(x => x.Value).ToListAsync(), x => x.Contains(token) || x.Contains(Password));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(31)]
    public async Task Idle_expiry_rejects_session(int minutes)
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await driver.SeedUserAsync("staff", Password, []);
        Assert.Equal(HttpStatusCode.OK, (await driver.LoginAsync("staff", Password)).StatusCode);
        driver.Advance(TimeSpan.FromMinutes(minutes));
        using var response = await driver.Client.GetAsync("/api/v1/auth/session");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }
}
