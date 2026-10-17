using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TPR10.Api.IntegrationTests;

public sealed class AttendanceRecordingOpenApiTests
{
    private static JsonElement Resolve(JsonElement root, JsonElement schema) => schema.TryGetProperty("$ref", out var reference)
        ? root.GetProperty("components").GetProperty("schemas").GetProperty(reference.GetString()!.Split('/')[^1]) : schema;
    internal static readonly string[] Routes = ["capture-challenges", "check-ins", "check-outs", "operations/{idempotencyKey}", "current", "mine", "team", "hr", "days/{dayId}"];
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task RecordingAndHistoryDocumentResponsesSecurityAndMultipart(string environment)
    {
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1", environment, settings: keys.Settings);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443") });
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/openapi/v1.json"));
        var paths = doc.RootElement.GetProperty("paths");
        foreach (var suffix in Routes)
        {
            Assert.True(paths.TryGetProperty("/api/v1/attendance/" + suffix, out var path), suffix);
            var mutation = suffix is "capture-challenges" or "check-ins" or "check-outs";
            var operation = path.GetProperty(mutation ? "post" : "get");
            Assert.True(operation.GetProperty("security")[0].TryGetProperty("SessionCookie", out _));
            if (mutation) Assert.Contains(operation.GetProperty("parameters").EnumerateArray(), p => p.GetProperty("name").GetString() == "X-CSRF-Token" && p.GetProperty("required").GetBoolean());
            var responses = operation.GetProperty("responses");
            if (suffix == "capture-challenges")
            {
                var schema = Resolve(doc.RootElement, operation.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema"));
                var actionSchema = Resolve(doc.RootElement, schema.GetProperty("properties").GetProperty("action"));
                Assert.Equal("string", actionSchema.GetProperty("type").GetString());
                Assert.Equal(new[] { "CheckIn", "CheckOut" }, actionSchema.GetProperty("enum").EnumerateArray().Select(x => x.GetString()));
            }
            if (suffix is "mine" or "team" or "hr")
            {
                var names = operation.GetProperty("parameters").EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToArray();
                foreach (var name in new[] { "from", "to", "pageSize", "cursor" }) Assert.Contains(name, names);
            }
            foreach (var status in new[] { "200", "400", "401", "403", "404", "409", "429", "503" }) Assert.True(responses.TryGetProperty(status, out _), suffix + status);
            Assert.Equal("no-store", responses.GetProperty("200").GetProperty("headers").GetProperty("Cache-Control").GetProperty("description").GetString());
            if (mutation) Assert.True(responses.TryGetProperty("201", out _));
            if (suffix is "check-ins" or "check-outs")
            {
                Assert.True(responses.TryGetProperty("413", out _)); Assert.True(responses.TryGetProperty("415", out _));
                var body = operation.GetProperty("requestBody").GetProperty("content").GetProperty("multipart/form-data").GetProperty("schema");
                Assert.Equal(new[] { "metadata", "photo" }, body.GetProperty("required").EnumerateArray().Select(x => x.GetString()).Order());
                Assert.True(responses.GetProperty("429").GetProperty("headers").TryGetProperty("Retry-After", out _));
            }
        }
    }
}
