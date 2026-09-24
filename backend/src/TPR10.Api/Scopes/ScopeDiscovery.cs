using Microsoft.EntityFrameworkCore;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Organization.Data;

namespace TPR10.Api.Scopes;

public sealed record ScopeChoice(ScopeKey Scope, string WorkspaceName, string? ProjectName, string? SiteName, string[] Capabilities);

public sealed class ScopeDiscovery(Tpr10DbContext db, ScopeAccess access, RequestSession current, IAuditEventWriter audit)
{
    public async Task<IResult> ListAsync(int page, int pageSize, CancellationToken ct)
    {
        try
        {
            await using var tx = await ScopeOperation.BeginAsync(db, ct);
            var session = await access.ValidateSessionAsync(false, ct);
            pageSize = Math.Min(pageSize, 100);
            var offset = ((long)page - 1) * pageSize;
            var status = session.Status ?? (page < 1 || pageSize < 1 || offset > int.MaxValue ? 400 : (int?)null);
            if (status is { } denied)
            {
                await ScopeOperation.DenialAuditAsync(audit, current.Entity?.UserId, null, "scope-discovery", ct);
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
                return ScopeOperation.Problem(denied);
            }
            var assignments = access.ActiveAssignments(session.ActorId);
            var tuples = assignments.Select(a => new { a.WorkspaceId, a.ProjectId, a.SiteId }).Distinct();
            var total = await tuples.CountAsync(ct);
            var pageQuery = tuples.OrderBy(a => a.WorkspaceId).ThenBy(a => a.ProjectId != null).ThenBy(a => a.ProjectId)
                .ThenBy(a => a.SiteId != null).ThenBy(a => a.SiteId).Skip((int)offset).Take(pageSize);
            var keys = await pageQuery.Select(k => new
            {
                k.WorkspaceId,
                k.ProjectId,
                k.SiteId,
                WorkspaceName = db.Set<Workspace>().Where(w => w.Id == k.WorkspaceId).Select(w => w.Name).First(),
                ProjectName = db.Set<Project>().Where(p => p.Id == k.ProjectId).Select(p => p.Name).FirstOrDefault(),
                SiteName = db.Set<Site>().Where(s => s.Id == k.SiteId).Select(s => s.Name).FirstOrDefault()
            }).ToArrayAsync(ct);
            // Only this page's tuples enter the grants query; no N+1 queries under the shared lock.
            var grants = await (from a in assignments
                                join rp in db.Set<RolePermission>() on a.RoleId equals rp.RoleId
                                join p in db.Set<IdentityPermission>() on rp.PermissionId equals p.Id
                                where p.Domain == ScopeCatalog.BusinessDomain
                                    && pageQuery.Any(k => k.WorkspaceId == a.WorkspaceId && k.ProjectId == a.ProjectId && k.SiteId == a.SiteId)
                                select new { a.WorkspaceId, a.ProjectId, a.SiteId, p.Capability }).Distinct().ToArrayAsync(ct);
            var choices = keys.Select(k => new ScopeChoice(new(k.WorkspaceId, k.ProjectId, k.SiteId), k.WorkspaceName, k.ProjectName, k.SiteName,
                grants.Where(g => g.WorkspaceId == k.WorkspaceId && g.ProjectId == k.ProjectId && g.SiteId == k.SiteId)
                    .Select(g => g.Capability).OrderBy(x => x, StringComparer.Ordinal).ToArray())).ToArray();
            await audit.WriteAsync(new SecurityAuditRequest(session.ActorId, null, null, null, null,
                "scope.discovery.list", "scope-discovery", null, "success", new Dictionary<string, string>()), ct);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return Results.Ok(new Page<ScopeChoice>(choices, total, page, pageSize));
        }
        catch (Exception error) when (ScopeOperation.IsDatabaseFault(error))
        {
            db.ChangeTracker.Clear();
            return ScopeOperation.Problem(503);
        }
    }
}
