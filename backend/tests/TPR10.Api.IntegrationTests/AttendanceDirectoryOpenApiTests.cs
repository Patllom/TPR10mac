using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TPR10.Api.IntegrationTests;

public sealed class AttendanceDirectoryOpenApiTests
{
    public static IEnumerable<string> ExpectedOperations()
    {
        yield return "get /api/v1/attendance/access";
        foreach (var resource in new[] { "memberships", "reporting-lines", "hr-assignments" })
        {
            yield return $"get /api/v1/attendance/directory/{resource}";
            yield return $"post /api/v1/attendance/directory/{resource}";
            yield return $"post /api/v1/attendance/directory/{resource}/{{id}}/end";
        }
        foreach (var resource in new[] { "users", "workspaces", "departments" }) yield return $"get /api/v1/attendance/directory/options/{resource}";
    }
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task All_thirteen_operations_document_actual_authority_and_contracts(string environment)
    {
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1", environment, settings: keys.Settings);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443") });
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/openapi/v1.json"));
        var root = doc.RootElement;
        var operations = root.GetProperty("paths").EnumerateObject().Where(p => p.Name == "/api/v1/attendance/access" || p.Name.StartsWith("/api/v1/attendance/directory/", StringComparison.Ordinal))
            .SelectMany(p => p.Value.EnumerateObject().Select(m => (Path: p.Name, Method: m.Name, Op: m.Value))).ToArray();
        Assert.Equal(13, operations.Length);
        Assert.Equal(ExpectedOperations().Order(), operations.Select(x => $"{x.Method} {x.Path}").Order());
        foreach (var (path, method, op) in operations)
        {
            var hint = path.EndsWith("/access", StringComparison.Ordinal);
            Assert.Equal(hint ? "attendance-access" : "attendance-directory", op.GetProperty("x-tpr10-scope").GetString());
            Assert.False(op.TryGetProperty("x-tpr10-scope-level", out _));
            Assert.Equal(!hint, op.GetProperty("x-tpr10-mfa-required").GetBoolean());
            Assert.Equal(hint ? [] : new[] { "attendance:directory-manage" }, op.GetProperty("x-tpr10-permissions").EnumerateArray().Select(x => x.GetString()).ToArray());
            Assert.True(Assert.Single(op.GetProperty("security").EnumerateArray()).TryGetProperty("SessionCookie", out _));
            Assert.DoesNotContain("ขอบเขต Module 2 ไม่มี business", op.GetProperty("description").GetString());
            var csrf = op.TryGetProperty("parameters", out var parameters) ? parameters.EnumerateArray().Where(x => x.GetProperty("name").GetString() == "X-CSRF-Token").ToArray() : [];
            if (method == "post") Assert.True(Assert.Single(csrf).GetProperty("required").GetBoolean()); else Assert.Empty(csrf);
            var responses = op.GetProperty("responses");
            foreach (var code in new[] { "400", "401", "403", "500", "503", "default" }) Assert.True(responses.TryGetProperty(code, out _), $"{path} missing {code}");
            var success = method == "get" ? "200" : path.EndsWith("/end", StringComparison.Ordinal) ? "204" : "201";
            Assert.True(responses.TryGetProperty(success, out _));
            foreach (var response in responses.EnumerateObject()) Assert.Equal("no-store", response.Value.GetProperty("headers").GetProperty("Cache-Control").GetProperty("description").GetString());
            if (method == "post")
            {
                foreach (var code in new[] { "404", "409", "429" }) Assert.True(responses.TryGetProperty(code, out _));
                var body = op.GetProperty("requestBody"); Assert.True(body.GetProperty("required").GetBoolean());
                var schema = Resolve(root, body.GetProperty("content").GetProperty("application/json").GetProperty("schema"));
                Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
                Assert.Equal(schema.GetProperty("properties").EnumerateObject().Select(x => x.Name).Order(), schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()).Order());
            }
        }
    }
    private static JsonElement Resolve(JsonElement root, JsonElement schema)
    {
        while (schema.TryGetProperty("$ref", out var reference)) schema = root.GetProperty("components").GetProperty("schemas").GetProperty(reference.GetString()!.Split('/')[^1]);
        return schema;
    }
}
