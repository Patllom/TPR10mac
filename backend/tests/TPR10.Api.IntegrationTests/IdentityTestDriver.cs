using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace TPR10.Api.IntegrationTests;

internal sealed class IdentityTestDriver : IAsyncDisposable
{
    public IdentityDatabase Database { get; }
    public ManualTimeProvider Clock { get; } = new();
    public ApiFactory Factory { get; }
    public HttpClient Client { get; }

    private IdentityTestDriver(IdentityDatabase database, Dictionary<string, string?>? settings)
    {
        Database = database;
        Factory = new ApiFactory(database.ConnectionString, clock: Clock, settings: settings);
        Client = NewClient();
    }

    public HttpClient NewClient() => Factory.CreateClient(new WebApplicationFactoryClientOptions
    { BaseAddress = new Uri("https://localhost:4443"), AllowAutoRedirect = false });

    public static async Task<IdentityTestDriver> CreateAsync(string connectionString, Dictionary<string, string?>? settings = null)
    {
        var database = new IdentityDatabase(connectionString);
        await database.InitializeAsync();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        return new IdentityTestDriver(database, settings);
    }

    public static async Task<string> TokenAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/auth/csrf");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!.Token;
    }

    public static HttpRequestMessage Mutation(string token, string path = "/api/v1/auth/login")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("Origin", "https://localhost:4443");
        request.Headers.Add("X-CSRF-Token", token);
        request.Content = JsonContent.Create(new { note = "csrf-test" });
        return request;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
        await Database.DisposeAsync();
    }

    private sealed record TokenResponse(string Token);
}
