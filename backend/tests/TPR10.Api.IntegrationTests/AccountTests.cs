using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Accounts;
using TPR10.Api.Identity.Passwords;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class AccountTests(PostgresFixture postgres)
{
    private const string Password = "รหัสทดสอบยาวพอ-123456";

    [Theory]
    [InlineData("ยืนยันไม่ตรงกัน-123456")]
    [InlineData("\u001b")]
    public async Task Bootstrap_confirmation_mismatch_or_cancel_does_not_create_account(string confirmation)
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var result = await BootstrapAsync(driver.Database.ConnectionString, "first-admin", confirmation);
        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain(Password, result.Output);
        if (confirmation.Length > 1) Assert.DoesNotContain(confirmation, result.Output);
        await using var db = driver.Database.CreateContext();
        Assert.Empty(await db.Set<IdentityUser>().ToListAsync());
        Assert.Empty(await db.Set<LocalCredential>().ToListAsync());
        Assert.Empty(await db.Set<IdentityRole>().ToListAsync());
        Assert.Empty(await db.AuditEvents.ToListAsync());
    }

    [Theory]
    [InlineData("", Password)]
    [InlineData("admin\u001bcontrol", Password)]
    [InlineData("admin", "short")]
    public async Task Invalid_bootstrap_input_has_no_partial_seed(string username, string password)
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await using var db = driver.Database.CreateContext();
        var service = new BootstrapService(db, new ArgonPasswordHasher(), AccountProvisioningTests.Audit(driver, db), driver.Clock);
        Assert.Equal(1, await service.CreateAsync(username, password, default));
        Assert.Empty(await db.Set<IdentityUser>().ToListAsync());
        Assert.Empty(await db.Set<IdentityRole>().ToListAsync());
        Assert.Empty(await db.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task Bootstrap_refuses_existing_non_admin_user()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await driver.SeedUserAsync("staff", Password, []);
        Assert.Equal(2, (await BootstrapAsync(driver.Database.ConnectionString, "admin")).ExitCode);
        Assert.Equal(1, await driver.CountAsync("users"));
    }

    [Fact]
    public async Task Bootstrap_rejects_redirected_input_without_reading_password()
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "TPR10.Api.dll"));
        start.ArgumentList.Add("--bootstrap-admin");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        Assert.Equal(64, process.ExitCode);
        Assert.DoesNotContain("รหัสผ่าน:", await output);
        Assert.Contains("interactive", await error);
    }

    [Fact]
    public async Task Public_registration_is_not_available()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        using var response = await driver.PostAsync("/api/v1/auth/register", new { username = "intruder" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await driver.CountAsync("users"));
    }

    [Fact]
    public async Task Bootstrap_creates_first_admin_without_echo_and_login_requires_mfa_enrollment()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var result = await BootstrapAsync(driver.Database.ConnectionString, "first-admin");
        Assert.True(result.ExitCode == 0, result.Output);
        Assert.DoesNotContain(Password, result.Output);
        await using var db = driver.Database.CreateContext();
        var user = await db.Set<IdentityUser>().SingleAsync();
        Assert.Equal("FIRST-ADMIN", user.NormalizedUsername);
        Assert.False((await db.Set<LocalCredential>().SingleAsync()).MustChangePassword);
        Assert.Equal(5, await db.Set<IdentityRole>().CountAsync());
        Assert.Contains(await db.Set<IdentityPermission>().Select(x => x.Capability).ToListAsync(), x => x == "users:manage");
        Assert.Contains(await db.Set<IdentityPermission>().Select(x => x.Capability).ToListAsync(), x => x == "roles:manage");
        Assert.Contains(await db.Set<IdentityPermission>().Select(x => x.Capability).ToListAsync(), x => x == "roles:read");
        Assert.Contains(await db.AuditEvents.Select(x => x.EventType).ToListAsync(), x => x == "identity.bootstrap");
        using var login = await driver.LoginAsync("first-admin", Password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        Assert.Equal("MfaEnrollmentRequired", body.RootElement.GetProperty("stage").GetString());
        Assert.Equal(0, body.RootElement.GetProperty("permissions").GetArrayLength());
    }

    [Fact]
    public async Task Two_bootstrap_processes_create_exactly_one_admin_and_never_overwrite()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var results = await Task.WhenAll(BootstrapAsync(driver.Database.ConnectionString, "admin-one"),
            BootstrapAsync(driver.Database.ConnectionString, "admin-two"));
        Assert.Single(results, x => x.ExitCode == 0);
        Assert.Single(results, x => x.ExitCode == 2);
        Assert.All(results, x => Assert.DoesNotContain(Password, x.Output));
        Assert.Equal(1, await driver.CountAsync("users"));
        await using var db = driver.Database.CreateContext();
        var before = await db.Set<LocalCredential>().AsNoTracking().SingleAsync();
        Assert.Equal(2, (await BootstrapAsync(driver.Database.ConnectionString, "another")).ExitCode);
        var after = await db.Set<LocalCredential>().AsNoTracking().SingleAsync();
        Assert.Equal(before.PasswordHash, after.PasswordHash);
        Assert.Single(await db.AuditEvents.Where(x => x.EventType == "identity.bootstrap").ToListAsync());
    }

    internal static async Task<(int ExitCode, string Output)> BootstrapAsync(string connectionString, string username, string confirmation = Password)
    {
        var start = new ProcessStartInfo("python3") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "BootstrapPty.py"));
        start.ArgumentList.Add(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "TPR10.Api.dll"));
        start.Environment["TPR10_CONNECTION_STRING"] = connectionString;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { username, password = Password, confirmation }));
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }
}
