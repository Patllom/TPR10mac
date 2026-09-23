using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.Identity;
using TPR10.Api.Identity.Data;
using TPR10.Api.Organization.Data;
using TPR10.Api.Scopes.Data;

namespace TPR10.Api.Organization;

// Determines assurance, never grants or flattens scoped authorization.
public sealed class EffectiveRolePolicy(Tpr10DbContext db) : IEffectiveRolePolicy
{
    public Task<bool> RequiresMfaAsync(Guid userId, CancellationToken ct) => db.Set<IdentityUser>().AnyAsync(u => u.Id == userId && u.IsActive
        && ((from ur in db.Set<UserRole>()
             join r in db.Set<IdentityRole>() on ur.RoleId equals r.Id
             where ur.UserId == u.Id && r.RoleClass != "staff"
             select r.Id).Any()
            || (from a in db.Set<ScopeAssignment>()
                join r in db.Set<IdentityRole>() on a.RoleId equals r.Id
                where a.UserId == u.Id && a.RevokedAtUtc == null
                    && (r.RoleClass == "approval" || r.RoleClass == "accounting" || r.RoleClass == "finance-data-access")
                    && db.Set<Workspace>().Any(w => w.Id == a.WorkspaceId && w.IsActive)
                    && (a.ProjectId == null || db.Set<Project>().Any(p => p.Id == a.ProjectId && p.WorkspaceId == a.WorkspaceId && p.IsActive))
                    && (a.SiteId == null || db.Set<Site>().Any(s => s.Id == a.SiteId && s.ProjectId == a.ProjectId && s.WorkspaceId == a.WorkspaceId && s.IsActive))
                select a.Id).Any()), ct);

    // Include every non-revoked assignment, even under an inactive ancestor:
    // revocation is conservative and must not leave a stale session behind.
    public Task<Guid[]> AffectedUsersAsync(Guid roleId, CancellationToken ct) => db.Set<UserRole>()
        .Where(x => x.RoleId == roleId).Select(x => x.UserId)
        .Union(db.Set<ScopeAssignment>().Where(x => x.RoleId == roleId && x.RevokedAtUtc == null).Select(x => x.UserId))
        .OrderBy(id => id).ToArrayAsync(ct);
}
