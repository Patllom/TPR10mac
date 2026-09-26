using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.Identity.Data;

namespace TPR10.Api.Attendance.Storage;

internal static class MigrationAuthority
{
    // Durable jobs are not tied to an expired web session, but never survive loss of the creator's authority.
    internal static Task<bool> AllowsAsync(Tpr10DbContext db, Guid actor, CancellationToken ct) => db.Set<IdentityUser>().AnyAsync(u => u.Id == actor && u.IsActive
        && (from ur in db.Set<UserRole>()
            join rp in db.Set<RolePermission>() on ur.RoleId equals rp.RoleId
            join p in db.Set<IdentityPermission>() on rp.PermissionId equals p.Id
            where ur.UserId == actor && p.Capability == StorageRegistry.Capability && p.Domain == "system"
            select p.Id).Any(), ct);
}
