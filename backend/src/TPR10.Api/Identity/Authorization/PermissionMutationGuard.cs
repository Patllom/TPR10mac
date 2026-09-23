using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Sessions;

namespace TPR10.Api.Identity.Authorization;

// Called after the shared identity lock, inside the mutation transaction.
public sealed class PermissionMutationGuard(Tpr10DbContext db, RequestSession current, PermissionContext permission, TimeProvider clock)
{
    public async Task<bool> AllowsAsync(Guid actorId, string capability, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (current.Entity is not { } candidate || candidate.UserId != actorId) return false;
        var valid = await db.Set<IdentitySession>().AsNoTracking().AnyAsync(s => s.Id == candidate.Id && s.UserId == actorId
            && s.RevokedAtUtc == null && s.Stage == SessionStage.Active && s.ExpiresAtUtc > now && s.LastSeenAtUtc > now.AddMinutes(-30)
            && s.MfaVerifiedAtUtc > now.AddMinutes(-15) && s.MfaVerifiedAtUtc <= now
            && db.Set<IdentityUser>().Any(u => u.Id == actorId && u.IsActive && u.SecurityVersion == s.SecurityVersion)
            && !db.Set<LocalCredential>().Any(c => c.UserId == actorId && c.MustChangePassword)
            && db.Set<MfaFactor>().Any(f => f.UserId == actorId && f.ConfirmedAtUtc != null && f.RevokedAtUtc == null), ct);
        if (!valid) return false;
        var role = await (from ur in db.Set<UserRole>()
                          join rp in db.Set<RolePermission>() on ur.RoleId equals rp.RoleId
                          join p in db.Set<IdentityPermission>() on rp.PermissionId equals p.Id
                          where ur.UserId == actorId && p.Capability == capability
                          orderby ur.RoleId
                          select (Guid?)ur.RoleId).FirstOrDefaultAsync(ct);
        if (role is null) return false;
        permission.ActingRoleId = role;
        return true;
    }

    public static Task<bool> HasManagingAdminAsync(Tpr10DbContext db, CancellationToken ct) => db.Set<IdentityUser>().AnyAsync(u => u.IsActive
        && (from ur in db.Set<UserRole>()
            join r in db.Set<IdentityRole>() on ur.RoleId equals r.Id
            where ur.UserId == u.Id && r.RoleClass == "system-administration"
            select r.Id).Any()
        && (from ur in db.Set<UserRole>()
            join rp in db.Set<RolePermission>() on ur.RoleId equals rp.RoleId
            join p in db.Set<IdentityPermission>() on rp.PermissionId equals p.Id
            where ur.UserId == u.Id && p.Capability == "users:manage"
            select p.Id).Any()
        && (from ur in db.Set<UserRole>()
            join rp in db.Set<RolePermission>() on ur.RoleId equals rp.RoleId
            join p in db.Set<IdentityPermission>() on rp.PermissionId equals p.Id
            where ur.UserId == u.Id && p.Capability == "roles:manage"
            select p.Id).Any(), ct);
}
