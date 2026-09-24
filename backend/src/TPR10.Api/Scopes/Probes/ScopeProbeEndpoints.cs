using Microsoft.AspNetCore.Http.HttpResults;
using TPR10.Api.Correlation;
using TPR10.Api.Identity;

namespace TPR10.Api.Scopes.Probes;

public static class ScopeProbeEndpoints
{
    public static IEndpointRouteBuilder MapScopeProbeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var w = endpoints.MapGroup("/api/v1/workspaces/{workspaceId}/scope-probe-records");
        Add(w.MapPost("/export-simulation", (Guid workspaceId, ExportScopeRecords body, ScopeExportService s, CancellationToken ct) => s.ExportAsync(new(workspaceId), body, ct)), "export", "workspace");
        Add(w.MapGet("", (Guid workspaceId, ScopeProbeService s, CancellationToken ct, int page = 1, int pageSize = 25) => s.ListAsync(new(workspaceId), page, pageSize, ct)), "read", "workspace", true);
        Add(w.MapGet("/{id}", (Guid workspaceId, Guid id, ScopeProbeService s, CancellationToken ct) => s.DetailAsync(new(workspaceId), id, ct)), "read", "workspace");
        Add(w.MapPost("", (Guid workspaceId, CreateScopeRecord body, ScopeProbeService s, CancellationToken ct) => s.CreateAsync(new(workspaceId), body, ct)), "write", "workspace", create: true);
        Add(w.MapPatch("/{id}", (Guid workspaceId, Guid id, UpdateScopeRecord body, ScopeProbeService s, CancellationToken ct) => s.UpdateAsync(new(workspaceId), id, body, ct)), "write", "workspace");
        var p = endpoints.MapGroup("/api/v1/workspaces/{workspaceId}/projects/{projectId}/scope-probe-records");
        Add(p.MapPost("/export-simulation", (Guid workspaceId, Guid projectId, ExportScopeRecords body, ScopeExportService s, CancellationToken ct) => s.ExportAsync(new(workspaceId, projectId), body, ct)), "export", "project");
        Add(p.MapGet("", (Guid workspaceId, Guid projectId, ScopeProbeService s, CancellationToken ct, int page = 1, int pageSize = 25) => s.ListAsync(new(workspaceId, projectId), page, pageSize, ct)), "read", "project", true);
        Add(p.MapGet("/{id}", (Guid workspaceId, Guid projectId, Guid id, ScopeProbeService s, CancellationToken ct) => s.DetailAsync(new(workspaceId, projectId), id, ct)), "read", "project");
        Add(p.MapPost("", (Guid workspaceId, Guid projectId, CreateScopeRecord body, ScopeProbeService s, CancellationToken ct) => s.CreateAsync(new(workspaceId, projectId), body, ct)), "write", "project", create: true);
        Add(p.MapPatch("/{id}", (Guid workspaceId, Guid projectId, Guid id, UpdateScopeRecord body, ScopeProbeService s, CancellationToken ct) => s.UpdateAsync(new(workspaceId, projectId), id, body, ct)), "write", "project");
        var site = endpoints.MapGroup("/api/v1/workspaces/{workspaceId}/projects/{projectId}/sites/{siteId}/scope-probe-records");
        Add(site.MapPost("/export-simulation", (Guid workspaceId, Guid projectId, Guid siteId, ExportScopeRecords body, ScopeExportService s, CancellationToken ct) => s.ExportAsync(new(workspaceId, projectId, siteId), body, ct)), "export", "site");
        Add(site.MapGet("", (Guid workspaceId, Guid projectId, Guid siteId, ScopeProbeService s, CancellationToken ct, int page = 1, int pageSize = 25) => s.ListAsync(new(workspaceId, projectId, siteId), page, pageSize, ct)), "read", "site", true);
        Add(site.MapGet("/{id}", (Guid workspaceId, Guid projectId, Guid siteId, Guid id, ScopeProbeService s, CancellationToken ct) => s.DetailAsync(new(workspaceId, projectId, siteId), id, ct)), "read", "site");
        Add(site.MapPost("", (Guid workspaceId, Guid projectId, Guid siteId, CreateScopeRecord body, ScopeProbeService s, CancellationToken ct) => s.CreateAsync(new(workspaceId, projectId, siteId), body, ct)), "write", "site", create: true);
        Add(site.MapPatch("/{id}", (Guid workspaceId, Guid projectId, Guid siteId, Guid id, UpdateScopeRecord body, ScopeProbeService s, CancellationToken ct) => s.UpdateAsync(new(workspaceId, projectId, siteId), id, body, ct)), "write", "site");
        return endpoints;
    }

    private static void Add(RouteHandlerBuilder route, string capability, string level, bool list = false, bool create = false)
    {
        route.RequireAuthorization().WithMetadata(new ScopeProbeBoundary("scope-probe:" + capability, level))
            .Produces(create ? 201 : 200, capability == "export" ? typeof(ExportRecordPage<PublicRecordView>) : list ? typeof(Page<PublicRecordView>) : typeof(PublicRecordView))
            .AddEndpointFilter(async (context, next) =>
            {
                if (context.HttpContext.Response.StatusCode == 400) return Results.Empty;
                context.HttpContext.Items[typeof(ScopeProbeBoundary)] = true;
                var result = await next(context);
                if (result is ProblemHttpResult problem) Correlate(context.HttpContext, problem);
                return result;
            });
        foreach (var status in new[] { 400, 401, 403, 404, 409, 503 })
            route.WithMetadata(new IdentityProblemTypes(status, ScopeOperation.ProblemType(status)));
    }

    internal static void Correlate(HttpContext context, ProblemHttpResult problem) =>
        problem.ProblemDetails.Extensions["correlationId"] = context.RequestServices.GetRequiredService<ICorrelationContext>().CorrelationId.ToString("D");
}
