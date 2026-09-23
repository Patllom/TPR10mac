using TPR10.Api.Identity;
using TPR10.Api.Identity.Sessions;

namespace TPR10.Api.Scopes.Assignments;

public sealed record AssignmentBindingBoundary;

public static class AssignmentEndpoints
{
    public static IEndpointRouteBuilder MapAssignmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/scope-assignments").RequireAuthorization("scope-assignments:manage").WithMetadata(new AssignmentBindingBoundary());
        group.AddEndpointFilter(async (context, next) =>
        {
            if (context.HttpContext.Response.StatusCode == 400) return Results.Empty;
            context.HttpContext.Items[typeof(AssignmentBindingBoundary)] = true;
            return await next(context);
        });
        group.MapGet("", (AssignmentService service, Guid? userId, Guid? workspaceId, Guid? projectId, Guid? siteId, bool? revoked, int? page, int? pageSize, CancellationToken ct)
            => service.ListAsync(userId, workspaceId is null && projectId is null && siteId is null ? null : new(workspaceId ?? Guid.Empty, projectId, siteId), revoked, page ?? 1, pageSize ?? 25, ct))
            .Produces<Page<AssignmentView>>();
        group.MapGet("/options/users", (AssignmentService service, string? prefix, int? page, int? pageSize, CancellationToken ct) => service.UsersAsync(prefix, page ?? 1, pageSize ?? 25, ct))
            .Produces<Page<AssignmentUserOption>>();
        group.MapGet("/options/roles", (AssignmentService service, string? prefix, int? page, int? pageSize, CancellationToken ct) => service.RolesAsync(prefix, page ?? 1, pageSize ?? 25, ct))
            .Produces<Page<AssignmentRoleOption>>();
        group.MapPost("", (AssignmentService service, RequestSession session, GrantAssignment request, CancellationToken ct) => service.GrantAsync(session.Entity!.UserId, request, ct))
            .Produces<AssignmentView>(201).WithMetadata(new IdentityProblemTypes(404), new IdentityProblemTypes(409));
        group.MapPost("/{id}/replace", (AssignmentService service, RequestSession session, Guid id, ReplaceAssignment request, CancellationToken ct) => service.ReplaceAsync(session.Entity!.UserId, id, request, ct))
            .Produces<AssignmentView>().WithMetadata(new IdentityProblemTypes(404), new IdentityProblemTypes(409));
        group.MapPost("/{id}/revoke", (AssignmentService service, RequestSession session, Guid id, ExpectedChange request, CancellationToken ct) => service.RevokeAsync(session.Entity!.UserId, id, request, ct))
            .Produces<AssignmentView>().WithMetadata(new IdentityProblemTypes(404), new IdentityProblemTypes(409));
        return endpoints;
    }
}
