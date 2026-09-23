using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.Identity;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Organization.Data;
using TPR10.Api.Scopes.Data;

namespace TPR10.Api.Scopes;

public sealed class ScopeAccess(Tpr10DbContext db, RequestSession current, IEffectiveRolePolicy roles, TimeProvider clock)
{
    public async Task<ScopeDecision> ResolveAsync(ScopeKey key, string capability, bool requireMfa, CancellationToken ct)
    {
        RequireTransaction();
        if (!key.IsValid) return Denied(400);
        var session = await ValidateSessionAsync(requireMfa || capability is "scope-probe:export" or "scope-probe:restricted-read", ct);
        if (session.Status is { } status) return Denied(status);
        var assignments = ActiveAssignments(session.ActorId).Where(a => a.WorkspaceId == key.WorkspaceId
            && a.ProjectId == key.ProjectId && a.SiteId == key.SiteId);
        if (!await assignments.AnyAsync(ct)) return Denied(404);
        var grants = await (from a in assignments
                            join rp in db.Set<RolePermission>() on a.RoleId equals rp.RoleId
                            join p in db.Set<IdentityPermission>() on rp.PermissionId equals p.Id
                            where p.Domain == ScopeCatalog.BusinessDomain
                            orderby a.RoleId, a.Id
                            select new { a.Id, a.RoleId, p.Capability }).ToArrayAsync(ct);
        var selected = grants.FirstOrDefault(g => g.Capability == capability);
        if (selected is null) return Denied(403);
        return new(new ScopeContext(session.ActorId, key, capability, selected.RoleId, selected.Id,
            session.RecentMfa && grants.Any(g => g.Capability == "scope-probe:restricted-read")), null, null);
    }

    // Caller owns transaction and acquires 7241002 before invoking authorization.
    internal async Task<(Guid ActorId, bool RecentMfa, int? Status)> ValidateSessionAsync(bool requireMfa, CancellationToken ct)
    {
        RequireTransaction();
        if (current.Entity is not { } candidate) return (Guid.Empty, false, 401);
        var now = clock.GetUtcNow();
        var session = await db.Set<IdentitySession>().AsNoTracking().SingleOrDefaultAsync(s => s.Id == candidate.Id && s.UserId == candidate.UserId
            && s.RevokedAtUtc == null && s.ExpiresAtUtc > now && s.LastSeenAtUtc > now.AddMinutes(-30)
            && db.Set<IdentityUser>().Any(u => u.Id == s.UserId && u.IsActive && u.SecurityVersion == s.SecurityVersion), ct);
        if (session is null) return (Guid.Empty, false, 401);
        var actor = session.UserId;
        if (session.Stage != SessionStage.Active || await db.Set<LocalCredential>().AnyAsync(x => x.UserId == actor && x.MustChangePassword, ct))
            return (actor, false, 403);
        var confirmed = await db.Set<MfaFactor>().AnyAsync(x => x.UserId == actor && x.ConfirmedAtUtc != null && x.RevokedAtUtc == null, ct);
        var recent = confirmed && session.MfaVerifiedAtUtc > now.AddMinutes(-15) && session.MfaVerifiedAtUtc <= now;
        if (!recent && (requireMfa || confirmed || session.MfaVerifiedAtUtc != null || await roles.RequiresMfaAsync(actor, ct)))
            return (actor, false, 403);
        return (actor, recent, null);
    }

    internal IQueryable<ScopeAssignment> ActiveAssignments(Guid actor) =>
        from a in db.Set<ScopeAssignment>().AsNoTracking()
        join r in db.Set<IdentityRole>() on a.RoleId equals r.Id
        where a.UserId == actor && a.RevokedAtUtc == null
            && (r.RoleClass == "staff" || r.RoleClass == "approval" || r.RoleClass == "accounting" || r.RoleClass == "finance-data-access")
            && db.Set<Workspace>().Any(w => w.Id == a.WorkspaceId && w.IsActive)
            && (a.ProjectId == null || db.Set<Project>().Any(p => p.Id == a.ProjectId && p.WorkspaceId == a.WorkspaceId && p.IsActive))
            && (a.SiteId == null || db.Set<Site>().Any(s => s.Id == a.SiteId && s.ProjectId == a.ProjectId && s.WorkspaceId == a.WorkspaceId && s.IsActive))
        select a;

    private void RequireTransaction()
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Scope authorization requires a caller transaction and identity lock.");
    }
    private static ScopeDecision Denied(int status) => new(null, status, ScopeOperation.ProblemType(status));
}
