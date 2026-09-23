using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Identity;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Passwords;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class PasswordResetTests(PostgresFixture fixture)
{
    internal const string Password = "รหัสทดสอบยาวพอ-123456";
    internal const string NewPassword = "รหัสใหม่ยาวพอ-654321";

    [Theory]
    [InlineData("known")]
    [InlineData("unknown")]
    public async Task Reset_request_does_not_reveal_account(string username)
    {
        await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString);
        await driver.SeedUserAsync("known", Password, []);
        var response = await driver.PostAsync("/api/v1/auth/password-reset/request", new { username });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("หากบัญชีรองรับการกู้คืน ระบบจะดำเนินการตามช่องทางที่กำหนด", await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task Eligible_request_stores_hash_and_encrypted_outbox_without_changing_credentials()
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString, keys.Settings);
        var id = await driver.SeedUserAsync("known", Password, []);
        await using (var setup = driver.Database.CreateContext())
        {
            (await setup.Set<IdentityUser>().SingleAsync()).Email = "test@example.invalid";
            await setup.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Accepted, (await driver.PostAsync("/api/v1/auth/password-reset/request", new { username = "known" })).StatusCode);
        await using var db = driver.Database.CreateContext();
        var reset = await db.Set<PasswordResetRequest>().SingleAsync();
        var outbox = await db.Set<IdentityDeliveryOutbox>().SingleAsync();
        Assert.Equal(id, reset.UserId);
        Assert.Equal(reset.Id, outbox.RequestId);
        Assert.Equal(driver.Clock.GetUtcNow().AddMinutes(15), reset.ExpiresAtUtc);
        var token = driver.Factory.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("TPR10.Reset.Delivery.v1", reset.Id.ToString("D")).Unprotect(outbox.ProtectedPayload);
        Assert.Equal(32, WebEncoders.Base64UrlDecode(token).Length);
        Assert.Equal(SHA256.HashData(WebEncoders.Base64UrlDecode(token)), reset.TokenHash);
        Assert.DoesNotContain(token, outbox.ProtectedPayload);
        Assert.True(await new ArgonPasswordHasher().VerifyAsync(Password, (await db.Set<LocalCredential>().SingleAsync()).PasswordHash, default));
        Assert.Null(outbox.DeliveredAtUtc);
    }

    [Fact]
    public async Task Complete_consumes_once_revokes_sessions_and_does_not_login()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString);
        var id = await driver.SeedUserAsync("known", Password, []);
        Assert.Equal(HttpStatusCode.OK, (await driver.LoginAsync("known", Password)).StatusCode);
        var token = await SeedResetAsync(driver, id);
        using var anonymous = driver.NewClient();
        var response = await PostAsync(anonymous, "/api/v1/auth/password-reset/complete", new { token, password = NewPassword });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out _));
        Assert.Equal(HttpStatusCode.Unauthorized, (await driver.Client.GetAsync("/api/v1/auth/session")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(anonymous, "/api/v1/auth/password-reset/complete", new { token, password = Password })).StatusCode);
        await using var db = driver.Database.CreateContext();
        Assert.NotNull((await db.Set<PasswordResetRequest>().SingleAsync()).ConsumedAtUtc);
        Assert.True(await new ArgonPasswordHasher().VerifyAsync(NewPassword, (await db.Set<LocalCredential>().SingleAsync()).PasswordHash, default));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("consumed")]
    [InlineData("inactive")]
    [InlineData("malformed")]
    public async Task Invalid_reset_cannot_change_password(string state)
    {
        await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString);
        var id = await driver.SeedUserAsync("known", Password, []);
        var token = await SeedResetAsync(driver, id);
        await using (var setup = driver.Database.CreateContext())
        {
            var row = await setup.Set<PasswordResetRequest>().SingleAsync();
            if (state == "expired") driver.Advance(TimeSpan.FromMinutes(15));
            if (state == "revoked") row.RevokedAtUtc = driver.Clock.GetUtcNow();
            if (state == "consumed") row.ConsumedAtUtc = driver.Clock.GetUtcNow();
            if (state == "inactive") (await setup.Set<IdentityUser>().SingleAsync()).IsActive = false;
            if (state == "malformed") token = "invalid";
            await setup.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await driver.PostAsync("/api/v1/auth/password-reset/complete", new { token, password = NewPassword })).StatusCode);
        await using var db = driver.Database.CreateContext();
        Assert.True(await new ArgonPasswordHasher().VerifyAsync(Password, (await db.Set<LocalCredential>().SingleAsync()).PasswordHash, default));
    }

    [Fact]
    public async Task Concurrent_complete_has_one_winner()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString);
        var id = await driver.SeedUserAsync("known", Password, []);
        var token = await SeedResetAsync(driver, id);
        using var other = driver.NewClient();
        var responses = await Task.WhenAll(driver.PostAsync("/api/v1/auth/password-reset/complete", new { token, password = NewPassword }),
            PostAsync(other, "/api/v1/auth/password-reset/complete", new { token, password = NewPassword }));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Forced_change_revokes_session_then_requires_login_and_mfa()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString);
        await driver.SeedUserAsync("forced", Password, ["system:probe"], requiresMfa: true, mustChangePassword: true);
        Assert.Equal(HttpStatusCode.OK, (await driver.LoginAsync("forced", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await driver.PostAsync("/api/v1/system/technical-probes", new { note = "denied" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await driver.PostAsync("/api/v1/auth/password/change", new { currentPassword = Password, newPassword = NewPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await driver.Client.GetAsync("/api/v1/auth/session")).StatusCode);
        var login = await driver.LoginAsync("forced", NewPassword);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        Assert.Equal("MfaEnrollmentRequired", body.RootElement.GetProperty("stage").GetString());
    }

    internal static async Task<string> SeedResetAsync(IdentityTestDriver driver, Guid userId)
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        await using var db = driver.Database.CreateContext();
        db.Add(new PasswordResetRequest { Id = Guid.NewGuid(), UserId = userId, TokenHash = SHA256.HashData(bytes), CreatedAtUtc = driver.Clock.GetUtcNow(), ExpiresAtUtc = driver.Clock.GetUtcNow().AddMinutes(15) });
        await db.SaveChangesAsync();
        return WebEncoders.Base64UrlEncode(bytes);
    }

    internal static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body)
    {
        using var request = IdentityTestDriver.Mutation(await IdentityTestDriver.TokenAsync(client), path);
        request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }
}
