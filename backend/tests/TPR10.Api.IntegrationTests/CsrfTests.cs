using System.Net;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Identity.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class CsrfTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("/api/v1/auth/csrf/")]
    [InlineData("/API/V1/AUTH/CSRF/")]
    public async Task Route_aliases_cannot_bypass_transport_or_rate_limit(string path)
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString,
            new() { ["Identity:Csrf:PerIpPermitLimit"] = "2" });
        Assert.Equal(HttpStatusCode.Forbidden, (await driver.Client.GetAsync("http://localhost:4443" + path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await driver.Client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await driver.Client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Explicit_origins_do_not_retain_development_default()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString,
            new() { ["Identity:Csrf:AllowedOrigins:0"] = "https://portal.example" });
        Assert.Equal(HttpStatusCode.Forbidden, (await driver.Client.GetAsync("/api/v1/auth/csrf")).StatusCode);
    }

    [Fact]
    public async Task Denial_audit_failure_is_fail_closed_and_does_not_leak_details()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await using var db = driver.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_csrf_audit() RETURNS trigger AS $$
            BEGIN RAISE EXCEPTION 'private-test-detail'; END; $$ LANGUAGE plpgsql;
            CREATE TRIGGER reject_csrf_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION reject_csrf_audit();
            """);
        using var response = await driver.Client.PostAsync("/api/v1/system/technical-probes", null);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.DoesNotContain("private-test-detail", await response.Content.ReadAsStringAsync());
        Assert.True(Guid.TryParse(response.Headers.GetValues("X-Correlation-ID").Single(), out _));
        Assert.Empty(await db.TechnicalProbes.ToListAsync());
        Assert.Empty(await db.Set<IdentitySession>().ToListAsync());
    }

    [Fact]
    public async Task Concurrent_issuers_cannot_exceed_database_capacity()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString,
            new() { ["Identity:Csrf:MaxActiveFlows"] = "3" });
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var client = driver.NewClient();
            using var response = await client.GetAsync("/api/v1/auth/csrf");
            return response.StatusCode;
        }));
        Assert.Equal(3, results.Count(x => x == HttpStatusCode.OK));
        Assert.Equal(5, results.Count(x => x == HttpStatusCode.ServiceUnavailable));
        await using var db = driver.Database.CreateContext();
        Assert.Equal(3, await db.Set<PreAuthFlow>().CountAsync());
    }

    [Fact]
    public async Task Global_budget_limits_issuance_even_with_larger_per_ip_budget()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString,
            new() { ["Identity:Csrf:GlobalPermitLimit"] = "1", ["Identity:Csrf:PerIpPermitLimit"] = "20" });
        await IdentityTestDriver.TokenAsync(driver.Client);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await driver.Client.GetAsync("/api/v1/auth/csrf")).StatusCode);
    }

    [Fact]
    public async Task Persistent_keys_survive_restart_and_are_encrypted_at_rest()
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        using var issued = await driver.Client.GetAsync("/api/v1/auth/csrf");
        issued.EnsureSuccessStatusCode();
        var json = await issued.Content.ReadAsStringAsync();
        var token = System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("token").GetString()!;
        var cookie = issued.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        await using var restarted = new ApiFactory(driver.Database.ConnectionString, "Production", driver.Clock, keys.Settings);
        using var client = restarted.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443"), HandleCookies = false });
        using var request = IdentityTestDriver.Mutation(token);
        request.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(request)).StatusCode);
        Assert.Contains("encryptedSecret", keys.ReadKeyXml());
        Assert.DoesNotContain("<masterKey", keys.ReadKeyXml());
    }

    [Theory]
    [InlineData("127.0.0.1", HttpStatusCode.OK)]
    [InlineData("::1", HttpStatusCode.OK)]
    [InlineData("192.0.2.50", HttpStatusCode.Forbidden)]
    public async Task Only_trusted_proxy_can_supply_https_authority(string remote, HttpStatusCode expected)
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await using var proxy = driver.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<IStartupFilter>(new RemoteAddressFilter(remote))));
        using var client = proxy.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://private-api:5080") });
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "localhost:4443");
        Assert.Equal(expected, (await client.GetAsync("/api/v1/auth/csrf")).StatusCode);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("origin-port")]
    [InlineData("missing-origin")]
    [InlineData("multiple-origin")]
    [InlineData("null-origin")]
    [InlineData("cross-site")]
    [InlineData("purpose")]
    [InlineData("revoked")]
    [InlineData("consumed")]
    [InlineData("duplicate-token")]
    public async Task Rejects_invalid_context_without_business_mutation(string scenario)
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var token = await IdentityTestDriver.TokenAsync(driver.Client);
        await using var db = driver.Database.CreateContext();
        if (scenario is "revoked" or "consumed")
        {
            var flow = await db.Set<PreAuthFlow>().SingleAsync();
            if (scenario == "revoked") flow.RevokedAtUtc = driver.Clock.GetUtcNow();
            else flow.ConsumedAtUtc = driver.Clock.GetUtcNow();
            await db.SaveChangesAsync();
        }
        if (scenario == "purpose")
            token = driver.Factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("TPR10.Identity.MfaSecret.v1").Protect("not-csrf");
        using var request = IdentityTestDriver.Mutation(token, "/api/v1/system/technical-probes");
        if (scenario == "http") request.RequestUri = new Uri("http://localhost:4443/api/v1/system/technical-probes");
        if (scenario is "origin-port" or "missing-origin" or "null-origin")
        {
            request.Headers.Remove("Origin");
            if (scenario != "missing-origin") request.Headers.Add("Origin", scenario == "null-origin" ? "null" : "https://localhost:4444");
        }
        if (scenario == "multiple-origin") request.Headers.Add("Origin", "https://evil.example");
        if (scenario == "cross-site") request.Headers.Add("Sec-Fetch-Site", "cross-site");
        if (scenario == "duplicate-token") request.Headers.Add("X-CSRF-Token", token);
        Assert.Equal(HttpStatusCode.Forbidden, (await driver.Client.SendAsync(request)).StatusCode);
        Assert.Empty(await db.TechnicalProbes.ToListAsync());
        var audit = await db.AuditEvents.SingleAsync();
        Assert.Equal("security.csrf.denied", audit.EventType);
        Assert.All(await db.AuditMetadata.ToListAsync(), item => Assert.Contains(item.Key, new[] { "outcome", "reason" }));
    }

    private sealed class RemoteAddressFilter(string address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, downstream) => { context.Connection.RemoteIpAddress = IPAddress.Parse(address); await downstream(context); });
            next(app);
        };
    }

    [Fact]
    public async Task Rate_limit_rejects_issuer_flood_before_database_growth()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString,
            new() { ["Identity:Csrf:PerIpPermitLimit"] = "2" });
        for (var i = 0; i < 2; i++)
        {
            using var client = driver.NewClient();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/csrf")).StatusCode);
        }
        using var response = await driver.Client.GetAsync("/api/v1/auth/csrf");
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.NotNull(response.Headers.RetryAfter);
        await using var db = driver.Database.CreateContext();
        Assert.Equal(2, await db.Set<PreAuthFlow>().CountAsync());
        Assert.Equal(HttpStatusCode.OK, (await driver.Client.GetAsync("/api/health/live")).StatusCode);
    }

    [Fact]
    public async Task Capacity_is_bounded_and_expired_records_are_reclaimed()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString,
            new() { ["Identity:Csrf:MaxActiveFlows"] = "1" });
        await IdentityTestDriver.TokenAsync(driver.Client);
        using var other = driver.NewClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await other.GetAsync("/api/v1/auth/csrf")).StatusCode);
        await IdentityTestDriver.TokenAsync(driver.Client);
        driver.Clock.Advance(TimeSpan.FromMinutes(11));
        await IdentityTestDriver.TokenAsync(other);
        await using var db = driver.Database.CreateContext();
        Assert.Equal(1, await db.Set<PreAuthFlow>().CountAsync());
    }

    [Fact]
    public async Task Valid_csrf_allows_existing_probe_and_missing_csrf_does_not_mutate()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await driver.Client.PostAsync("/api/v1/system/technical-probes", null)).StatusCode);
        await using var db = driver.Database.CreateContext();
        Assert.Empty(await db.TechnicalProbes.ToListAsync());
        var token = await IdentityTestDriver.TokenAsync(driver.Client);
        using var request = IdentityTestDriver.Mutation(token, "/api/v1/system/technical-probes");
        Assert.Equal(HttpStatusCode.Created, (await driver.Client.SendAsync(request)).StatusCode);
        Assert.Equal(1, await db.TechnicalProbes.CountAsync());
    }

    [Fact]
    public async Task Missing_token_is_denied_before_login_and_audited_without_session()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        using var response = await driver.Client.PostAsync("/api/v1/auth/login", null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var db = driver.Database.CreateContext();
        Assert.Equal(0, await db.Set<IdentitySession>().CountAsync());
        Assert.Equal("security.csrf.denied", (await db.AuditEvents.SingleAsync()).EventType);
    }

    [Fact]
    public async Task Issuer_sets_host_cookie_and_reuses_bounded_flow_without_business_writes()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        using var response = await driver.Client.GetAsync("/api/v1/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("__Host-tpr10_preauth=", cookie);
        Assert.Contains("secure", cookie.ToLowerInvariant());
        Assert.Contains("httponly", cookie.ToLowerInvariant());
        Assert.Contains("samesite=lax", cookie.ToLowerInvariant());
        Assert.Contains("path=/", cookie);
        Assert.DoesNotContain("domain=", cookie.ToLowerInvariant());
        await IdentityTestDriver.TokenAsync(driver.Client);
        await using var db = driver.Database.CreateContext();
        Assert.Equal(1, await db.Set<PreAuthFlow>().CountAsync());
        Assert.Empty(await db.AuditEvents.ToListAsync());
        Assert.Empty(await db.TechnicalProbes.ToListAsync());
        Assert.Empty(await db.Set<IdentitySession>().ToListAsync());
    }

    [Theory]
    [InlineData("valid", HttpStatusCode.NotFound)]
    [InlineData("tampered", HttpStatusCode.Forbidden)]
    [InlineData("expired", HttpStatusCode.Forbidden)]
    [InlineData("other-flow", HttpStatusCode.Forbidden)]
    [InlineData("origin", HttpStatusCode.Forbidden)]
    [InlineData("host", HttpStatusCode.Forbidden)]
    [InlineData("session", HttpStatusCode.Forbidden)]
    public async Task Validation_binds_transport_cookie_and_short_lived_token(string scenario, HttpStatusCode expected)
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var token = await IdentityTestDriver.TokenAsync(driver.Client);
        if (scenario == "tampered") token = "broken" + token;
        if (scenario == "expired") driver.Clock.Advance(TimeSpan.FromMinutes(11));
        using var request = IdentityTestDriver.Mutation(token);
        if (scenario == "origin") { request.Headers.Remove("Origin"); request.Headers.Add("Origin", "https://evil.example"); }
        if (scenario == "host") request.Headers.Host = "evil.example";
        if (scenario == "session") request.Headers.Add("Cookie", "__Host-tpr10_session=unvalidated");
        using var other = driver.NewClient();
        if (scenario == "other-flow") await IdentityTestDriver.TokenAsync(other);
        using var response = await (scenario == "other-flow" ? other : driver.Client).SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        await using var db = driver.Database.CreateContext();
        Assert.Empty(await db.Set<IdentitySession>().ToListAsync());
    }
}
