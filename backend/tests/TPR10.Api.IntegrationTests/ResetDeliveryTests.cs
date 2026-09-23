using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Reset;
using static TPR10.Api.IntegrationTests.PasswordResetTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class ResetDeliveryTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("expired")]
    [InlineData("consumed")]
    [InlineData("revoked")]
    [InlineData("inactive")]
    [InlineData("recipient-changed")]
    public async Task Dispatcher_does_not_deliver_invalid_request(string state)
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString, keys.Settings);
        await driver.SeedUserAsync("known", Password, []);
        await using (var setup = driver.Database.CreateContext())
        {
            (await setup.Set<IdentityUser>().SingleAsync()).Email = "test@example.invalid";
            await setup.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Accepted, (await driver.PostAsync("/api/v1/auth/password-reset/request", new { username = "known" })).StatusCode);
        Guid id;
        await using (var setup = driver.Database.CreateContext())
        {
            var request = await setup.Set<PasswordResetRequest>().SingleAsync();
            id = request.Id;
            if (state == "expired") driver.Advance(TimeSpan.FromMinutes(15));
            if (state == "consumed") request.ConsumedAtUtc = driver.Clock.GetUtcNow();
            if (state == "revoked") request.RevokedAtUtc = driver.Clock.GetUtcNow();
            if (state == "inactive") (await setup.Set<IdentityUser>().SingleAsync()).IsActive = false;
            if (state == "recipient-changed") (await setup.Set<IdentityUser>().SingleAsync()).Email = "changed@example.invalid";
            await setup.SaveChangesAsync();
        }
        await using var scope = driver.Factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<ResetOutboxDispatcher>().DispatchAsync(id, default));
        Assert.Null(driver.Factory.Services.GetRequiredService<DevelopmentResetSink>().Take(id));
        await using var check = driver.Database.CreateContext();
        Assert.Equal(0, (await check.Set<IdentityDeliveryOutbox>().SingleAsync()).Attempts);
    }

    [Fact]
    public async Task Development_sink_is_DI_only_and_can_deliver_a_usable_reset()
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString, keys.Settings);
        Assert.NotNull(driver.Factory.Services.GetService<IResetTransport>());
        await driver.SeedUserAsync("known", Password, []);
        await using (var setup = driver.Database.CreateContext())
        {
            (await setup.Set<IdentityUser>().SingleAsync()).Email = "test@example.invalid";
            await setup.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Accepted, (await driver.PostAsync("/api/v1/auth/password-reset/request", new { username = "known" })).StatusCode);
        await using var scope = driver.Factory.Services.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetService<ResetOutboxDispatcher>();
        Assert.NotNull(dispatcher);
        await using var db = driver.Database.CreateContext();
        var id = (await db.Set<PasswordResetRequest>().SingleAsync()).Id;
        Assert.True(await dispatcher.DispatchAsync(id, default));
        var sink = driver.Factory.Services.GetRequiredService<DevelopmentResetSink>();
        var token = sink.Take(id);
        Assert.NotNull(token);
        Assert.Null(sink.Take(id));
        Assert.Equal(HttpStatusCode.NoContent, (await driver.PostAsync("/api/v1/auth/password-reset/complete", new { token, password = NewPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await driver.Client.GetAsync("/api/v1/auth/password-reset/tokens")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await driver.Client.GetAsync("/api/v1/auth/password-reset/outbox")).StatusCode);
    }

    [Fact]
    public async Task Production_refuses_configuration_claiming_email_delivery_is_enabled()
    {
        using var keys = new TestKeyMaterial();
        keys.Settings["Identity:Reset:EmailEnabled"] = "true";
        await using var database = new IdentityDatabase(fixture.ConnectionString);
        await database.InitializeAsync();
        await using var factory = new ApiFactory(database.ConnectionString, "Production", settings: keys.Settings);
        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => factory.CreateClient());
    }

    [Fact]
    public async Task Disabled_email_configuration_does_not_queue_in_development()
    {
        using var keys = new TestKeyMaterial();
        keys.Settings["Identity:Reset:EmailEnabled"] = "false";
        await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString, keys.Settings);
        await driver.SeedUserAsync("known", Password, []);
        await using (var setup = driver.Database.CreateContext())
        {
            (await setup.Set<IdentityUser>().SingleAsync()).Email = "test@example.invalid";
            await setup.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Accepted, (await driver.PostAsync("/api/v1/auth/password-reset/request", new { username = "known" })).StatusCode);
        await using var db = driver.Database.CreateContext();
        Assert.Empty(await db.Set<PasswordResetRequest>().ToListAsync());
    }

    [Fact]
    public async Task Failed_delivery_retries_same_token_without_changing_credentials()
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString, keys.Settings);
        await driver.SeedUserAsync("known", Password, []);
        await using (var setup = driver.Database.CreateContext())
        {
            (await setup.Set<IdentityUser>().SingleAsync()).Email = "test@example.invalid";
            await setup.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Accepted, (await driver.PostAsync("/api/v1/auth/password-reset/request", new { username = "known" })).StatusCode);
        var transport = new FailingOnceTransport();
        Guid requestId;
        string hash;
        string payload;
        await using (var db = driver.Database.CreateContext())
        {
            var outbox = await db.Set<IdentityDeliveryOutbox>().SingleAsync();
            requestId = outbox.RequestId;
            hash = (await db.Set<LocalCredential>().SingleAsync()).PasswordHash;
            payload = outbox.ProtectedPayload;
            var dispatcher = new ResetOutboxDispatcher(db, driver.Factory.Services.GetRequiredService<IDataProtectionProvider>(), transport, driver.Clock);
            Assert.False(await dispatcher.DispatchAsync(requestId, default));
        }
        driver.Advance(TimeSpan.FromMinutes(1));
        await using (var db = driver.Database.CreateContext())
        {
            Assert.Equal(hash, (await db.Set<LocalCredential>().SingleAsync()).PasswordHash);
            var outbox = await db.Set<IdentityDeliveryOutbox>().SingleAsync();
            Assert.Equal(1, outbox.Attempts);
            Assert.Null(outbox.DeliveredAtUtc);
            Assert.Equal(payload, outbox.ProtectedPayload);
            var dispatcher = new ResetOutboxDispatcher(db, driver.Factory.Services.GetRequiredService<IDataProtectionProvider>(), transport, driver.Clock);
            Assert.True(await dispatcher.DispatchAsync(requestId, default));
        }
        await using var check = driver.Database.CreateContext();
        Assert.Single(await check.Set<PasswordResetRequest>().ToListAsync());
        Assert.NotNull((await check.Set<IdentityDeliveryOutbox>().SingleAsync()).DeliveredAtUtc);
        Assert.Equal(2, (await check.Set<IdentityDeliveryOutbox>().SingleAsync()).Attempts);
        Assert.Equal(transport.FirstToken, transport.DeliveredToken);
        Assert.Equal(HttpStatusCode.NoContent, (await driver.PostAsync("/api/v1/auth/password-reset/complete", new { token = transport.DeliveredToken, password = NewPassword })).StatusCode);
    }

    [Fact]
    public async Task Production_request_never_queues_email_without_adapter()
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString, keys.Settings);
        await driver.SeedUserAsync("known", Password, []);
        await using (var setup = driver.Database.CreateContext())
        {
            (await setup.Set<IdentityUser>().SingleAsync()).Email = "test@example.invalid";
            await setup.SaveChangesAsync();
        }
        await using var production = new ApiFactory(driver.Database.ConnectionString, "Production", driver.Clock, keys.Settings);
        using var client = await production.CreateCsrfClientAsync();
        Assert.Null(production.Services.GetService<IResetTransport>());
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(client, "/api/v1/auth/password-reset/request", new { username = "known" })).StatusCode);
        await using var db = driver.Database.CreateContext();
        Assert.Empty(await db.Set<PasswordResetRequest>().ToListAsync());
        Assert.Empty(await db.Set<IdentityDeliveryOutbox>().ToListAsync());
    }

    private sealed class FailingOnceTransport : IResetTransport
    {
        public string? FirstToken { get; private set; }
        public string? DeliveredToken { get; private set; }
        public Task SendAsync(Guid requestId, string recipient, string token, CancellationToken ct)
        {
            if (FirstToken is null) { FirstToken = token; throw new HttpRequestException("simulated transport failure"); }
            DeliveredToken = token;
            return Task.CompletedTask;
        }
    }
}
