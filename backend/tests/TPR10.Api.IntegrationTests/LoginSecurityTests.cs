using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TPR10.Api.Identity;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Passwords;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class LoginSecurityTests(PostgresFixture postgres)
{
    private const string Password = "รหัสทดสอบยาวพอ-123456";

    [Fact]
    public async Task Unknown_identifier_performs_real_dummy_password_work_and_returns_generic_401()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var hasher = new ObservedHasher();
        await using var factory = driver.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        { services.RemoveAll<IPasswordHasher>(); services.AddSingleton<IPasswordHasher>(hasher); }));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443") });
        using var request = IdentityTestDriver.Mutation(await IdentityTestDriver.TokenAsync(client), "/api/v1/auth/login");
        request.Content = JsonContent.Create(new { username = "missing", password = Password });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, hasher.CompletedVerifications);
        await using var db = driver.Database.CreateContext();
        Assert.Empty(await db.Set<IdentitySession>().ToListAsync());
        Assert.Equal(1, (await db.Set<LoginAttemptWindow>().SingleAsync()).FailedAttempts);
    }

    [Fact]
    public async Task Raw_session_token_and_password_never_appear_in_application_logs()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await driver.SeedUserAsync("staff", Password, []);
        using var logs = new CapturedLogs();
        await using var factory = driver.Factory.WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(logs)));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443") });
        using var request = IdentityTestDriver.Mutation(await IdentityTestDriver.TokenAsync(client), "/api/v1/auth/login");
        request.Content = JsonContent.Create(new { username = "staff", password = Password });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = response.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("__Host-tpr10_session=")).Split(';')[0].Split('=')[1];
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/session")).StatusCode);
        Assert.NotEmpty(logs.Messages);
        Assert.DoesNotContain(logs.Messages, x => x.Contains(token) || x.Contains(Password));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("staff", null)]
    public async Task Invalid_credentials_are_rejected_without_session(string? username, string? password)
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        using var response = await driver.PostAsync("/api/v1/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await using var db = driver.Database.CreateContext();
        Assert.Empty(await db.Set<IdentitySession>().ToListAsync());
    }

    private sealed class ObservedHasher : IPasswordHasher
    {
        private readonly ArgonPasswordHasher inner = new();
        public int CompletedVerifications { get; private set; }
        public Task<string> HashAsync(string password, CancellationToken ct) => inner.HashAsync(password, ct);
        public async Task<bool> VerifyAsync(string password, string encoded, CancellationToken ct)
        {
            var result = await inner.VerifyAsync(password, encoded, ct);
            CompletedVerifications++;
            return result;
        }
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturedLogger(Messages);
        public void Dispose() { }
        private sealed class CapturedLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => messages.Enqueue(formatter(state, exception));
        }
    }
}
