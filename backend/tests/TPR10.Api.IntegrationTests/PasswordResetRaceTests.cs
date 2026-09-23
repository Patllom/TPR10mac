using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Reset;
using TPR10.Api.Identity.Sessions;
using static TPR10.Api.IntegrationTests.PasswordResetTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class PasswordResetRaceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Password_change_racing_reset_cannot_restore_revoked_credentials()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString);
        var id = await driver.SeedUserAsync("known", Password, []);
        Assert.Equal(HttpStatusCode.OK, (await driver.LoginAsync("known", Password)).StatusCode);
        var token = await SeedResetAsync(driver, id);
        using var anonymous = driver.NewClient();
        var responses = await Task.WhenAll(driver.PostAsync("/api/v1/auth/password/change", new { currentPassword = Password, newPassword = NewPassword }),
            PostAsync(anonymous, "/api/v1/auth/password-reset/complete", new { token, password = "another-new-password-123456" }));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(responses, x => x.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
        await using var db = driver.Database.CreateContext();
        Assert.Equal(1, (await db.Set<IdentityUser>().SingleAsync()).SecurityVersion);
        Assert.All(await db.Set<IdentitySession>().ToListAsync(), x => Assert.NotNull(x.RevokedAtUtc));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reset_mutations_recheck_revoked_session_after_authentication(bool admin)
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(driver);
        var target = await driver.SeedUserAsync("target", Password, []);
        await using var db = driver.Database.CreateContext();
        var session = await db.Set<IdentitySession>().AsNoTracking().SingleAsync(x => x.UserId == actor && x.RevokedAtUtc == null);
        using var scope = driver.Factory.Services.CreateScope();
        var current = scope.ServiceProvider.GetRequiredService<RequestSession>();
        current.Entity = session;
        await db.Set<IdentitySession>().Where(x => x.Id == session.Id).ExecuteUpdateAsync(x => x.SetProperty(s => s.RevokedAtUtc, driver.Clock.GetUtcNow()));
        var service = scope.ServiceProvider.GetRequiredService<PasswordResetService>();
        var result = admin ? await service.AdminResetAsync(actor, target, default)
            : await service.ChangeAsync(new(Password, NewPassword), new DefaultHttpContext(), current, default);
        Assert.Equal(admin ? 403 : 401, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.All(await db.Set<IdentityUser>().ToListAsync(), x => Assert.Equal(0, x.SecurityVersion));
    }

    [Fact]
    public async Task Reset_racing_totp_does_not_leave_an_authenticated_session()
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString, keys.Settings);
        var id = await driver.SeedUserAsync("known", Password, ["system:probe"], requiresMfa: true);
        Assert.Equal(HttpStatusCode.OK, (await driver.LoginAsync("known", Password)).StatusCode);
        var enroll = await driver.PostAsync("/api/v1/auth/mfa/enroll", new { });
        var secret = MfaTests.Secret(await enroll.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await driver.PostAsync("/api/v1/auth/mfa/confirm", new { code = MfaTests.Code(secret, driver.Clock.GetUtcNow()) })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await driver.PostAsync("/api/v1/auth/logout", new { })).StatusCode);
        driver.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await driver.LoginAsync("known", Password)).StatusCode);
        var token = await SeedResetAsync(driver, id);
        using var anonymous = driver.NewClient();
        var results = await Task.WhenAll(driver.PostAsync("/api/v1/auth/mfa/challenge", new { code = MfaTests.Code(secret, driver.Clock.GetUtcNow()) }),
            PostAsync(anonymous, "/api/v1/auth/password-reset/complete", new { token, password = NewPassword }));
        Assert.Equal(HttpStatusCode.NoContent, results[1].StatusCode);
        Assert.Contains(results[0].StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        Assert.Equal(HttpStatusCode.Unauthorized, (await driver.Client.GetAsync("/api/v1/auth/session")).StatusCode);
        await using var db = driver.Database.CreateContext();
        Assert.All(await db.Set<IdentitySession>().ToListAsync(), x => Assert.NotNull(x.RevokedAtUtc));
        Assert.Null((await db.Set<MfaFactor>().SingleAsync()).RevokedAtUtc);
        var login = await PostAsync(anonymous, "/api/v1/auth/login", new { username = "known", password = NewPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("MfaChallengeRequired", await login.Content.ReadAsStringAsync());
    }
}
