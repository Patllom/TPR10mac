using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Sessions;

namespace TPR10.Api.Identity.Accounts;

public sealed record CreateRoleRequest(string Name, string RoleClass);
public sealed record RenameRoleRequest(string Name);
public sealed record RoleGrantsRequest(Guid[] PermissionIds);
public sealed record UserRolesRequest(Guid[] RoleIds);

public static class RoleEndpoints
{
    public static void MapRoleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/roles").RequireAuthorization("roles:manage");
        group.MapGet("", async (Tpr10DbContext db, int? page, int? pageSize, CancellationToken ct) =>
        {
            var number = page ?? 1;
            var size = Math.Min(pageSize ?? 25, 100);
            var offset = ((long)number - 1) * size;
            if (number < 1 || size < 1 || offset > int.MaxValue)
                return Results.Problem(statusCode: 400, title: "เลขหน้าหรือจำนวนรายการต่อหน้าไม่ถูกต้อง");
            var total = await db.Set<IdentityRole>().CountAsync(ct);
            var items = await db.Set<IdentityRole>().AsNoTracking().OrderBy(x => x.Name).ThenBy(x => x.Id)
                .Skip((int)offset).Take(size).Select(x => new
                {
                    x.Id,
                    x.Name,
                    x.RoleClass,
                    PermissionIds = db.Set<RolePermission>().Where(p => p.RoleId == x.Id).Select(p => p.PermissionId).ToArray()
                }).ToArrayAsync(ct);
            return Results.Ok(new { items, total, page = number, pageSize = size });
        });
        group.MapPost("", (RoleAdministration roles, CreateRoleRequest request, CancellationToken ct) => roles.CreateAsync(request, ct));
        group.MapPatch("/{id:guid}", (RoleAdministration roles, Guid id, RenameRoleRequest request, CancellationToken ct) => roles.RenameAsync(id, request, ct));
        group.MapPut("/{id:guid}/permissions", (RoleAdministration roles, Guid id, RoleGrantsRequest request, CancellationToken ct) => roles.GrantsAsync(id, request, ct));
        endpoints.MapPut("/api/v1/users/{id:guid}/roles", (RoleAdministration roles, Guid id, UserRolesRequest request, CancellationToken ct) => roles.AssignAsync(id, request, ct))
            .RequireAuthorization("roles:manage");
        endpoints.MapPost("/api/v1/users/{id:guid}/sign-out-everywhere", (RoleAdministration roles, Guid id, CancellationToken ct) => roles.SignOutAsync(id, ct))
            .RequireAuthorization("users:manage");
        endpoints.MapPost("/api/v1/users/{id:guid}/mfa/recover", (Mfa.OperatorMfaRecovery recovery, RequestSession session, Guid id, OperatorRecoveryRequest request, CancellationToken ct) =>
            recovery.RecoverAsync(session.Entity!.Id, id, request.Reason, request.EvidenceReference, ct)).RequireAuthorization("users:recover-mfa");
        endpoints.MapGet("/api/v1/permissions", async (Tpr10DbContext db, CancellationToken ct) => Results.Ok(await db.Set<IdentityPermission>()
            .AsNoTracking().Where(x => IdentityCatalog.Capabilities.Contains(x.Capability)).OrderBy(x => x.Capability).ToArrayAsync(ct)))
            .RequireAuthorization("roles:read");
    }
    public sealed record OperatorRecoveryRequest(string Reason, string EvidenceReference);
}
