using TPR10.Api.Identity;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Scopes;

namespace TPR10.Api.Organization;

public static class OrganizationEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/organization/workspaces").RequireAuthorization("organization:manage")
            .WithMetadata(new OrganizationBindingBoundary(), new ScopeEndpointMetadata("system-management", "organization:manage", true, "none"));
        group.AddEndpointFilter(async (context, next) =>
        {
            // Scalar binding can set 400 before invoking filters with default arguments.
            if (context.HttpContext.Response.StatusCode == 400) return Results.Empty;
            context.HttpContext.Items[typeof(OrganizationBindingBoundary)] = true;
            return await next(context);
        });
        List(group.MapGet("", (OrganizationService service, int? page, int? pageSize, CancellationToken ct) => service.ListAsync(OrganizationKind.Workspace, null, page ?? 1, pageSize ?? 25, ct)));
        Create(group.MapPost("", (OrganizationService service, RequestSession session, CreateOrganization request, CancellationToken ct) => service.CreateAsync(session.Entity!.UserId, OrganizationKind.Workspace, null, request, ct)));
        Update(group.MapPatch("/{id}", (OrganizationService service, RequestSession session, Guid id, UpdateOrganization request, CancellationToken ct) => service.UpdateAsync(session.Entity!.UserId, OrganizationKind.Workspace, null, id, request, ct)));
        foreach (var kind in new[] { OrganizationKind.Department, OrganizationKind.Project })
        {
            var path = "/{workspaceId}/" + (kind == OrganizationKind.Department ? "departments" : "projects");
            List(group.MapGet(path, (OrganizationService service, Guid workspaceId, int? page, int? pageSize, CancellationToken ct) => service.ListAsync(kind, new ScopeKey(workspaceId), page ?? 1, pageSize ?? 25, ct)));
            Create(group.MapPost(path, (OrganizationService service, RequestSession session, Guid workspaceId, CreateOrganization request, CancellationToken ct) => service.CreateAsync(session.Entity!.UserId, kind, new ScopeKey(workspaceId), request, ct)));
            Update(group.MapPatch(path + "/{id}", (OrganizationService service, RequestSession session, Guid workspaceId, Guid id, UpdateOrganization request, CancellationToken ct) => service.UpdateAsync(session.Entity!.UserId, kind, new ScopeKey(workspaceId), id, request, ct)));
        }
        const string sites = "/{workspaceId}/projects/{projectId}/sites";
        List(group.MapGet(sites, (OrganizationService service, Guid workspaceId, Guid projectId, int? page, int? pageSize, CancellationToken ct) => service.ListAsync(OrganizationKind.Site, new ScopeKey(workspaceId, projectId), page ?? 1, pageSize ?? 25, ct)));
        Create(group.MapPost(sites, (OrganizationService service, RequestSession session, Guid workspaceId, Guid projectId, CreateOrganization request, CancellationToken ct) => service.CreateAsync(session.Entity!.UserId, OrganizationKind.Site, new ScopeKey(workspaceId, projectId), request, ct)));
        Update(group.MapPatch(sites + "/{id}", (OrganizationService service, RequestSession session, Guid workspaceId, Guid projectId, Guid id, UpdateOrganization request, CancellationToken ct) => service.UpdateAsync(session.Entity!.UserId, OrganizationKind.Site, new ScopeKey(workspaceId, projectId), id, request, ct)));
        return endpoints;
    }

    private static void List(RouteHandlerBuilder route) => route.Produces<OrganizationPage>().WithMetadata(new IdentityProblemTypes(404));
    private static void Create(RouteHandlerBuilder route) => route.Produces<OrganizationView>(201).WithMetadata(new IdentityProblemTypes(404), new IdentityProblemTypes(409));
    private static void Update(RouteHandlerBuilder route) => route.Produces<OrganizationView>().WithMetadata(new IdentityProblemTypes(404), new IdentityProblemTypes(409));
}
