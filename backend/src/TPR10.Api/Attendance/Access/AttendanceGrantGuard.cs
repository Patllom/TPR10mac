using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.Identity.Data;
using TPR10.Api.Scopes.Data;

namespace TPR10.Api.Attendance.Access;

// Called under the identity transaction/lock. No grants or sessions are changed here.
public sealed class AttendanceGrantGuard(Tpr10DbContext db)
{
    public async Task<bool> WouldElevateSelfAsync(Guid actorId, Guid targetRoleId, Guid[] proposedPermissionIds, CancellationToken ct)
    {
        RequireTransaction();
        var held = await db.Set<UserRole>().AnyAsync(x => x.UserId == actorId && x.RoleId == targetRoleId, ct)
            || await db.Set<ScopeAssignment>().AnyAsync(x => x.UserId == actorId && x.RoleId == targetRoleId && x.RevokedAtUtc == null, ct);
        if (!held) return false;
        var previous = await db.Set<RolePermission>().Where(x => x.RoleId == targetRoleId).Select(x => x.PermissionId).ToArrayAsync(ct);
        var added = proposedPermissionIds.Except(previous).ToArray();
        return await db.Set<IdentityPermission>().AnyAsync(x => added.Contains(x.Id) && x.Capability.StartsWith("attendance:"), ct);
    }

    public async Task<bool> WouldAssignSelfAsync(Guid actorId, Guid targetUserId, Guid[] proposedRoleIds, CancellationToken ct)
    {
        RequireTransaction();
        if (actorId != targetUserId) return false;
        var previousRoles = db.Set<UserRole>().Where(x => x.UserId == actorId).Select(x => x.RoleId);
        var previous = await Capabilities(previousRoles).ToArrayAsync(ct);
        var next = await Capabilities(db.Set<IdentityRole>().Where(x => proposedRoleIds.Contains(x.Id)).Select(x => x.Id)).ToArrayAsync(ct);
        return next.Except(previous).Any();
    }

    public async Task<bool> ValidRoleGrantsAsync(Guid roleId, Guid[] permissionIds, CancellationToken ct)
    {
        RequireTransaction();
        if (!await db.Set<IdentityPermission>().AnyAsync(x => permissionIds.Contains(x.Id) && x.Domain == AttendanceCatalog.Domain, ct)) return true;
        return await db.Set<IdentityRole>().AnyAsync(x => x.Id == roleId
            && (x.RoleClass == "approval" || x.RoleClass == "accounting" || x.RoleClass == "finance-data-access"), ct);
    }

    private IQueryable<Guid> Capabilities(IQueryable<Guid> roles) =>
        from mapping in db.Set<RolePermission>()
        join p in db.Set<IdentityPermission>() on mapping.PermissionId equals p.Id
        where roles.Contains(mapping.RoleId) && p.Capability.StartsWith("attendance:")
        select p.Id;

    private void RequireTransaction()
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Attendance grant checks require a caller transaction and identity lock.");
    }
}
