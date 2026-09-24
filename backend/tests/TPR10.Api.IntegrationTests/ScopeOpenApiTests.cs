using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TPR10.Api.IntegrationTests;

public sealed class ScopeOpenApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    public static IEnumerable<object[]> Contracts()
    {
        const string organization = "/api/v1/organization/workspaces";
        foreach (var path in new[] { organization, organization + "/{workspaceId}/departments", organization + "/{workspaceId}/projects", organization + "/{workspaceId}/projects/{projectId}/sites" })
        {
            yield return [path, "get", "system-management", "organization:manage", true, "none"];
            yield return [path, "post", "system-management", "organization:manage", true, "none"];
            yield return [path + "/{id}", "patch", "system-management", "organization:manage", true, "none"];
        }
        const string assignments = "/api/v1/scope-assignments";
        foreach (var suffix in new[] { "", "/options/users", "/options/roles" })
            yield return [assignments + suffix, "get", "system-management", "scope-assignments:manage", true, "none"];
        foreach (var suffix in new[] { "", "/{id}/replace", "/{id}/revoke" })
            yield return [assignments + suffix, "post", "system-management", "scope-assignments:manage", true, "none"];
        yield return ["/api/v1/scopes", "get", "scope-discovery", "", false, "none"];
        foreach (var (prefix, level) in new[] { ("/api/v1/workspaces/{workspaceId}", "workspace"), ("/api/v1/workspaces/{workspaceId}/projects/{projectId}", "project"), ("/api/v1/workspaces/{workspaceId}/projects/{projectId}/sites/{siteId}", "site") })
        {
            var path = prefix + "/scope-probe-records";
            yield return [path, "get", "exact-business", "scope-probe:read", false, level];
            yield return [path + "/{id}", "get", "exact-business", "scope-probe:read", false, level];
            yield return [path, "post", "exact-business", "scope-probe:write", false, level];
            yield return [path + "/{id}", "patch", "exact-business", "scope-probe:write", false, level];
            yield return [path + "/export-simulation", "post", "exact-business", "scope-probe:export", true, level];
        }
    }

    // Catch lost/misclassified scope metadata, a permission/MFA mismatch, duplicate CSRF,
    // or a consumer contract falsely claiming every failure is a JSON 503.
    [Theory]
    [MemberData(nameof(Contracts))]
    public async Task Every_scope_operation_has_explicit_mode_level_security_and_error_contract(string path, string method, string mode, string capability, bool mfa, string level)
    {
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/openapi/v1.json"));
        var operation = document.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method);
        Assert.Equal(mode, operation.GetProperty("x-tpr10-scope").GetString());
        Assert.Equal(level, operation.GetProperty("x-tpr10-scope-level").GetString());
        Assert.Equal(capability == "" ? [] : new[] { capability }, operation.GetProperty("x-tpr10-permissions").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(mfa, operation.GetProperty("x-tpr10-mfa-required").GetBoolean());
        Assert.True(Assert.Single(operation.GetProperty("security").EnumerateArray()).TryGetProperty("SessionCookie", out _));
        Assert.Equal("Active", Assert.Single(operation.GetProperty("x-tpr10-session-stages").EnumerateArray()).GetString());
        Assert.DoesNotContain("ขอบเขต Module 2 ไม่มี business", operation.GetProperty("description").GetString());
        var parameters = operation.GetProperty("parameters").EnumerateArray().ToArray();
        var csrf = parameters.Where(x => x.GetProperty("name").GetString() == "X-CSRF-Token").ToArray();
        if (method == "get") Assert.Empty(csrf);
        else
        {
            Assert.Equal("header", Assert.Single(csrf).GetProperty("in").GetString());
            Assert.True(csrf[0].GetProperty("required").GetBoolean());
        }
        var responses = operation.GetProperty("responses");
        foreach (var status in new[] { "400", "401", "403", "500", "503", "default" })
            Assert.True(responses.TryGetProperty(status, out _), $"{method} {path} missing {status}");
        if (method != "get") Assert.True(responses.TryGetProperty("429", out _));
        Assert.Contains("body", responses.GetProperty("400").GetProperty("description").GetString());
        Assert.Contains("body", responses.GetProperty("default").GetProperty("description").GetString());
        foreach (var response in responses.EnumerateObject())
        {
            var headers = response.Value.GetProperty("headers");
            Assert.Equal("no-store", headers.GetProperty("Cache-Control").GetProperty("description").GetString());
            Assert.True(headers.TryGetProperty("X-Correlation-ID", out _));
        }
        if (parameters.Any(x => x.GetProperty("name").GetString() == "pageSize"))
        {
            var pagination = operation.GetProperty("x-tpr10-pagination");
            Assert.Equal(1, pagination.GetProperty("pageMinimum").GetInt32());
            Assert.Equal(25, pagination.GetProperty("pageSizeDefault").GetInt32());
            Assert.Equal(100, pagination.GetProperty("pageSizeMaximum").GetInt32());
            Assert.Equal("clamp", pagination.GetProperty("overMaximum").GetString());
        }
    }

    [Fact]
    public async Task Enumerates_all_module3_operations_without_classifying_identity_as_scoped()
    {
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/openapi/v1.json"));
        var scoped = document.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(p => p.Value.EnumerateObject().Where(m => m.Value.TryGetProperty("x-tpr10-scope-level", out _)).Select(m => $"{m.Name} {p.Name}"));
        Assert.Equal(Contracts().Select(row => $"{row[1]} {row[0]}").Order(), scoped.Order());
        var identity = document.RootElement.GetProperty("paths").GetProperty("/api/v1/auth/session").GetProperty("get");
        Assert.Equal("identity-only-no-business-scope", identity.GetProperty("x-tpr10-scope").GetString());
        Assert.False(identity.TryGetProperty("x-tpr10-field-policy", out _));
    }

    [Theory]
    [InlineData("/api/v1/workspaces/{workspaceId}/scope-probe-records", "post")]
    [InlineData("/api/v1/workspaces/{workspaceId}/scope-probe-records/{id}", "patch")]
    [InlineData("/api/v1/workspaces/{workspaceId}/projects/{projectId}/scope-probe-records", "post")]
    [InlineData("/api/v1/workspaces/{workspaceId}/projects/{projectId}/scope-probe-records/{id}", "patch")]
    [InlineData("/api/v1/workspaces/{workspaceId}/projects/{projectId}/sites/{siteId}/scope-probe-records", "post")]
    [InlineData("/api/v1/workspaces/{workspaceId}/projects/{projectId}/sites/{siteId}/scope-probe-records/{id}", "patch")]
    public async Task Restricted_write_schema_distinguishes_absence_null_and_string(string path, string method)
    {
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/openapi/v1.json"));
        var operation = document.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method);
        var schema = Resolve(document.RootElement, operation.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        var required = schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Contains("note", required);
        Assert.DoesNotContain("restrictedNote", required);
        Assert.Equal(method == "patch", required.Contains("expectedVersion"));
        var restricted = schema.GetProperty("properties").GetProperty("restrictedNote");
        Assert.Equal(new[] { "null", "string" }, restricted.GetProperty("type").EnumerateArray().Select(x => x.GetString()).Order());
        Assert.Equal(500, restricted.GetProperty("maxLength").GetInt32());
        var policy = operation.GetProperty("x-tpr10-field-policy").GetProperty("restrictedNote");
        Assert.Equal("scope-probe:restricted-read", policy.GetProperty("permission").GetString());
        Assert.True(policy.GetProperty("recentMfa").GetBoolean());
        Assert.True(policy.GetProperty("writeWhenPresentIncludingNull").GetBoolean());
    }

    [Theory]
    [InlineData("/api/v1/workspaces/{workspaceId}/scope-probe-records", "get", true)]
    [InlineData("/api/v1/workspaces/{workspaceId}/scope-probe-records/{id}", "get", false)]
    [InlineData("/api/v1/workspaces/{workspaceId}/scope-probe-records/export-simulation", "post", true)]
    public async Task Read_and_export_document_property_omission_not_a_nullable_public_secret(string path, string method, bool page)
    {
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/openapi/v1.json"));
        var root = document.RootElement;
        var operation = root.GetProperty("paths").GetProperty(path).GetProperty(method);
        Assert.Equal("omit", operation.GetProperty("x-tpr10-field-policy").GetProperty("restrictedNote").GetProperty("withoutPermission").GetString());
        var variants = operation.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("anyOf").EnumerateArray()
            .Select(x => Resolve(root, x)).ToArray();
        var rows = variants.Select(x => page ? Resolve(root, x.GetProperty("properties").GetProperty("items").GetProperty("items")) : x).ToArray();
        Assert.False(rows[0].GetProperty("properties").TryGetProperty("restrictedNote", out _));
        Assert.True(rows[1].GetProperty("properties").TryGetProperty("restrictedNote", out _));
    }

    [Fact]
    public async Task Production_openapi_keeps_management_and_discovery_but_excludes_all_technical_records()
    {
        using var keys = new TestKeyMaterial();
        await using var production = new ApiFactory("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1", "Production", settings: keys.Settings);
        using var client = production.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443") });
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/openapi/v1.json"));
        var paths = document.RootElement.GetProperty("paths");
        Assert.DoesNotContain(paths.EnumerateObject(), p => p.Name.Contains("scope-probe-records", StringComparison.Ordinal));
        var operations = paths.EnumerateObject().SelectMany(p => p.Value.EnumerateObject()
            .Where(m => m.Value.TryGetProperty("x-tpr10-scope-level", out _)).Select(m => $"{m.Name} {p.Name}"));
        Assert.Equal(Contracts().Where(row => (string)row[2] != "exact-business").Select(row => $"{row[1]} {row[0]}").Order(), operations.Order());
    }

    [Fact]
    public async Task Discovery_documents_its_dependency_failure_problem_type()
    {
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/openapi/v1.json"));
        var response = document.RootElement.GetProperty("paths").GetProperty("/api/v1/scopes")
            .GetProperty("get").GetProperty("responses").GetProperty("503");
        Assert.Contains("urn:tpr10:scope-unavailable-service", response.GetProperty("x-tpr10-problem-types").EnumerateArray().Select(x => x.GetString()));
    }

    private static JsonElement Resolve(JsonElement root, JsonElement schema)
    {
        while (schema.TryGetProperty("$ref", out var reference)) schema = root.GetProperty("components").GetProperty("schemas").GetProperty(reference.GetString()!.Split('/')[^1]);
        return schema;
    }
}
