using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Passwords;

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

    public static HttpRequestMessage Mutation(string token, string path = "/api/v1/auth/not-implemented")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("Origin", "https://localhost:4443");
        request.Headers.Add("X-CSRF-Token", token);
        request.Content = JsonContent.Create(new { note = "csrf-test" });
        return request;
    }

    public async Task<Guid> SeedUserAsync(string username, string password, string[] permissions, bool requiresMfa = false, bool mustChangePassword = false)
    {
        await using var db = Database.CreateContext();
        var id = Guid.NewGuid();
        db.Add(new IdentityUser { Id = id, Username = username, NormalizedUsername = UsernameNormalizer.Normalize(username)!, CreatedAtUtc = Clock.GetUtcNow() });
        db.Add(new LocalCredential
        {
            UserId = id,
            PasswordHash = await new ArgonPasswordHasher().HashAsync(password, default),
            MustChangePassword = mustChangePassword,
            PasswordChangedAtUtc = Clock.GetUtcNow()
        });
        var roleId = Guid.NewGuid();
        db.Add(new IdentityRole { Id = roleId, Name = "test-" + roleId, RoleClass = requiresMfa ? "system-administration" : "staff", CreatedAtUtc = Clock.GetUtcNow() });
        db.Add(new UserRole { UserId = id, RoleId = roleId, CreatedAtUtc = Clock.GetUtcNow() });
        foreach (var capability in permissions)
        {
            var permission = await db.Set<IdentityPermission>().SingleOrDefaultAsync(x => x.Capability == capability);
            if (permission is null) { permission = new IdentityPermission { Id = Guid.NewGuid(), Capability = capability }; db.Add(permission); }
            db.Add(new RolePermission { RoleId = roleId, PermissionId = permission.Id });
        }
        await db.SaveChangesAsync();
        return id;
    }

    public async Task<HttpResponseMessage> PostAsync(string path, object body)
    {
        using var request = Mutation(await TokenAsync(Client), path);
        request.Content = JsonContent.Create(body);
        return await Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> LoginAsync(string username, string password) => PostAsync("/api/v1/auth/login", new { username, password });
    public void Advance(TimeSpan delta) => Clock.Advance(delta);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
        await Database.DisposeAsync();
    }

    private sealed record TokenResponse(string Token);
}
