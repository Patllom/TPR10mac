using System.Net;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Passwords;
using static TPR10.Api.IntegrationTests.PasswordResetTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class PasswordResetThrottleTests(PostgresFixture fixture)
{
    private const string WrongPassword = "wrong-password-long-enough-123456";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Change_failures_share_account_budget_across_sessions_and_block_login(bool concurrent)
    {
        await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString);
        await driver.SeedUserAsync("known", Password, []);
        Assert.Equal(HttpStatusCode.OK, (await driver.LoginAsync("known", Password)).StatusCode);
        using var other = driver.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(other, "/api/v1/auth/login", new { username = "known", password = Password })).StatusCode);
        async Task<HttpResponseMessage> Fail(int i) => await PostAsync(i % 2 == 0 ? driver.Client : other,
            "/api/v1/auth/password/change", new { currentPassword = WrongPassword, newPassword = NewPassword });
        var responses = new List<HttpResponseMessage>();
        if (concurrent) responses.AddRange(await Task.WhenAll(Enumerable.Range(0, 5).Select(Fail)));
        else for (var i = 0; i < 5; i++) responses.Add(await Fail(i));
        Assert.All(responses, x => Assert.Equal(HttpStatusCode.BadRequest, x.StatusCode));
        var blocked = await PostAsync(other, "/api/v1/auth/password/change", new { currentPassword = Password, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.NotNull(blocked.Headers.RetryAfter);
        using var anonymous = driver.NewClient();
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostAsync(anonymous, "/api/v1/auth/login", new { username = "known", password = Password })).StatusCode);
        await using var db = driver.Database.CreateContext();
        var credential = await db.Set<LocalCredential>().SingleAsync();
        Assert.Equal(5, credential.FailedAttempts);
        Assert.Equal(driver.Clock.GetUtcNow().AddMinutes(15), credential.LockedUntilUtc);
        Assert.Equal(5, await db.AuditEvents.CountAsync(x => x.EventType == "identity.password.change.failed" && x.Outcome == "denied"));
        Assert.True(await new ArgonPasswordHasher().VerifyAsync(Password, credential.PasswordHash, default));
        Assert.Equal(0, (await db.Set<IdentityUser>().SingleAsync()).SecurityVersion);
        driver.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(other, "/api/v1/auth/password/change", new { currentPassword = Password, newPassword = NewPassword })).StatusCode);
    }

    [Fact]
    public async Task Login_lockout_cannot_be_bypassed_by_password_change()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString);
        await driver.SeedUserAsync("known", Password, []);
        Assert.Equal(HttpStatusCode.OK, (await driver.LoginAsync("known", Password)).StatusCode);
        using var anonymous = driver.NewClient();
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Unauthorized,
            (await PostAsync(anonymous, "/api/v1/auth/login", new { username = "known", password = WrongPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await driver.PostAsync("/api/v1/auth/password/change", new { currentPassword = Password, newPassword = NewPassword })).StatusCode);
        await using var db = driver.Database.CreateContext();
        Assert.Equal(0, (await db.Set<IdentityUser>().SingleAsync()).SecurityVersion);
    }

    [Fact]
    public async Task Existing_session_recreates_collected_attempt_window_and_records_failure()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString);
        await driver.SeedUserAsync("known", Password, []);
        Assert.Equal(HttpStatusCode.OK, (await driver.LoginAsync("known", Password)).StatusCode);
        await using (var setup = driver.Database.CreateContext()) await setup.Set<LoginAttemptWindow>().ExecuteDeleteAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await driver.PostAsync("/api/v1/auth/password/change", new { currentPassword = WrongPassword, newPassword = NewPassword })).StatusCode);
        await using var db = driver.Database.CreateContext();
        Assert.Equal(1, (await db.Set<LocalCredential>().SingleAsync()).FailedAttempts);
        Assert.Equal(1, (await db.Set<LoginAttemptWindow>().SingleAsync()).FailedAttempts);
        Assert.Single(await db.AuditEvents.Where(x => x.EventType == "identity.password.change.failed").ToListAsync());
    }
}
