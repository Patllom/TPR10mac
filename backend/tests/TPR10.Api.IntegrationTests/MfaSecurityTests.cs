using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Identity.Data;
using static TPR10.Api.IntegrationTests.MfaTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class MfaSecurityTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Challenge_rotates_and_rejects_replay_across_two_sessions()
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var (secret, _) = await EnrollConfirmedAsync(driver);
        using var one = driver.NewClient(); using var two = driver.NewClient();
        await LoginAsync(one); await LoginAsync(two);
        driver.Advance(TimeSpan.FromSeconds(30));
        var code = Code(secret, driver.Clock.GetUtcNow());
        var replies = await Task.WhenAll(PostAsync(one, "challenge", code), PostAsync(two, "challenge", code));
        Assert.Single(replies, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(replies, x => x.StatusCode == HttpStatusCode.Forbidden);
        using var db = driver.Database.CreateContext();
        Assert.Single(await db.AuditEvents.Where(x => x.EventType == "identity.mfa.challenge").ToListAsync());
    }

    [Fact]
    public async Task Recovery_consumes_once_revokes_all_sessions_and_requires_new_factor()
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var (_, codes) = await EnrollConfirmedAsync(driver);
        using var one = driver.NewClient(); using var two = driver.NewClient();
        await LoginAsync(one); await LoginAsync(two);
        var replies = await Task.WhenAll(PostAsync(one, "recover", codes[0]), PostAsync(two, "recover", codes[0]));
        Assert.Single(replies, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(replies, x => x.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
        using var body = JsonDocument.Parse(await replies.Single(x => x.StatusCode == HttpStatusCode.OK).Content.ReadAsStringAsync());
        Assert.Equal("MfaEnrollmentRequired", body.RootElement.GetProperty("session").GetProperty("stage").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("session").GetProperty("mfaVerifiedAtUtc").ValueKind);
        Assert.Equal(HttpStatusCode.Unauthorized, (await driver.Client.GetAsync("/api/v1/auth/session")).StatusCode);
        await using var db = driver.Database.CreateContext();
        Assert.NotNull((await db.Set<MfaFactor>().SingleAsync()).RevokedAtUtc);
        Assert.Equal(1, await db.Set<MfaRecoveryCode>().CountAsync(x => x.ConsumedAtUtc != null));
        Assert.False(await db.Set<MfaRecoveryCode>().AnyAsync(x => x.RevokedAtUtc == null && x.ConsumedAtUtc == null));
    }

    [Fact]
    public async Task Drift_outside_window_is_denied_and_five_bad_codes_lock_account_across_sessions()
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var (secret, _) = await EnrollConfirmedAsync(driver);
        using var client = driver.NewClient(); await LoginAsync(client);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(client, "challenge", Code(secret, driver.Clock.GetUtcNow().AddMinutes(2)))).StatusCode);
        for (var i = 0; i < 4; i++) Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(client, "challenge", "invalid")).StatusCode);
        // Let the 1-minute IP budget renew; the account-wide 15-minute MFA lock must still hold.
        driver.Advance(TimeSpan.FromMinutes(1));
        using var other = driver.NewClient(); await LoginAsync(other);
        var denied = await PostAsync(other, "challenge", Code(secret, driver.Clock.GetUtcNow().AddSeconds(30)));
        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
        Assert.NotNull(denied.Headers.RetryAfter);
    }

    [Fact]
    public async Task Expired_challenge_and_assurance_require_fresh_proof()
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var (secret, _) = await EnrollConfirmedAsync(driver);
        using var restricted = driver.NewClient(); await LoginAsync(restricted);
        driver.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(restricted, "challenge", Code(secret, driver.Clock.GetUtcNow()))).StatusCode);
        driver.Advance(TimeSpan.FromMinutes(5));
        using var body = JsonDocument.Parse(await driver.Client.GetStringAsync("/api/v1/auth/session"));
        Assert.Equal("MfaChallengeRequired", body.RootElement.GetProperty("stage").GetString());
        Assert.Equal(0, body.RootElement.GetProperty("permissions").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(driver.Client, "challenge", Code(secret, driver.Clock.GetUtcNow()))).StatusCode);
    }

    [Fact]
    public async Task Optional_staff_can_enroll_and_confirm()
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await EnrollConfirmedAsync(driver, mandatory: false);
    }

    [Fact]
    public async Task Restricted_sessions_cannot_mutate_technical_probe()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await driver.SeedUserAsync("admin", Password, ["system:probe"], requiresMfa: true);
        await driver.LoginAsync("admin", Password);
        using var response = await driver.PostAsync("/api/v1/system/technical-probes", new { note = "blocked" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("enroll")]
    [InlineData("confirm")]
    [InlineData("challenge")]
    [InlineData("recover")]
    public async Task Password_change_stage_cannot_use_mfa(string action)
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await driver.SeedUserAsync("admin", Password, [], requiresMfa: true, mustChangePassword: true);
        await driver.LoginAsync("admin", Password);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(driver.Client, action, "123456")).StatusCode);
    }

    internal static async Task<(string Secret, string[] Codes)> EnrollConfirmedAsync(IdentityTestDriver driver, bool mandatory = true)
    {
        await driver.SeedUserAsync("admin", Password, ["users:manage"], requiresMfa: mandatory);
        await driver.LoginAsync("admin", Password);
        var response = await driver.PostAsync("/api/v1/auth/mfa/enroll", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var secret = Secret(await response.Content.ReadAsStringAsync());
        var confirmed = await PostAsync(driver.Client, "confirm", Code(secret, driver.Clock.GetUtcNow()));
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        using var body = JsonDocument.Parse(await confirmed.Content.ReadAsStringAsync());
        return (secret, body.RootElement.GetProperty("recoveryCodes").EnumerateArray().Select(x => x.GetString()!).ToArray());
    }
    internal static async Task LoginAsync(HttpClient client)
    {
        var token = await IdentityTestDriver.TokenAsync(client);
        using var request = IdentityTestDriver.Mutation(token, "/api/v1/auth/login");
        request.Content = JsonContent.Create(new { username = "admin", password = Password });
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
    }
    internal static async Task<HttpResponseMessage> PostAsync(HttpClient client, string action, string code)
    {
        using var request = IdentityTestDriver.Mutation(await IdentityTestDriver.TokenAsync(client), "/api/v1/auth/mfa/" + action);
        request.Content = JsonContent.Create(new { code });
        return await client.SendAsync(request);
    }
}
