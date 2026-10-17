using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TPR10.Api.IntegrationTests;

public sealed class CorrectionOpenApiTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Contracts_document_all_routes_security_strict_json_acknowledgments_and_scoped_reads(string environment)
    {
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1", environment, settings: keys.Settings);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443") });
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/openapi/v1.json"));
        var paths = doc.RootElement.GetProperty("paths");
        foreach (var (suffix, method) in new[] { ("", "get"), ("", "post"), ("/{id}", "get"), ("/approvals", "get"),
            ("/{id}/revisions", "post"), ("/{id}/submit", "post"), ("/{id}/supervisor-approve", "post"),
            ("/{id}/hr-approve", "post"), ("/{id}/return", "post"), ("/{id}/reroute", "post") })
        {
            Assert.True(paths.TryGetProperty(CorrectionApiTests.Root + suffix, out var path), suffix);
            var op = path.GetProperty(method); Assert.True(op.GetProperty("security")[0].TryGetProperty("SessionCookie", out _));
            foreach (var status in new[] { "200", "400", "401", "403", "404", "409", "429", "503" })
                Assert.True(op.GetProperty("responses").TryGetProperty(status, out _), suffix + status);
            Assert.Contains("readBasis", op.GetProperty("description").GetString());
            Assert.Equal("no-store", op.GetProperty("responses").GetProperty("200").GetProperty("headers").GetProperty("Cache-Control").GetProperty("description").GetString());
            if (method == "post")
            {
                Assert.Contains(op.GetProperty("parameters").EnumerateArray(), p => p.GetProperty("name").GetString() == "X-CSRF-Token" && p.GetProperty("required").GetBoolean());
                var body = op.GetProperty("requestBody"); Assert.True(body.GetProperty("required").GetBoolean());
                Assert.Single(body.GetProperty("content").EnumerateObject());
                var schema = Resolve(doc.RootElement, body.GetProperty("content").GetProperty("application/json").GetProperty("schema"));
                Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
                Assert.Contains(schema.GetProperty("required").EnumerateArray(), x => x.GetString() == (suffix == "/{id}/revisions" ? "command" : "commandId"));
                foreach (var code in new[] { "413", "415" }) Assert.True(op.GetProperty("responses").TryGetProperty(code, out _));
                var ack = Resolve(doc.RootElement, op.GetProperty("responses").GetProperty(suffix == "" ? "201" : "200").GetProperty("content").GetProperty("application/json").GetProperty("schema"));
                Assert.Equal(new[] { "commandId", "id" }, ack.GetProperty("properties").EnumerateObject().Select(x => x.Name).Order());
            }
        }
    }
    private static JsonElement Resolve(JsonElement root, JsonElement schema) => schema.TryGetProperty("$ref", out var r)
        ? root.GetProperty("components").GetProperty("schemas").GetProperty(r.GetString()!.Split('/')[^1]) : schema;
}
