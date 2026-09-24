using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using TPR10.Api.Identity.Authorization;
using TPR10.Api.Identity.Csrf;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Scopes;

namespace TPR10.Api.Identity;

// Documentation only. Runtime enforcement remains in authentication, CSRF, policies and services.
public sealed class IdentityOpenApiTransformer(IAuthorizationPolicyProvider policies, ScopeOpenApiTransformer scopes)
    : IOpenApiOperationTransformer, IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["SessionCookie"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Cookie,
            Name = CsrfService.SessionCookieName,
            Description = "Session ที่ API ออกเท่านั้น: Secure; HttpOnly; SameSite=Lax; Path=/; ไม่มี Domain. ห้ามเก็บ token ใน JavaScript/URL/log."
        };
        return Task.CompletedTask;
    }

    public async Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.Description.RelativePath?.StartsWith("api/v1/", StringComparison.Ordinal) != true) return;
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        var auth = metadata.OfType<IAuthorizeData>().ToArray();
        var policy = await AuthorizationPolicy.CombineAsync(policies, auth);
        var authenticated = policy is not null && !metadata.OfType<IAllowAnonymous>().Any();
        var permissions = authenticated ? policy!.Requirements.OfType<PermissionRequirement>().ToArray() : [];
        var scope = metadata.OfType<ScopeEndpointMetadata>().LastOrDefault();
        operation.Security = authenticated
            ? [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("SessionCookie", context.Document)] = [] }]
            : [];
        operation.Extensions ??= new Dictionary<string, IOpenApiExtension>();
        operation.Extensions["x-tpr10-permissions"] = Strings(scope?.Capability is { } capability ? [capability] : permissions.Select(x => x.Capability));
        operation.Extensions["x-tpr10-mfa-required"] = new JsonNodeExtension(JsonValue.Create(scope?.RequireMfa ?? permissions.Any(x => x.RequireMfa)));
        var conditional = metadata.OfType<IdentityConditionalPermission>().Select(x => (JsonNode?)new JsonObject
        { ["field"] = x.Field, ["permission"] = x.Permission, ["condition"] = "ค่าของ field ไม่เป็น null รวม array ว่าง" }).ToArray();
        if (conditional.Length > 0) operation.Extensions["x-tpr10-conditional-permissions"] = new JsonNodeExtension(new JsonArray(conditional));
        // RestrictedSessionMiddleware always admits Active, in addition to the explicit stage metadata.
        var stages = (metadata.OfType<AllowedSessionStages>().LastOrDefault()?.Stages ?? []).Append(SessionStage.Active).Distinct();
        operation.Extensions["x-tpr10-session-stages"] = Strings(stages.Select(x => x.ToString()));
        if (scope is null)
        {
            operation.Extensions["x-tpr10-scope"] = new JsonNodeExtension(JsonValue.Create("identity-only-no-business-scope"));
            operation.Description += "\nAPI เป็น authority; ขอบเขต Module 2 ไม่มี business workspace/project/site scope. ";
        }
        operation.Description = (operation.Description +
            "Stage metadata อธิบาย session ที่มีอยู่ ไม่ได้บังคับ login ใน route สาธารณะ; service ยังตรวจ state เพิ่มเติม. " +
            "Response ไม่ cache; ห้าม retry mutation อัตโนมัติเมื่อไม่ทราบผลลัพธ์.").Trim();
        var unsafeMethod = CsrfMiddleware.IsUnsafe(context.Description.HttpMethod!);
        var issuer = metadata.OfType<CsrfIssuerMetadata>().Any();
        if (unsafeMethod)
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "X-CSRF-Token",
                In = ParameterLocation.Header,
                Required = true,
                Description = "รับใหม่จาก GET /api/v1/auth/csrf ผูกกับ pre-auth flow หรือ session ปัจจุบัน; ต้องส่ง Origin ที่ตรง HTTPS allowlist และ Host ที่เชื่อถือ",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String }
            });
        }
        operation.Responses ??= new();
        var problemSchema = await context.GetOrCreateSchemaAsync(typeof(ProblemDetails), cancellationToken: cancellationToken);
        // Authenticated cookies can fail validation/activity renewal even on an otherwise public operation.
        AddProblem(operation, problemSchema, 401, permissions.Length > 0 ? ["urn:tpr10:session-required"] : []);
        var forbiddenTypes = new List<string>();
        if (unsafeMethod || issuer) forbiddenTypes.Add("urn:tpr10:csrf-invalid");
        if (permissions.Length > 0) forbiddenTypes.AddRange(["urn:tpr10:permission-denied", "urn:tpr10:mfa-required", "urn:tpr10:stage-restricted"]);
        AddProblem(operation, problemSchema, 403, forbiddenTypes);
        AddProblem(operation, problemSchema, 503, []);
        if (unsafeMethod || issuer) AddProblem(operation, problemSchema, 429, []);
        if (context.Description.ParameterDescriptions.Any(p => p.Source == Microsoft.AspNetCore.Mvc.ModelBinding.BindingSource.Body
            || p.Source == Microsoft.AspNetCore.Mvc.ModelBinding.BindingSource.Query || p.Source == Microsoft.AspNetCore.Mvc.ModelBinding.BindingSource.Path))
            AddProblem(operation, problemSchema, 400, []);
        foreach (var problem in metadata.OfType<IdentityProblemTypes>())
            AddProblem(operation, problemSchema, problem.Status, problem.Types);
        if (scope is not null) await scopes.TransformAsync(operation, context, scope, cancellationToken);
        foreach (var entry in operation.Responses.ToArray())
        {
            if (entry.Value is not OpenApiResponse response) continue;
            response.Headers ??= new Dictionary<string, IOpenApiHeader>();
            response.Headers["Cache-Control"] = new OpenApiHeader { Description = "no-store", Schema = new OpenApiSchema { Type = JsonSchemaType.String } };
            response.Headers["X-Correlation-ID"] = new OpenApiHeader { Description = "รหัสอ้างอิงการตรวจสอบ ไม่ใช่ credential", Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" } };
            operation.Responses[entry.Key] = response;
        }
    }

    private static void AddProblem(OpenApiOperation operation, IOpenApiSchema schema, int status, IEnumerable<string> types)
    {
        var response = operation.Responses!.TryGetValue(status.ToString(), out var existing) && existing is OpenApiResponse concrete
            ? concrete : new OpenApiResponse { Description = $"ข้อผิดพลาด {status}" };
        operation.Responses[status.ToString()] = response;
        response.Content ??= new Dictionary<string, OpenApiMediaType>();
        response.Content["application/problem+json"] = new OpenApiMediaType { Schema = schema };
        response.Extensions ??= new Dictionary<string, IOpenApiExtension>();
        // Results.Problem defaults to the RFC status URI; typed URNs below are additional observed cases.
        var defaults = status switch
        {
            400 => "https://tools.ietf.org/html/rfc9110#section-15.5.1",
            401 => "https://tools.ietf.org/html/rfc9110#section-15.5.2",
            403 => "https://tools.ietf.org/html/rfc9110#section-15.5.4",
            404 => "https://tools.ietf.org/html/rfc9110#section-15.5.5",
            409 => "https://tools.ietf.org/html/rfc9110#section-15.5.10",
            429 => "https://tools.ietf.org/html/rfc6585#section-4",
            _ => "https://tools.ietf.org/html/rfc9110#section-15.6.4"
        };
        response.Extensions["x-tpr10-problem-types"] = Strings(types.Append(defaults));
        response.Description += " อาจไม่มี body เมื่อถูกปฏิเสธระหว่าง request binding หรือ revalidation; client ต้องใช้ status และห้าม parse JSON โดยไม่ตรวจ Content-Type";
        if (status == 429)
        {
            response.Headers ??= new Dictionary<string, IOpenApiHeader>();
            response.Headers["Retry-After"] = new OpenApiHeader { Description = "เวลารอเป็นวินาทีเมื่อ limiter ระบุค่า", Schema = new OpenApiSchema { Type = JsonSchemaType.String } };
        }
    }

    private static JsonNodeExtension Strings(IEnumerable<string> values) => new(new JsonArray(values.Distinct().Order().Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()));
}

public sealed record IdentityProblemTypes(int Status, params string[] Types);
public sealed record IdentityConditionalPermission(string Field, string Permission);

// Response DTO contracts for existing IResult/anonymous payloads. These never expose database secrets.
public sealed record CsrfTokenResponse(string Token);
public sealed record MfaEnrollmentResponse(string ProvisioningUri);
public sealed record MfaSessionResponse(SessionView Session);
public sealed record MfaConfirmationResponse(SessionView Session, string[] RecoveryCodes);
public sealed record TemporaryPasswordResponse(string TemporaryPassword, DateTimeOffset? ExpiresAtUtc);
public sealed record RoleResponse(Guid Id, string Name, string RoleClass);
public sealed record RoleListItem(Guid Id, string Name, string RoleClass, Guid[] PermissionIds);
public sealed record RolePage(RoleListItem[] Items, int Total, int Page, int PageSize);
public sealed record IdentityProbeResponse(string Status);
public sealed record TechnicalProbeResponse(Guid Id, string Note, DateTimeOffset CreatedAtUtc, string CorrelationId);
