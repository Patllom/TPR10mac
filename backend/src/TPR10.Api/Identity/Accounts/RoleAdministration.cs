using Microsoft.EntityFrameworkCore;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.Identity.Authorization;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Sessions;

namespace TPR10.Api.Identity.Accounts;

public sealed class RoleAdministration(Tpr10DbContext db, RequestSession current, PermissionMutationGuard guard,
    PermissionContext permission, ISessionService sessions, IAuditEventWriter audit, TimeProvider clock)
{
    public Task<IResult> CreateAsync(CreateRoleRequest request, CancellationToken ct) => MutateAsync("roles:manage", async () =>
    {
        if (!ValidName(request.Name) || request.RoleClass is not ("staff" or "system-administration" or "approval" or "accounting" or "finance-data-access")) return Invalid();
        if (await db.Set<IdentityRole>().AnyAsync(x => x.Name == request.Name.Trim(), ct)) return Conflict();
        var role = new IdentityRole { Id = Guid.NewGuid(), Name = request.Name.Trim(), RoleClass = request.RoleClass, CreatedAtUtc = clock.GetUtcNow() };
        db.Add(role);
        await AuditAsync("identity.role.created", "role", role.Id, "name,class", ct);
        return Results.Created($"/api/v1/roles/{role.Id}", new { role.Id, role.Name, role.RoleClass });
    }, ct);

    public Task<IResult> RenameAsync(Guid id, RenameRoleRequest request, CancellationToken ct) => MutateAsync("roles:manage", async () =>
    {
        if (!ValidName(request.Name)) return Invalid();
        var role = await db.Set<IdentityRole>().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (role is null) return Missing();
        if (await db.Set<IdentityRole>().AnyAsync(x => x.Id != id && x.Name == request.Name.Trim(), ct)) return Conflict();
        role.Name = request.Name.Trim();
        await AuditAsync("identity.role.updated", "role", id, "name", ct);
        return Results.Ok(new { role.Id, role.Name, role.RoleClass });
    }, ct);

    public Task<IResult> GrantsAsync(Guid id, RoleGrantsRequest request, CancellationToken ct) => MutateAsync("roles:manage", async () =>
    {
        if (request.PermissionIds is not { Length: <= 100 } ids || ids.Distinct().Count() != ids.Length
            || await db.Set<IdentityPermission>().CountAsync(x => ids.Contains(x.Id) && IdentityCatalog.Capabilities.Contains(x.Capability), ct) != ids.Length) return Invalid();
        if (!await db.Set<IdentityRole>().AnyAsync(x => x.Id == id, ct)) return Missing();
        var mappings = await db.Set<RolePermission>().Where(x => x.RoleId == id).ToListAsync(ct);
        if (mappings.Select(x => x.PermissionId).ToHashSet().SetEquals(ids)) return Results.NoContent();
        var hadAdmin = await PermissionMutationGuard.HasManagingAdminAsync(db, ct);
        db.RemoveRange(mappings.Where(x => !ids.Contains(x.PermissionId)));
        foreach (var permissionId in ids.Except(mappings.Select(x => x.PermissionId))) db.Add(new RolePermission { RoleId = id, PermissionId = permissionId });
        await db.SaveChangesAsync(ct);
        if (hadAdmin && !await PermissionMutationGuard.HasManagingAdminAsync(db, ct)) return LastAdmin();
        foreach (var userId in await db.Set<UserRole>().Where(x => x.RoleId == id).Select(x => x.UserId).OrderBy(x => x).ToArrayAsync(ct))
            await sessions.RevokeUserAsync(userId, "role-grants-changed", ct);
        await AuditAsync("identity.role.permissions.changed", "role", id, "permissions", ct);
        return Results.NoContent();
    }, ct);

    public Task<IResult> AssignAsync(Guid id, UserRolesRequest request, CancellationToken ct) => MutateAsync("roles:manage", async () =>
    {
        if (request.RoleIds is not { Length: <= 20 } ids || ids.Distinct().Count() != ids.Length
            || await db.Set<IdentityRole>().CountAsync(x => ids.Contains(x.Id), ct) != ids.Length) return Invalid();
        if (!await db.Set<IdentityUser>().AnyAsync(x => x.Id == id, ct)) return Missing();
        var mappings = await db.Set<UserRole>().Where(x => x.UserId == id).ToListAsync(ct);
        if (mappings.Select(x => x.RoleId).ToHashSet().SetEquals(ids)) return Results.NoContent();
        var hadAdminClass = await HasAdminClassAsync(ct);
        var hadAdmin = await PermissionMutationGuard.HasManagingAdminAsync(db, ct);
        db.RemoveRange(mappings.Where(x => !ids.Contains(x.RoleId)));
        foreach (var roleId in ids.Except(mappings.Select(x => x.RoleId))) db.Add(new UserRole { UserId = id, RoleId = roleId, CreatedAtUtc = clock.GetUtcNow() });
        await db.SaveChangesAsync(ct);
        if (hadAdminClass && !await HasAdminClassAsync(ct)) return LastAdmin();
        if (hadAdmin && !await PermissionMutationGuard.HasManagingAdminAsync(db, ct)) return LastAdmin();
        await sessions.RevokeUserAsync(id, "user-roles-changed", ct);
        await AuditAsync("identity.user.roles.changed", "user", id, "roles", ct);
        return Results.NoContent();
    }, ct);

    public Task<IResult> SignOutAsync(Guid id, CancellationToken ct) => MutateAsync("users:manage", async () =>
    {
        if (!await db.Set<IdentityUser>().AnyAsync(x => x.Id == id, ct)) return Missing();
        await sessions.RevokeUserAsync(id, "admin-sign-out", ct);
        await AuditAsync("identity.user.sessions.revoked", "user", id, "sessions", ct);
        return Results.NoContent();
    }, ct);

    private async Task<IResult> MutateAsync(string capability, Func<Task<IResult>> mutate, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct);
        if (current.Entity is null || !await guard.AllowsAsync(current.Entity.UserId, capability, ct))
            return Results.Problem(statusCode: 403, type: "urn:tpr10:permission-denied", title: "สิทธิ์หรือ session เปลี่ยนแปลง กรุณาเข้าสู่ระบบใหม่");
        var result = await mutate();
        if (result is IStatusCodeHttpResult { StatusCode: >= 400 }) return result;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }
    private Task AuditAsync(string action, string type, Guid id, string changes, CancellationToken ct) => audit.WriteAsync(
        new SecurityAuditRequest(current.Entity!.UserId, permission.ActingRoleId, null, null, null, action, type, id, "success",
            new Dictionary<string, string> { ["changed-fields"] = changes }), ct);
    private static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 120 && !name.Any(char.IsControl);
    private Task<bool> HasAdminClassAsync(CancellationToken ct) => (from u in db.Set<IdentityUser>()
                                                                    join ur in db.Set<UserRole>() on u.Id equals ur.UserId
                                                                    join role in db.Set<IdentityRole>() on ur.RoleId equals role.Id
                                                                    where u.IsActive && role.RoleClass == "system-administration"
                                                                    select u.Id).AnyAsync(ct);
    private static IResult Invalid() => Results.Problem(statusCode: 400, title: "ข้อมูล role หรือ permission ไม่ถูกต้อง");
    private static IResult Missing() => Results.Problem(statusCode: 404, title: "ไม่พบผู้ใช้หรือ role");
    private static IResult Conflict() => Results.Problem(statusCode: 409, title: "ชื่อ role นี้ถูกใช้งานแล้ว");
    private static IResult LastAdmin() => Results.Problem(statusCode: 409, title: "ไม่สามารถถอดสิทธิ์จัดการของผู้ดูแลคนสุดท้ายได้");
}
