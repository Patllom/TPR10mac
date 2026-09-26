using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TPR10.Api.IntegrationTests;

public sealed class IdentityOpenApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient client;
    public IdentityOpenApiTests(WebApplicationFactory<Program> factory) => client = factory.CreateClient();

    // Independent public contract: changing/removing a route, guard, response or schema must break these checks.
    public static TheoryData<string, string, string, int, string> Contracts => new()
    {
        { "/api/v1/auth/csrf", "get", "", 200, "token" },
        { "/api/v1/auth/login", "post", "", 200, "userId,stage,permissions,mfaVerifiedAtUtc" },
        { "/api/v1/auth/session", "get", "session", 200, "userId,stage,permissions,mfaVerifiedAtUtc" },
        { "/api/v1/auth/logout", "post", "session", 204, "" },
        { "/api/v1/auth/logout-all", "post", "session", 204, "" },
        { "/api/v1/auth/password/change", "post", "session", 204, "" },
        { "/api/v1/auth/password-reset/request", "post", "", 202, "text" },
        { "/api/v1/auth/password-reset/complete", "post", "", 204, "" },
        { "/api/v1/auth/mfa/enroll", "post", "session", 200, "provisioningUri" },
        { "/api/v1/auth/mfa/confirm", "post", "session", 200, "session,recoveryCodes" },
        { "/api/v1/auth/mfa/challenge", "post", "session", 200, "session" },
        { "/api/v1/auth/mfa/recover", "post", "session", 200, "session" },
        { "/api/v1/users", "get", "users:manage", 200, "items,total,page,pageSize" },
        { "/api/v1/users", "post", "users:manage", 201, "id,username,email,isActive,roleIds" },
        { "/api/v1/users/{id}", "patch", "users:manage", 200, "id,username,email,isActive,roleIds" },
        { "/api/v1/users/{id}/password-reset", "post", "users:manage", 200, "temporaryPassword,expiresAtUtc" },
        { "/api/v1/users/{id}/roles", "put", "roles:manage", 204, "" },
        { "/api/v1/users/{id}/sign-out-everywhere", "post", "users:manage", 204, "" },
        { "/api/v1/users/{id}/mfa/recover", "post", "users:recover-mfa", 204, "" },
        { "/api/v1/roles", "get", "roles:manage", 200, "items,total,page,pageSize" },
        { "/api/v1/roles", "post", "roles:manage", 201, "id,name,roleClass" },
        { "/api/v1/roles/{id}", "patch", "roles:manage", 200, "id,name,roleClass" },
        { "/api/v1/roles/{id}/permissions", "put", "roles:manage", 204, "" },
        { "/api/v1/permissions", "get", "roles:read", 200, "array" },
        { "/api/v1/system/identity-probe", "get", "system:probe", 200, "status" },
        { "/api/v1/system/technical-probes", "post", "system:probe", 201, "id,note,createdAtUtc,correlationId" },
        { "/api/v1/organization/workspaces", "get", "organization:manage", 200, "items,total,page,pageSize" },
        { "/api/v1/organization/workspaces", "post", "organization:manage", 201, "id,workspaceId,projectId,code,name,isActive,version" },
        { "/api/v1/organization/workspaces/{id}", "patch", "organization:manage", 200, "id,workspaceId,projectId,code,name,isActive,version" },
        { "/api/v1/organization/workspaces/{workspaceId}/departments", "get", "organization:manage", 200, "items,total,page,pageSize" },
        { "/api/v1/organization/workspaces/{workspaceId}/departments", "post", "organization:manage", 201, "id,workspaceId,projectId,code,name,isActive,version" },
        { "/api/v1/organization/workspaces/{workspaceId}/departments/{id}", "patch", "organization:manage", 200, "id,workspaceId,projectId,code,name,isActive,version" },
        { "/api/v1/organization/workspaces/{workspaceId}/projects", "get", "organization:manage", 200, "items,total,page,pageSize" },
        { "/api/v1/organization/workspaces/{workspaceId}/projects", "post", "organization:manage", 201, "id,workspaceId,projectId,code,name,isActive,version" },
        { "/api/v1/organization/workspaces/{workspaceId}/projects/{id}", "patch", "organization:manage", 200, "id,workspaceId,projectId,code,name,isActive,version" },
        { "/api/v1/organization/workspaces/{workspaceId}/projects/{projectId}/sites", "get", "organization:manage", 200, "items,total,page,pageSize" },
        { "/api/v1/organization/workspaces/{workspaceId}/projects/{projectId}/sites", "post", "organization:manage", 201, "id,workspaceId,projectId,code,name,isActive,version" },
        { "/api/v1/organization/workspaces/{workspaceId}/projects/{projectId}/sites/{id}", "patch", "organization:manage", 200, "id,workspaceId,projectId,code,name,isActive,version" },
        { "/api/v1/scope-assignments", "get", "scope-assignments:manage", 200, "items,total,pageNumber,pageSize" },
        { "/api/v1/scope-assignments", "post", "scope-assignments:manage", 201, "id,userId,scope,roleId,version,revokedAtUtc" },
        { "/api/v1/scope-assignments/{id}/replace", "post", "scope-assignments:manage", 200, "id,userId,scope,roleId,version,revokedAtUtc" },
        { "/api/v1/scope-assignments/{id}/revoke", "post", "scope-assignments:manage", 200, "id,userId,scope,roleId,version,revokedAtUtc" },
        { "/api/v1/scope-assignments/options/users", "get", "scope-assignments:manage", 200, "items,total,pageNumber,pageSize" },
        { "/api/v1/scope-assignments/options/roles", "get", "scope-assignments:manage", 200, "items,total,pageNumber,pageSize" },
        { "/api/v1/scopes", "get", "session", 200, "items,total,pageNumber,pageSize" }
    };

    [Theory]
    [MemberData(nameof(Contracts))]
    public async Task Every_operation_documents_security_and_real_success_shape(string path, string method, string permission, int success, string fields)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/openapi/v1.json"));
        var root = document.RootElement;
        var operation = root.GetProperty("paths").GetProperty(path).GetProperty(method);
        var parameters = operation.TryGetProperty("parameters", out var all) ? all.EnumerateArray().ToArray() : [];
        var csrf = parameters.Where(p => p.GetProperty("name").GetString() == "X-CSRF-Token").ToArray();
        if (method is "post" or "put" or "patch" or "delete")
        {
            Assert.Single(csrf);
            Assert.True(csrf[0].GetProperty("required").GetBoolean());
            Assert.Equal("header", csrf[0].GetProperty("in").GetString());
        }
        else Assert.Empty(csrf);
        var security = operation.GetProperty("security").EnumerateArray().ToArray();
        if (permission == "") Assert.Empty(security);
        else Assert.True(Assert.Single(security).TryGetProperty("SessionCookie", out _));
        Assert.Equal(permission.Contains(':') ? new[] { permission } : [],
            operation.GetProperty("x-tpr10-permissions").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(permission.Contains(':'), operation.GetProperty("x-tpr10-mfa-required").GetBoolean());
        Assert.Equal(path == "/api/v1/scopes" ? "scope-discovery" : path.StartsWith("/api/v1/scope-assignments", StringComparison.Ordinal)
            || path.StartsWith("/api/v1/organization/", StringComparison.Ordinal) ? "system-management" : "identity-only-no-business-scope", operation.GetProperty("x-tpr10-scope").GetString());

        var response = operation.GetProperty("responses").GetProperty(success.ToString());
        Assert.Equal(new[] { success.ToString() }, operation.GetProperty("responses").EnumerateObject()
            .Where(r => r.Name.StartsWith('2')).Select(r => r.Name).ToArray());
        if (success == 204) Assert.False(response.TryGetProperty("content", out _));
        else
        {
            var media = fields == "text" ? "text/plain" : "application/json";
            var schema = Resolve(root, response.GetProperty("content").GetProperty(media).GetProperty("schema"));
            if (fields == "text") Assert.Equal("string", schema.GetProperty("type").GetString());
            else if (fields == "array") Assert.Equal("array", schema.GetProperty("type").GetString());
            else foreach (var field in fields.Split(',')) Assert.True(schema.GetProperty("properties").TryGetProperty(field, out _), $"{path}: {field}");
        }
    }

    [Theory]
    [InlineData("/api/v1/users", "post")]
    [InlineData("/api/v1/users/{id}", "patch")]
    public async Task Assigning_roles_documents_conditional_permission(string path, string method)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/openapi/v1.json"));
        var operation = document.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method);
        var conditional = Assert.Single(operation.GetProperty("x-tpr10-conditional-permissions").EnumerateArray());
        Assert.Equal("roleIds", conditional.GetProperty("field").GetString());
        Assert.Equal("roles:manage", conditional.GetProperty("permission").GetString());
    }

    [Fact]
    public async Task Enumerates_every_versioned_operation_and_cookie_flags()
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/openapi/v1.json"));
        var root = document.RootElement;
        var actual = root.GetProperty("paths").EnumerateObject().Where(p => p.Name.StartsWith("/api/v1/", StringComparison.Ordinal))
            .SelectMany(p => p.Value.EnumerateObject().Where(m => new[] { "get", "post", "put", "patch", "delete", "head", "options" }.Contains(m.Name))
                .Select(m => $"{m.Name} {p.Name}")).Order().ToArray();
        var expected = Contracts.Select(row => $"{row[1]} {row[0]}")
            .Concat(ScopeRecordOpenApiTests.Contracts().Select(row => $"{row[1]} {row[0]}"))
            .Concat(AttendanceDirectoryOpenApiTests.ExpectedOperations()).Concat(StorageOpenApiTests.ExpectedOperations()).Concat(EvidenceOpenApiTests.ExpectedOperations()).Order().ToArray();
        Assert.Equal(expected, actual);
        var cookie = root.GetProperty("components").GetProperty("securitySchemes").GetProperty("SessionCookie");
        Assert.Equal("apiKey", cookie.GetProperty("type").GetString());
        Assert.Equal("cookie", cookie.GetProperty("in").GetString());
        Assert.Equal("__Host-tpr10_session", cookie.GetProperty("name").GetString());
        Assert.Contains("HttpOnly", cookie.GetProperty("description").GetString());
        Assert.Contains("Secure", cookie.GetProperty("description").GetString());
    }

    [Theory]
    [InlineData("/api/v1/auth/session", "get", "Active,MfaChallengeRequired,MfaEnrollmentRequired,PasswordChangeRequired")]
    [InlineData("/api/v1/auth/password/change", "post", "Active,PasswordChangeRequired")]
    [InlineData("/api/v1/auth/mfa/enroll", "post", "Active,MfaEnrollmentRequired")]
    [InlineData("/api/v1/auth/mfa/challenge", "post", "Active,MfaChallengeRequired")]
    [InlineData("/api/v1/roles", "get", "Active")]
    public async Task Stages_include_active_bypass_and_explicit_restricted_metadata(string path, string method, string expected)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/openapi/v1.json"));
        var stages = document.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method)
            .GetProperty("x-tpr10-session-stages").EnumerateArray().Select(x => x.GetString()).Order();
        Assert.Equal(expected.Split(','), stages);
    }

    [Theory]
    [InlineData("/api/v1/auth/login", "post", "400,401,403,409,429,503", "username,password")]
    [InlineData("/api/v1/auth/password-reset/complete", "post", "400,401,403,503", "token,password")]
    [InlineData("/api/v1/auth/password/change", "post", "400,401,403,429,503", "currentPassword,newPassword")]
    [InlineData("/api/v1/roles/{id}/permissions", "put", "400,401,403,404,409,503", "permissionIds")]
    [InlineData("/api/v1/users/{id}/mfa/recover", "post", "400,401,403,404,503", "reason,evidenceReference")]
    public async Task Request_schemas_and_actual_error_statuses_are_discoverable(string path, string method, string statuses, string fields)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/openapi/v1.json"));
        var root = document.RootElement;
        var operation = root.GetProperty("paths").GetProperty(path).GetProperty(method);
        var body = operation.GetProperty("requestBody");
        Assert.True(body.GetProperty("required").GetBoolean());
        var schema = Resolve(root, body.GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        foreach (var field in fields.Split(',')) Assert.True(schema.GetProperty("properties").TryGetProperty(field, out _));
        foreach (var status in statuses.Split(','))
        {
            var response = operation.GetProperty("responses").GetProperty(status);
            if (path == "/api/v1/auth/login" && status == "409")
            {
                Assert.False(response.TryGetProperty("content", out _)); // Results.Conflict() has no body.
                continue;
            }
            var problem = response.GetProperty("content")
                .GetProperty("application/problem+json").GetProperty("schema");
            Assert.True(Resolve(root, problem).GetProperty("properties").TryGetProperty("status", out _));
        }
        var types = operation.GetProperty("responses").GetProperty("403").GetProperty("x-tpr10-problem-types")
            .EnumerateArray().Select(x => x.GetString());
        Assert.Contains("urn:tpr10:csrf-invalid", types);
    }

    private static JsonElement Resolve(JsonElement root, JsonElement schema)
    {
        while (schema.TryGetProperty("$ref", out var reference))
        {
            Assert.StartsWith("#/components/schemas/", reference.GetString());
            schema = root.GetProperty("components").GetProperty("schemas").GetProperty(reference.GetString()!.Split('/')[^1]);
        }
        return schema;
    }
}
