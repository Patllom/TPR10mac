using System.Data.Common;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Data;
using TPR10.Api.Identity;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Sessions;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class LogoutRotationRaceTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Rotation_winning_after_logout_authentication_cannot_return_false_logout_success()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var (cookie, token, _) = await LoginAsync(driver);
        var gate = new CommandGate(pauseAfterActivity: true);
        await using var factory = driver.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<Tpr10DbContext>(options => options.AddInterceptors(gate))));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443"), HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        // This host has its own ephemeral Data Protection keys; obtain its session-bound token first.
        var csrf = await IdentityTestDriver.TokenAsync(client);
        gate.Armed = true;
        using var request = IdentityTestDriver.Mutation(csrf, "/api/v1/auth/logout");
        var logout = client.SendAsync(request);
        IssuedSession rotated;
        try
        {
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var db = driver.Database.CreateContext();
            await using var transaction = await db.Database.BeginTransactionAsync();
            rotated = await new SessionService(db, driver.Clock, new RequestSession(), new Organization.EffectiveRolePolicy(db)).RotateAsync(token, SessionStage.Active, null, default);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        finally { gate.Resume.TrySetResult(); }
        using var response = await logout;
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        await using var observer = driver.Database.CreateContext();
        Assert.False(await observer.AuditEvents.AnyAsync(x => x.EventType == "identity.logout"));
        Assert.NotNull(await new SessionService(observer, driver.Clock, new RequestSession(), new Organization.EffectiveRolePolicy(observer)).ValidateAsync(rotated.Token, default));
    }

    [Fact]
    public async Task Logout_winning_before_rotation_lock_prevents_a_new_session()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var (_, token, csrf) = await LoginAsync(driver);
        var gate = new CommandGate(pauseAfterActivity: false);
        await using var db = new Tpr10DbContext(new DbContextOptionsBuilder<Tpr10DbContext>()
            .UseNpgsql(driver.Database.ConnectionString).AddInterceptors(gate).Options);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var rotation = new SessionService(db, driver.Clock, new RequestSession(), new Organization.EffectiveRolePolicy(db)).RotateAsync(token, SessionStage.Active, null, default);
        try
        {
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var request = IdentityTestDriver.Mutation(csrf, "/api/v1/auth/logout");
            using var response = await driver.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        finally { gate.Resume.TrySetResult(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => rotation);
        await transaction.RollbackAsync();
        await using var observer = driver.Database.CreateContext();
        Assert.NotNull((await observer.Set<IdentitySession>().SingleAsync()).RevokedAtUtc);
        Assert.Single(await observer.AuditEvents.Where(x => x.EventType == "identity.logout").ToListAsync());
    }

    private static async Task<(string Cookie, string Token, string Csrf)> LoginAsync(IdentityTestDriver driver)
    {
        const string password = "รหัสทดสอบยาวพอ-123456";
        await driver.SeedUserAsync("staff", password, []);
        using var login = await driver.LoginAsync("staff", password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("__Host-tpr10_session=")).Split(';')[0];
        return (cookie, cookie.Split('=')[1], await IdentityTestDriver.TokenAsync(driver.Client));
    }

    // Gates synchronize real SQL requests without sleep or a production test hook.
    private sealed class CommandGate(bool pauseAfterActivity) : DbCommandInterceptor
    {
        public bool Armed { get; set; } = !pauseAfterActivity;
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (Armed && pauseAfterActivity && command.CommandText.Contains("UPDATE sessions") && command.CommandText.Contains("last_seen_at_utc"))
            {
                Reached.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!pauseAfterActivity && command.CommandText.Contains("FROM sessions WHERE token_hash=") && command.CommandText.Contains("FOR UPDATE"))
            {
                Reached.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
