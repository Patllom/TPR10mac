using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TPR10.Api.IntegrationTests;

public sealed class EvidenceOpenApiTests
{
    public static IEnumerable<string> ExpectedOperations() =>
    ["get /api/v1/attendance/evidence/{id}", "get /api/v1/attendance/evidence/{id}/thumbnail", "get /api/v1/attendance/evidence/{id}/download"];
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Evidence_documents_three_protected_read_routes_without_publisher(string environment)
    {
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1", environment, settings: keys.Settings);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443") });
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/openapi/v1.json"));
        var operations = doc.RootElement.GetProperty("paths").EnumerateObject().Where(x => x.Name.StartsWith("/api/v1/attendance/evidence/", StringComparison.Ordinal))
            .SelectMany(x => x.Value.EnumerateObject().Select(m => (Name: $"{m.Name} {x.Name}", Op: m.Value))).ToArray();
        Assert.Equal(ExpectedOperations().Order(), operations.Select(x => x.Name).Order());
        foreach (var (_, op) in operations)
        {
            Assert.Equal("attendance-evidence", op.GetProperty("x-tpr10-scope").GetString());
            Assert.Empty(op.GetProperty("x-tpr10-permissions").EnumerateArray());
            Assert.Contains("CanReadPhoto", op.GetProperty("description").GetString());
            Assert.Contains("shared", op.GetProperty("description").GetString());
            var responses = op.GetProperty("responses");
            Assert.True(responses.GetProperty("200").GetProperty("content").TryGetProperty("image/jpeg", out _));
            Assert.True(responses.TryGetProperty("416", out _));
            Assert.Equal("no-store", responses.GetProperty("200").GetProperty("headers").GetProperty("Cache-Control").GetProperty("description").GetString());
        }
    }
}
