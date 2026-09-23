using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Identity.Data;
using static TPR10.Api.IntegrationTests.MfaTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class AuthorizationTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("anonymous", 401, "session-required")]
    [InlineData("permission", 403, "permission-denied")]
    [InlineData("mfa", 403, "mfa-required")]
    [InlineData("expired", 403, "mfa-required")]
    [InlineData("enrollment", 403, "stage-restricted")]
    [InlineData("password", 403, "stage-restricted")]
    [InlineData("success", 200, null)]
    public async Task Protected_probe_enforces_session_permission_stage_and_recent_mfa(string scenario, int status, string? problem)
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        if (scenario != "anonymous")
        {
            await driver.SeedUserAsync("actor", Password, scenario == "permission" ? [] : ["system:probe"],
                requiresMfa: scenario == "enrollment", mustChangePassword: scenario == "password");
            Assert.Equal(HttpStatusCode.OK, (await driver.LoginAsync("actor", Password)).StatusCode);
            if (scenario is "expired" or "success") await ConfirmAsync(driver);
            if (scenario == "expired") driver.Advance(TimeSpan.FromMinutes(15));
        }
        using var response = await driver.Client.GetAsync("/api/v1/system/identity-probe");
        Assert.Equal(status, (int)response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        if (problem is not null)
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("urn:tpr10:" + problem, body.RootElement.GetProperty("type").GetString());
            Assert.Equal(response.Headers.GetValues("X-Correlation-ID").Single(), body.RootElement.GetProperty("correlationId").GetString());
            await using var db = driver.Database.CreateContext();
            Assert.True(await db.AuditEvents.AnyAsync(x => x.EventType == "identity.authorization.denied"));
        }
    }

    [Fact]
    public async Task Anonymous_with_valid_csrf_cannot_create_probe()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await driver.PostAsync("/api/v1/system/technical-probes", new { note = "blocked" })).StatusCode);
        await using var db = driver.Database.CreateContext();
        Assert.Empty(await db.TechnicalProbes.ToListAsync());
    }

    [Theory]
    [InlineData("GET", "/api/v1/users")]
    [InlineData("POST", "/api/v1/users")]
    [InlineData("PATCH", "/api/v1/users/10000000-0000-0000-0000-000000000099")]
    [InlineData("GET", "/api/v1/roles")]
    [InlineData("POST", "/api/v1/roles")]
    [InlineData("PATCH", "/api/v1/roles/10000000-0000-0000-0000-000000000099")]
    [InlineData("PUT", "/api/v1/roles/10000000-0000-0000-0000-000000000099/permissions")]
    [InlineData("PUT", "/api/v1/users/10000000-0000-0000-0000-000000000099/roles")]
    [InlineData("POST", "/api/v1/users/10000000-0000-0000-0000-000000000099/sign-out-everywhere")]
    [InlineData("POST", "/api/v1/users/10000000-0000-0000-0000-000000000099/mfa/recover")]
    [InlineData("GET", "/api/v1/permissions")]
    public async Task Every_admin_route_denies_anonymous_and_unprivileged_session_before_binding(string method, string path)
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        async Task<HttpResponseMessage> Send() => method == "GET" ? await driver.Client.GetAsync(path) : await SendAsync(driver, new HttpMethod(method), path, new { });
        using var anonymous = await Send();
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.True(anonymous.Headers.CacheControl?.NoStore);
        await driver.SeedUserAsync("staff", Password, []);
        Assert.Equal(HttpStatusCode.OK, (await driver.LoginAsync("staff", Password)).StatusCode);
        using var denied = await Send();
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.True(denied.Headers.CacheControl?.NoStore);
        using var body = JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
        Assert.Equal("urn:tpr10:permission-denied", body.RootElement.GetProperty("type").GetString());
    }

    internal static async Task ConfirmAsync(IdentityTestDriver driver)
    {
        using var enroll = await driver.PostAsync("/api/v1/auth/mfa/enroll", new { });
        Assert.Equal(HttpStatusCode.OK, enroll.StatusCode);
        var secret = Secret(await enroll.Content.ReadAsStringAsync());
        using var confirm = await driver.PostAsync("/api/v1/auth/mfa/confirm", new { code = Code(secret, driver.Clock.GetUtcNow()) });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
    }

    internal static async Task<HttpResponseMessage> SendAsync(IdentityTestDriver driver, HttpMethod method, string path, object body)
    {
        using var request = IdentityTestDriver.Mutation(await IdentityTestDriver.TokenAsync(driver.Client), path);
        request.Method = method;
        request.Content = JsonContent.Create(body);
        return await driver.Client.SendAsync(request);
    }
}
