using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Identity.Data;
using static TPR10.Api.IntegrationTests.PasswordResetTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class AdminPasswordResetTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Admin_reset_revokes_immediately_and_temporary_password_allows_only_one_login()
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(driver);
        var target = await driver.SeedUserAsync("target", Password, [], requiresMfa: true);
        using var old = driver.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(old, "/api/v1/auth/login", new { username = "target", password = Password })).StatusCode);
        var reset = await driver.PostAsync($"/api/v1/users/{target}/password-reset", new { });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.True(reset.Headers.CacheControl!.NoStore);
        using var body = JsonDocument.Parse(await reset.Content.ReadAsStringAsync());
        var temporary = body.RootElement.GetProperty("temporaryPassword").GetString();
        Assert.Equal(HttpStatusCode.Unauthorized, (await old.GetAsync("/api/v1/auth/session")).StatusCode);
        using var first = driver.NewClient();
        using var second = driver.NewClient();
        var logins = await Task.WhenAll(PostAsync(first, "/api/v1/auth/login", new { username = "target", password = temporary }),
            PostAsync(second, "/api/v1/auth/login", new { username = "target", password = temporary }));
        Assert.Single(logins, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(logins, x => x.StatusCode == HttpStatusCode.Unauthorized);
        var winner = logins[0].IsSuccessStatusCode ? first : second;
        using var session = JsonDocument.Parse(await (await winner.GetAsync("/api/v1/auth/session")).Content.ReadAsStringAsync());
        Assert.Equal("PasswordChangeRequired", session.RootElement.GetProperty("stage").GetString());
        driver.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(winner, "/api/v1/auth/password/change", new { currentPassword = temporary, newPassword = NewPassword })).StatusCode);
        using var next = driver.NewClient();
        var login = await PostAsync(next, "/api/v1/auth/login", new { username = "target", password = NewPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var newSession = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        Assert.Equal("MfaEnrollmentRequired", newSession.RootElement.GetProperty("stage").GetString());
        await using var db = driver.Database.CreateContext();
        Assert.Empty(await db.Set<IdentityDeliveryOutbox>().ToListAsync());
        var audit = await db.AuditEvents.SingleAsync(x => x.EventType == "identity.password-reset.admin");
        Assert.Equal(actor, audit.ActorId);
        Assert.Equal(target, audit.TargetId);
        Assert.NotNull(audit.ActingRoleId);
        Assert.Equal("success", audit.Outcome);
        Assert.Equal("user", audit.TargetType);
        var metadata = string.Join(" ", await db.AuditMetadata.Select(x => x.Value).ToListAsync());
        Assert.DoesNotContain(temporary!, metadata);
        Assert.DoesNotContain(NewPassword, metadata);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Admin_reset_requires_permission_and_mfa(bool permission)
    {
        await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString);
        await driver.SeedUserAsync("actor", Password, permission ? ["users:manage"] : []);
        var target = await driver.SeedUserAsync("target", Password, []);
        Assert.Equal(HttpStatusCode.OK, (await driver.LoginAsync("actor", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await driver.PostAsync($"/api/v1/users/{target}/password-reset", new { })).StatusCode);
        await using var db = driver.Database.CreateContext();
        Assert.Equal(0, (await db.Set<IdentityUser>().SingleAsync(x => x.Id == target)).SecurityVersion);
    }

    [Fact]
    public async Task Temporary_password_expires_after_fifteen_minutes()
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString, keys.Settings);
        await RoleAuthorizationTests.AdminAsync(driver);
        var target = await driver.SeedUserAsync("target", Password, []);
        var response = await driver.PostAsync($"/api/v1/users/{target}/password-reset", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        driver.Advance(TimeSpan.FromMinutes(15));
        using var client = driver.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(client, "/api/v1/auth/login", new { username = "target", password = body.RootElement.GetProperty("temporaryPassword").GetString() })).StatusCode);
    }
}
