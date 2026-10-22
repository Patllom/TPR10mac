using Microsoft.EntityFrameworkCore;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.Identity.Authorization;
using TPR10.Api.Scopes.Data;
using TPR10.Api.FieldAdvances.Data;
using FinanceGrant = TPR10.Api.FieldAdvances.Data.GrantAssignment;

namespace TPR10.Api.Scopes.Assignments;

// Caller owns the transaction and takes identity advisory lock 7241002 first.
// Does not save, commit, or revoke sessions: those belong to the use case.
public sealed class AssignmentLifecycle(Tpr10DbContext db, IAuditEventWriter audit, TimeProvider clock, PermissionContext permission)
{
    public async Task<int> RevokeForUserAsync(Guid userId, Guid actorId, string reason, CancellationToken ct)
    {
        RequireTransaction();
        var rows = await db.Set<ScopeAssignment>().Where(a => a.UserId == userId && a.RevokedAtUtc == null).OrderBy(a => a.Id).ToArrayAsync(ct);
        // A caller may invoke us twice before SaveChanges; tracked revocations
        // must also be excluded, not just the values currently in the database.
        rows = rows.Where(a => a.RevokedAtUtc is null).ToArray();
        await RevokeAsync(rows, actorId, reason, ct);
        return rows.Length;
    }

    public async Task<Guid[]> RevokeForScopeAsync(ScopeKey scope, Guid actorId, string reason, CancellationToken ct)
    {
        RequireTransaction();
        if (!scope.IsValid) throw new ArgumentException("Invalid revocation scope.", nameof(scope));
        // Deliberate cascade for deactivation, NOT authorization inheritance.
        var rows = await db.Set<ScopeAssignment>().Where(a => a.RevokedAtUtc == null && a.WorkspaceId == scope.WorkspaceId
            && (scope.ProjectId == null || a.ProjectId == scope.ProjectId)
            && (scope.SiteId == null || a.SiteId == scope.SiteId)).OrderBy(a => a.Id).ToArrayAsync(ct);
        rows = rows.Where(a => a.RevokedAtUtc is null).ToArray();
        await RevokeAsync(rows, actorId, reason, ct);
        var financeRecipients = await RevokeFinanceForScopeAsync(scope, actorId, reason, ct);
        return rows.Select(a => a.UserId).Concat(financeRecipients).Distinct().OrderBy(id => id).ToArray();
    }

    private async Task<Guid[]> RevokeFinanceForScopeAsync(ScopeKey scope, Guid actorId, string reason, CancellationToken ct)
    {
        // Finance grants have no site scope; closing one site must not revoke the whole project's grants.
        if (scope.SiteId is not null) return [];
        var candidates = await (from assignment in db.Set<FinanceGrant>()
                                join proposal in db.Set<GrantProposal>() on assignment.ProposalId equals proposal.Id
                                where assignment.RevokedAtUtc == null && assignment.WorkspaceId == scope.WorkspaceId
                                    && (scope.ProjectId == null || assignment.ProjectId == scope.ProjectId)
                                orderby assignment.Id
                                select new { Assignment = assignment, proposal.RecipientId }).ToArrayAsync(ct);
        // Repeated calls before the caller saves must not increment versions or audit twice.
        var rows = candidates.Where(x => x.Assignment.RevokedAtUtc is null).ToArray();
        var now = clock.GetUtcNow();
        foreach (var row in rows)
        {
            var assignment = row.Assignment;
            assignment.RevokedAtUtc = now;
            assignment.RevokedBy = actorId;
            assignment.RevocationReason = reason.Trim();
            assignment.Version = checked(assignment.Version + 1);
            await audit.WriteAsync(new SecurityAuditRequest(actorId, permission.ActingRoleId, assignment.WorkspaceId, assignment.ProjectId, null,
                "field-advance.grant.revoked", "field-advance-grant", assignment.Id, "success", new Dictionary<string, string>
                { ["reason"] = reason.Trim(), ["changed-fields"] = "revoked-at,revoked-by,revocation-reason,version" }), ct);
        }
        return rows.Select(x => x.RecipientId).Distinct().OrderBy(id => id).ToArray();
    }

    private void RequireTransaction()
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Assignment revocation requires a caller transaction and identity lock.");
    }

    private async Task RevokeAsync(ScopeAssignment[] rows, Guid actorId, string reason, CancellationToken ct)
    {
        if (actorId == Guid.Empty || string.IsNullOrWhiteSpace(reason) || reason.Length > 500 || reason.Any(char.IsControl))
            throw new ArgumentException("Revocation requires an actor and a bounded reason.");
        var now = clock.GetUtcNow();
        foreach (var row in rows)
        {
            row.RevokedAtUtc = now;
            row.RevokedBy = actorId;
            row.RevocationReason = reason.Trim();
            row.Version = checked(row.Version + 1);
            await audit.WriteAsync(new SecurityAuditRequest(actorId, permission.ActingRoleId, row.WorkspaceId, row.ProjectId, row.SiteId,
                "scope.assignment.revoked", "scope-assignment", row.Id, "success", new Dictionary<string, string>
                { ["reason"] = reason.Trim(), ["changed-fields"] = "revoked-at,revoked-by,revocation-reason,version" }), ct);
        }
    }
}
