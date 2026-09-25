using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TPR10.Api.IntegrationTests;

public sealed class StorageOpenApiTests
{
    public static IEnumerable<string> ExpectedOperations() =>
    ["get /api/v1/attendance/storage/options", "get /api/v1/attendance/storage/locations", "post /api/v1/attendance/storage/locations",
        "post /api/v1/attendance/storage/locations/{id}/probe", "get /api/v1/attendance/storage/write-target",
        "post /api/v1/attendance/storage/write-target", "get /api/v1/attendance/storage/health",
        "get /api/v1/attendance/storage/migrations", "post /api/v1/attendance/storage/migrations",
        "get /api/v1/attendance/storage/migrations/{id}", "post /api/v1/attendance/storage/migrations/{id}/resume"];

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Storage_contracts_document_eleven_routes_no_pin_raw_paths_or_implicit_read_rights(string environment)
    {
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1", environment, settings: keys.Settings);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443") });
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/openapi/v1.json"));
        var operations = doc.RootElement.GetProperty("paths").EnumerateObject().Where(p => p.Name.StartsWith("/api/v1/attendance/storage/", StringComparison.Ordinal))
            .SelectMany(p => p.Value.EnumerateObject().Select(m => (Path: p.Name, Method: m.Name, Op: m.Value))).ToArray();
        Assert.Equal(ExpectedOperations().Order(), operations.Select(x => $"{x.Method} {x.Path}").Order());
        foreach (var (path, method, op) in operations)
        {
            Assert.Equal("attendance-storage", op.GetProperty("x-tpr10-scope").GetString());
            Assert.Equal("attendance:storage-manage", Assert.Single(op.GetProperty("x-tpr10-permissions").EnumerateArray()).GetString());
            Assert.True(op.GetProperty("x-tpr10-mfa-required").GetBoolean());
            Assert.Contains("readiness", op.GetProperty("description").GetString());
            Assert.Contains("ไม่ให้สิทธิ์อ่านรูป", op.GetProperty("description").GetString());
            var response = op.GetProperty("responses");
            foreach (var code in new[] { "400", "401", "403", "404", "409", "503", "default" }) Assert.True(response.TryGetProperty(code, out _));
            var success = method == "post" && path.Contains("/migrations", StringComparison.Ordinal) ? "202"
                : method == "post" && path.EndsWith("/locations", StringComparison.Ordinal) ? "201" : "200";
            Assert.True(response.TryGetProperty(success, out _));
            Assert.Equal("no-store", response.GetProperty(success).GetProperty("headers").GetProperty("Cache-Control").GetProperty("description").GetString());
            var parameters = op.TryGetProperty("parameters", out var p) ? p.EnumerateArray().ToArray() : [];
            if (method == "post") Assert.True(Assert.Single(parameters, p => p.GetProperty("name").GetString() == "X-CSRF-Token").GetProperty("required").GetBoolean());
            if (parameters.Any(p => p.GetProperty("name").GetString() == "limit")) Assert.Equal(100, op.GetProperty("x-tpr10-pagination").GetProperty("limitMaximum").GetInt32());
        }
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        foreach (var name in new[] { "RegisterStorage", "ProbeStorage", "SwitchWriteTarget", "StartMigration", "ResumeMigration" })
        {
            var schema = schemas.GetProperty(name); Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
            Assert.Equal(schema.GetProperty("properties").EnumerateObject().Select(x => x.Name).Order(), schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()).Order());
            Assert.DoesNotContain("root", schema.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
