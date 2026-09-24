using TPR10.Api.Scopes;

namespace TPR10.Api.Organization;

public sealed record OrganizationBindingBoundary;

// Runs after authorization/CSRF/session middleware. It never reads or logs the body.
public sealed class OrganizationBindingAuditMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, OrganizationService service)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<OrganizationBindingBoundary>() is null)
        {
            await next(context);
            return;
        }
        try { await next(context); }
        catch (BadHttpRequestException error) when (error.StatusCode == 400 && !context.Response.HasStarted
            && !context.Items.ContainsKey(typeof(OrganizationBindingBoundary)))
        {
            context.Response.StatusCode = 400;
        }
        if (context.Response.StatusCode != 400 || context.Response.HasStarted
            || context.Items.ContainsKey(typeof(OrganizationBindingBoundary))) return;

        // Derive kind from the registered template, never from arbitrary client text.
        var template = ((RouteEndpoint)endpoint).RoutePattern.RawText!;
        var kind = template.EndsWith("/sites") || template.Contains("/sites/") ? OrganizationKind.Site
            : template.Contains("/departments") ? OrganizationKind.Department
            : template.Contains("/projects") ? OrganizationKind.Project : OrganizationKind.Workspace;
        Guid? RouteId(string key) => Guid.TryParse(context.Request.RouteValues[key]?.ToString(), out var id) ? id : null;
        ScopeKey? parent = kind == OrganizationKind.Workspace ? null : new(RouteId("workspaceId") ?? Guid.Empty, RouteId("projectId"));
        var result = await service.RejectBindingAsync(kind, parent, RouteId("id"), context.RequestAborted);
        context.Response.Clear();
        await result.ExecuteAsync(context);
    }
}
