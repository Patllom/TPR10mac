using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TPR10.Api.IntegrationTests;

public sealed class ScopeRecordOpenApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient client;
    public ScopeRecordOpenApiTests(WebApplicationFactory<Program> factory) => client = factory.CreateClient();
    public static IEnumerable<object[]> Contracts()
    {
        foreach (var prefix in new[] { "/api/v1/workspaces/{workspaceId}", "/api/v1/workspaces/{workspaceId}/projects/{projectId}", "/api/v1/workspaces/{workspaceId}/projects/{projectId}/sites/{siteId}" })
        {
            var path = prefix + "/scope-probe-records";
            yield return [path, "get", "scope-probe:read", 200, 2];
            yield return [path + "/{id}", "get", "scope-probe:read", 200, 2];
            yield return [path, "post", "scope-probe:write", 201, 3];
            yield return [path + "/{id}", "patch", "scope-probe:write", 200, 3];
        }
    }
    [Theory]
    [MemberData(nameof(Contracts))]
    public async Task Exact_business_contract_covers_security_errors_and_visibility_variants(string path, string method, string capability, int status, int variants)
    {
        using var http = await client.GetAsync("/api/openapi/v1.json");
        var text = await http.Content.ReadAsStringAsync();
        Assert.True(http.IsSuccessStatusCode, text);
        using var json = JsonDocument.Parse(text);
        var operation = json.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method);
        Assert.Equal("exact-business", operation.GetProperty("x-tpr10-scope").GetString());
        Assert.Equal(capability, Assert.Single(operation.GetProperty("x-tpr10-permissions").EnumerateArray()).GetString());
        Assert.False(operation.GetProperty("x-tpr10-mfa-required").GetBoolean());
        Assert.True(Assert.Single(operation.GetProperty("security").EnumerateArray()).TryGetProperty("SessionCookie", out _));
        var response = operation.GetProperty("responses").GetProperty(status.ToString());
        Assert.Equal(variants, response.GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("anyOf").GetArrayLength());
        foreach (var error in new[] { "400", "401", "403", "404", "409", "503" })
            Assert.True(operation.GetProperty("responses").GetProperty(error).TryGetProperty("x-tpr10-problem-types", out _));
        if (method != "get")
        {
            Assert.Contains(operation.GetProperty("parameters").EnumerateArray(), x => x.GetProperty("name").GetString() == "X-CSRF-Token" && x.GetProperty("required").GetBoolean());
            Assert.True(response.GetProperty("headers").TryGetProperty("Location", out _));
            Assert.Contains("null", operation.GetProperty("description").GetString());
        }
    }
}
