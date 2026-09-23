using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Identity;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Sessions;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class SessionRotationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Revoke_user_also_invalidates_a_session_issued_after_its_session_snapshot()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var id = await driver.SeedUserAsync("staff", "รหัสทดสอบยาวพอ-123456", []);
        await using var revoking = driver.Database.CreateContext();
        var revoke = new SessionService(revoking, driver.Clock, new RequestSession());
        await revoke.RevokeUserAsync(id, "test", default);
        await using var issuing = driver.Database.CreateContext();
        var issue = new SessionService(issuing, driver.Clock, new RequestSession());
        var late = await issue.IssueAsync(id, SessionStage.Active, default);
        await issuing.SaveChangesAsync();
        await revoking.SaveChangesAsync();
        Assert.Null(await issue.ValidateAsync(late.Token, default));
    }

    [Fact]
    public async Task Rotation_revokes_old_cookie_and_csrf_without_extending_absolute_lifetime()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await driver.SeedUserAsync("staff", "รหัสทดสอบยาวพอ-123456", []);
        using var login = await driver.LoginAsync("staff", "รหัสทดสอบยาวพอ-123456");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var oldToken = login.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("__Host-tpr10_session=")).Split(';')[0].Split('=')[1];
        var csrf = await IdentityTestDriver.TokenAsync(driver.Client);
        driver.Advance(TimeSpan.FromMinutes(20));
        await using var db = driver.Database.CreateContext();
        var original = await db.Set<IdentitySession>().AsNoTracking().SingleAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var service = new SessionService(db, driver.Clock, new RequestSession());
        var rotated = await service.RotateAsync(oldToken, SessionStage.Active, null, default);
        await using (var observer = driver.Database.CreateContext())
        {
            Assert.Single(await observer.Set<IdentitySession>().ToListAsync());
            Assert.Null((await observer.Set<IdentitySession>().SingleAsync()).RevokedAtUtc);
        }
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        Assert.NotEqual(oldToken, rotated.Token);
        var next = await db.Set<IdentitySession>().SingleAsync(x => x.RevokedAtUtc == null);
        Assert.Equal(original.ExpiresAtUtc, next.ExpiresAtUtc);
        Assert.Equal(original.CreatedAtUtc, next.CreatedAtUtc);
        Assert.Equal(HttpStatusCode.Unauthorized, (await driver.Client.GetAsync("/api/v1/auth/session")).StatusCode);
        using var client = driver.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443"), HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", "__Host-tpr10_session=" + rotated.Token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/session")).StatusCode);
        using var request = IdentityTestDriver.Mutation(csrf, "/api/v1/auth/logout");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Issue_and_revoke_are_not_saved_without_caller_commit()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var id = await driver.SeedUserAsync("staff", "รหัสทดสอบยาวพอ-123456", []);
        await using var db = driver.Database.CreateContext();
        var service = new SessionService(db, driver.Clock, new RequestSession());
        var issued = await service.IssueAsync(id, SessionStage.Active, default);
        await using var observer = driver.Database.CreateContext();
        Assert.Empty(await observer.Set<IdentitySession>().ToListAsync());
        await db.SaveChangesAsync();
        await service.RevokeUserAsync(id, "test", default);
        Assert.Null((await observer.Set<IdentitySession>().AsNoTracking().SingleAsync()).RevokedAtUtc);
        await db.SaveChangesAsync();
        Assert.NotNull((await observer.Set<IdentitySession>().AsNoTracking().SingleAsync()).RevokedAtUtc);
        Assert.Null(await service.ValidateAsync(issued.Token, default));
    }
}
