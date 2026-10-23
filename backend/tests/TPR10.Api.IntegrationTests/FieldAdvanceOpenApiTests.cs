using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TPR10.Api.IntegrationTests;

public sealed class FieldAdvanceOpenApiTests
{
    internal static string[] ExpectedOperations() =>
    [
        "post /api/v1/field-advances/policies",
        "get /api/v1/field-advances/policies",
        "get /api/v1/field-advances/policies/scopes",
        "get /api/v1/field-advances/policies/company-context",
        "get /api/v1/field-advances/policies/{id}",
        "get /api/v1/field-advances/policies/{id}/history",
        "get /api/v1/field-advances/policies/review-queue",
        "get /api/v1/field-advances/policies/effective",
        "post /api/v1/field-advances/policies/preview",
        "put /api/v1/field-advances/policies/{id}",
        "post /api/v1/field-advances/policies/{id}/submit",
        "post /api/v1/field-advances/policies/{id}/review",
        "post /api/v1/field-advances/grants",
        "get /api/v1/field-advances/grants",
        "get /api/v1/field-advances/grants/scopes",
        "get /api/v1/field-advances/grants/recipients",
        "get /api/v1/field-advances/grants/review-queue",
        "get /api/v1/field-advances/grants/{id}",
        "get /api/v1/field-advances/grants/{id}/history",
        "post /api/v1/field-advances/grants/{id}/review",
        "post /api/v1/field-advances/grants/{id}/revoke",
        "get /api/v1/field-advances/requests/scopes",
        "get /api/v1/field-advances/requests/people",
        "get /api/v1/field-advances/requests",
        "get /api/v1/field-advances/requests/review-queue",
        "get /api/v1/field-advances/requests/{id}",
        "get /api/v1/field-advances/requests/{id}/history",
        "get /api/v1/field-advances/requests/{id}/quote",
        "post /api/v1/field-advances/requests",
        "put /api/v1/field-advances/requests/{id}",
        "post /api/v1/field-advances/requests/{id}/send-to-owner",
        "post /api/v1/field-advances/requests/{id}/confirm",
        "post /api/v1/field-advances/requests/{id}/return-to-author",
        "post /api/v1/field-advances/requests/{id}/review",
        "post /api/v1/field-advances/requests/{id}/revise",
        "post /api/v1/field-advances/requests/{id}/withdraw",
        "post /api/v1/field-advances/requests/{id}/copy"
    ];

    [Fact]
    public async Task Request_contract_has_string_decimals_current_step_and_protected_commands()
    {
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1", "Production", settings: keys.Settings);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443") });
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/openapi/v1.json"));
        var paths = doc.RootElement.GetProperty("paths");
        foreach (var route in ExpectedOperations().Where(x => x.Contains("/requests")))
        {
            var split = route.Split(' '); var op = paths.GetProperty(split[1]).GetProperty(split[0]);
            Assert.True(op.GetProperty("x-tpr10-mfa-required").GetBoolean());
            Assert.True(op.GetProperty("security")[0].TryGetProperty("SessionCookie", out _));
            Assert.Contains("no-store", op.GetProperty("description").GetString());
            if (split[0] != "get")
            {
                Assert.True(op.TryGetProperty("requestBody", out _));
                Assert.Contains(op.GetProperty("parameters").EnumerateArray(), x => x.GetProperty("name").GetString() == "X-CSRF-Token");
                Assert.True(op.GetProperty("responses").TryGetProperty("400", out _));
            }
        }
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.Equal("string", schemas.GetProperty("RequestEstimateHttp").GetProperty("properties").GetProperty("total").GetProperty("type").GetString());
        Assert.True(schemas.GetProperty("RequestHttpView").GetProperty("properties").TryGetProperty("currentStep", out _));
        foreach (var field in new[] { "quantity", "unitRate", "lumpSum" })
        {
            var schema = schemas.GetProperty("EstimateLineInput").GetProperty("properties").GetProperty(field).GetRawText();
            Assert.Contains("string", schema); Assert.DoesNotContain("number", schema);
        }
    }

    [Fact]
    public async Task Policy_and_grant_output_money_are_documented_as_strings()
    {
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1", "Production", settings: keys.Settings);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443") });
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/openapi/v1.json"));
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        var response = schemas.GetProperty("PolicyHttpView").GetProperty("properties").GetProperty("content");
        var contentName = response.GetProperty("$ref").GetString()!.Split('/')[^1];
        var bandRef = schemas.GetProperty(contentName).GetProperty("properties").GetProperty("bands").GetProperty("items").GetProperty("$ref").GetString()!.Split('/')[^1];
        var type = schemas.GetProperty(bandRef).GetProperty("properties").GetProperty("upperInclusive").GetProperty("type");
        Assert.Equal(JsonValueKind.String, type.ValueKind);
        Assert.Equal("string", type.GetString());
        var grant = schemas.GetProperty("GrantWireSpec").GetProperty("properties").GetProperty("approvalLimit").GetRawText();
        Assert.Contains("string", grant);
        Assert.DoesNotContain("number", grant);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task Policy_and_grant_commands_document_session_csrf_mfa_and_no_store_without_delete(string environment)
    {
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1", environment, settings: keys.Settings);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443") });
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/openapi/v1.json"));
        var paths = doc.RootElement.GetProperty("paths");
        foreach (var (suffix, method) in new[] { ("policies", "post"), ("policies/{id}", "put"), ("policies/{id}/submit", "post"),
            ("policies/{id}/review", "post"), ("grants", "post"), ("grants/{id}/review", "post"), ("grants/{id}/revoke", "post") })
        {
            Assert.True(paths.TryGetProperty("/api/v1/field-advances/" + suffix, out var path), suffix);
            Assert.False(path.TryGetProperty("delete", out _));
            var op = path.GetProperty(method);
            Assert.True(op.GetProperty("security")[0].TryGetProperty("SessionCookie", out _));
            Assert.True(op.GetProperty("x-tpr10-mfa-required").GetBoolean());
            Assert.Contains(op.GetProperty("parameters").EnumerateArray(), p => p.GetProperty("name").GetString() == "X-CSRF-Token" && p.GetProperty("required").GetBoolean());
            foreach (var status in new[] { "400", "401", "403", "409", "503" }) Assert.True(op.GetProperty("responses").TryGetProperty(status, out _));
            Assert.Equal("no-store", op.GetProperty("responses").GetProperty("403").GetProperty("headers").GetProperty("Cache-Control").GetProperty("description").GetString());
        }
        if (environment == "Production") Assert.DoesNotContain(paths.EnumerateObject(), p =>
            p.Name.Contains("scope-probe") || p.Name is "/api/v1/system/identity-probe" or "/api/v1/system/technical-probes"
            || p.Name.StartsWith("/api/v1/field-advances/", StringComparison.Ordinal) && p.Name.Contains("probe"));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task Read_and_preview_contracts_require_session_mfa_no_store_and_csrf_only_for_post(string environment)
    {
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1", environment, settings: keys.Settings);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443") });
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/openapi/v1.json"));
        var paths = doc.RootElement.GetProperty("paths");
        foreach (var suffix in new[] { "", "/{id}", "/{id}/history", "/review-queue", "/effective", "/preview" })
        {
            var post = suffix == "/preview";
            var op = paths.GetProperty("/api/v1/field-advances/policies" + suffix).GetProperty(post ? "post" : "get");
            Assert.True(op.GetProperty("security")[0].TryGetProperty("SessionCookie", out _));
            Assert.True(op.GetProperty("x-tpr10-mfa-required").GetBoolean());
            Assert.Equal(post, op.GetProperty("parameters").EnumerateArray().Any(p => p.GetProperty("name").GetString() == "X-CSRF-Token"));
            foreach (var status in new[] { "400", "401", "403", "409", "503" }) Assert.True(op.GetProperty("responses").TryGetProperty(status, out _));
            Assert.Equal("no-store", op.GetProperty("responses").GetProperty("403").GetProperty("headers").GetProperty("Cache-Control").GetProperty("description").GetString());
        }
    }
}
