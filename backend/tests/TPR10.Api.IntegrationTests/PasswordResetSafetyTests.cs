using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Passwords;
using static TPR10.Api.IntegrationTests.PasswordResetTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class PasswordResetSafetyTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("request")]
    [InlineData("change")]
    [InlineData("admin")]
    public async Task Audit_failure_rolls_back_every_reset_mutation(string operation)
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString, keys.Settings);
        Guid id;
        string path;
        object body;
        if (operation == "admin")
        {
            await RoleAuthorizationTests.AdminAsync(driver);
            id = await driver.SeedUserAsync("target", Password, []);
            path = $"/api/v1/users/{id}/password-reset";
            body = new { };
        }
        else
        {
            id = await driver.SeedUserAsync("target", Password, [], mustChangePassword: operation == "change");
            if (operation == "change") Assert.Equal(HttpStatusCode.OK, (await driver.LoginAsync("target", Password)).StatusCode);
            path = operation == "change" ? "/api/v1/auth/password/change" : "/api/v1/auth/password-reset/request";
            body = operation == "change" ? new { currentPassword = Password, newPassword = NewPassword } : new { username = "target" };
        }
        using var existing = driver.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(existing, "/api/v1/auth/login", new { username = "target", password = Password })).StatusCode);
        var csrf = await IdentityTestDriver.TokenAsync(driver.Client);
        string original;
        await using (var setup = driver.Database.CreateContext())
        {
            (await setup.Set<IdentityUser>().SingleAsync(x => x.Id == id)).Email = "test@example.invalid";
            original = (await setup.Set<LocalCredential>().SingleAsync(x => x.UserId == id)).PasswordHash;
            await setup.SaveChangesAsync();
            await setup.Database.ExecuteSqlRawAsync("CREATE FUNCTION reject_reset_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'audit fault'; END $$; CREATE TRIGGER reject_reset_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION reject_reset_audit();");
        }
        using var request = IdentityTestDriver.Mutation(csrf, path);
        request.Content = JsonContent.Create(body);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await driver.Client.SendAsync(request)).StatusCode);
        await using var check = driver.Database.CreateContext();
        Assert.Equal(original, (await check.Set<LocalCredential>().SingleAsync(x => x.UserId == id)).PasswordHash);
        Assert.Equal(0, (await check.Set<IdentityUser>().SingleAsync(x => x.Id == id)).SecurityVersion);
        Assert.All(await check.Set<IdentitySession>().Where(x => x.UserId == id).ToListAsync(), row => Assert.Null(row.RevokedAtUtc));
        Assert.Empty(await check.Set<PasswordResetRequest>().ToListAsync());
        Assert.Empty(await check.Set<IdentityDeliveryOutbox>().ToListAsync());
        Assert.Equal(HttpStatusCode.OK, (await existing.GetAsync("/api/v1/auth/session")).StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/auth/password-reset/request")]
    [InlineData("/api/v1/auth/password-reset/complete")]
    [InlineData("/api/v1/auth/password/change")]
    public async Task Reset_mutations_require_csrf_and_origin(string path)
    {
        await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString);
        Assert.Equal(HttpStatusCode.Forbidden, (await driver.Client.PostAsJsonAsync(path, new { })).StatusCode);
        using var request = IdentityTestDriver.Mutation(await IdentityTestDriver.TokenAsync(driver.Client), path);
        request.Headers.Remove("Origin");
        request.Headers.Add("Origin", "https://attacker.invalid");
        Assert.Equal(HttpStatusCode.Forbidden, (await driver.Client.SendAsync(request)).StatusCode);
        await using var db = driver.Database.CreateContext();
        Assert.Empty(await db.Set<PasswordResetRequest>().ToListAsync());
    }

    [Fact]
    public async Task Password_change_revokes_all_previous_reset_tokens()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString);
        var id = await driver.SeedUserAsync("known", Password, []);
        var token = await SeedResetAsync(driver, id);
        Assert.Equal(HttpStatusCode.OK, (await driver.LoginAsync("known", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await driver.PostAsync("/api/v1/auth/password/change", new { currentPassword = Password, newPassword = NewPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await driver.PostAsync("/api/v1/auth/password-reset/complete", new { token, password = Password })).StatusCode);
    }

    [Fact]
    public async Task Token_of_another_account_cannot_change_logged_in_account()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString);
        await driver.SeedUserAsync("first", Password, []);
        var second = await driver.SeedUserAsync("second", Password, []);
        var token = await SeedResetAsync(driver, second);
        Assert.Equal(HttpStatusCode.OK, (await driver.LoginAsync("first", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await driver.PostAsync("/api/v1/auth/password-reset/complete", new { token, password = NewPassword })).StatusCode);
        await using var db = driver.Database.CreateContext();
        Assert.Null((await db.Set<PasswordResetRequest>().SingleAsync()).ConsumedAtUtc);
        foreach (var credential in await db.Set<LocalCredential>().ToListAsync())
            Assert.True(await new ArgonPasswordHasher().VerifyAsync(Password, credential.PasswordHash, default));
    }

    [Fact]
    public async Task Audit_failure_rolls_back_consumption_password_and_revocation_then_token_can_retry()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString);
        var id = await driver.SeedUserAsync("known", Password, []);
        Assert.Equal(HttpStatusCode.OK, (await driver.LoginAsync("known", Password)).StatusCode);
        var token = await SeedResetAsync(driver, id);
        using var anonymous = driver.NewClient();
        var csrf = await IdentityTestDriver.TokenAsync(anonymous);
        await using (var setup = driver.Database.CreateContext())
            await setup.Database.ExecuteSqlRawAsync("CREATE FUNCTION reject_reset_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'audit fault'; END $$; CREATE TRIGGER reject_reset_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION reject_reset_audit();");
        using var request = IdentityTestDriver.Mutation(csrf, "/api/v1/auth/password-reset/complete");
        request.Content = JsonContent.Create(new { token, password = NewPassword });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await anonymous.SendAsync(request)).StatusCode);
        await using (var check = driver.Database.CreateContext())
        {
            Assert.Null((await check.Set<PasswordResetRequest>().SingleAsync()).ConsumedAtUtc);
            Assert.Null((await check.Set<PasswordResetRequest>().SingleAsync()).RevokedAtUtc);
            Assert.Null((await check.Set<IdentitySession>().SingleAsync()).RevokedAtUtc);
            Assert.Equal(0, (await check.Set<IdentityUser>().SingleAsync()).SecurityVersion);
            Assert.True(await new ArgonPasswordHasher().VerifyAsync(Password, (await check.Set<LocalCredential>().SingleAsync()).PasswordHash, default));
            await check.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_reset_audit ON audit_events");
        }
        Assert.Equal(HttpStatusCode.OK, (await driver.Client.GetAsync("/api/v1/auth/session")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(anonymous, "/api/v1/auth/password-reset/complete", new { token, password = NewPassword })).StatusCode);
    }

    [Theory]
    [InlineData("short", NewPassword)]
    [InlineData(Password, "short")]
    [InlineData(Password, Password)]
    public async Task Invalid_change_preserves_password_session_and_reset(string currentPassword, string newPassword)
    {
        await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString);
        var id = await driver.SeedUserAsync("known", Password, [], mustChangePassword: true);
        await SeedResetAsync(driver, id);
        Assert.Equal(HttpStatusCode.OK, (await driver.LoginAsync("known", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await driver.PostAsync("/api/v1/auth/password/change", new { currentPassword, newPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await driver.Client.GetAsync("/api/v1/auth/session")).StatusCode);
        await using var db = driver.Database.CreateContext();
        Assert.True((await db.Set<LocalCredential>().SingleAsync()).MustChangePassword);
        Assert.Null((await db.Set<PasswordResetRequest>().SingleAsync()).RevokedAtUtc);
    }

    [Fact]
    public async Task Repeated_requests_do_not_mint_more_tokens()
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString, keys.Settings);
        await driver.SeedUserAsync("known", Password, []);
        await using (var setup = driver.Database.CreateContext())
        {
            (await setup.Set<IdentityUser>().SingleAsync()).Email = "test@example.invalid";
            await setup.SaveChangesAsync();
        }
        for (var i = 0; i < 2; i++) Assert.Equal(HttpStatusCode.Accepted, (await driver.PostAsync("/api/v1/auth/password-reset/request", new { username = "known" })).StatusCode);
        await using var db = driver.Database.CreateContext();
        Assert.Single(await db.Set<PasswordResetRequest>().ToListAsync());
        Assert.Single(await db.Set<IdentityDeliveryOutbox>().ToListAsync());
    }
}
