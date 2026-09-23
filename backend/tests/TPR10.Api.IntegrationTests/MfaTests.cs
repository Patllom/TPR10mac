using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Identity;
using TPR10.Api.Identity.Data;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class MfaTests(PostgresFixture postgres)
{
    internal const string Password = "รหัสทดสอบยาวพอ-123456";

    [Fact]
    public async Task Privileged_login_requires_mfa_enrollment()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await driver.SeedUserAsync("admin", Password, ["users:manage"], requiresMfa: true);
        using var response = await driver.LoginAsync("admin", Password);
        using var view = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("MfaEnrollmentRequired", view.RootElement.GetProperty("stage").GetString());
    }

    [Fact]
    public async Task Enrollment_encrypts_secret_and_confirmation_rotates_session_without_secret_audit()
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await driver.SeedUserAsync("admin", Password, ["users:manage"], requiresMfa: true);
        using var login = await driver.LoginAsync("admin", Password);
        var oldCookie = SessionCookie(login);
        using var enrolled = await driver.PostAsync("/api/v1/auth/mfa/enroll", new { });
        Assert.Equal(HttpStatusCode.OK, enrolled.StatusCode);
        Assert.True(enrolled.Headers.CacheControl!.NoStore);
        var secret = Secret(await enrolled.Content.ReadAsStringAsync());
        await using var db = driver.Database.CreateContext();
        var factor = await db.Set<MfaFactor>().AsNoTracking().SingleAsync();
        Assert.Null(factor.ConfirmedAtUtc);
        Assert.DoesNotContain(secret, factor.ProtectedSecret);
        using var confirm = await driver.PostAsync("/api/v1/auth/mfa/confirm", new { code = Code(secret, driver.Clock.GetUtcNow()) });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        Assert.NotEqual(oldCookie, SessionCookie(confirm));
        using var body = JsonDocument.Parse(await confirm.Content.ReadAsStringAsync());
        Assert.Equal("Active", body.RootElement.GetProperty("session").GetProperty("stage").GetString());
        Assert.Equal(10, body.RootElement.GetProperty("recoveryCodes").GetArrayLength());
        Assert.NotNull((await db.Set<MfaFactor>().AsNoTracking().SingleAsync()).ConfirmedAtUtc);
        var metadata = string.Join(" ", await db.AuditMetadata.Select(x => x.Value).ToListAsync());
        Assert.DoesNotContain(secret, metadata);
        foreach (var code in body.RootElement.GetProperty("recoveryCodes").EnumerateArray()) Assert.DoesNotContain(code.GetString()!, metadata);
        using var stale = driver.NewClient();
        stale.DefaultRequestHeaders.Add("Cookie", oldCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stale.GetAsync("/api/v1/auth/session")).StatusCode);
    }

    [Theory]
    [InlineData("enroll")]
    [InlineData("confirm")]
    [InlineData("challenge")]
    [InlineData("recover")]
    public async Task Anonymous_cannot_use_mfa(string action)
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        using var response = await driver.PostAsync("/api/v1/auth/mfa/" + action, new { code = "123456" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    internal static string SessionCookie(HttpResponseMessage response) => response.Headers.GetValues("Set-Cookie")
        .Single(x => x.StartsWith("__Host-tpr10_session=", StringComparison.Ordinal)).Split(';')[0];
    internal static string Secret(string json)
    {
        using var body = JsonDocument.Parse(json);
        var uri = new Uri(body.RootElement.GetProperty("provisioningUri").GetString()!);
        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query)["secret"].ToString();
    }
    // Independent RFC 6238 calculation, not the production OTP library.
    internal static string Code(string secret, DateTimeOffset now)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0; var value = 0; var bytes = new List<byte>();
        foreach (var character in secret)
        {
            value = (value << 5) | alphabet.IndexOf(character); bits += 5;
            if (bits >= 8) { bits -= 8; bytes.Add((byte)(value >> bits)); }
        }
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, now.ToUnixTimeSeconds() / 30);
        var hash = HMACSHA1.HashData(bytes.ToArray(), counter);
        var offset = hash[^1] & 15;
        return ((BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(offset, 4)) & 0x7fffffff) % 1000000).ToString("D6");
    }
}
