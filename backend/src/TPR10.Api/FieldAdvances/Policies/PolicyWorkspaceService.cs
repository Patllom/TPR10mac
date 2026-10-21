using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Organization.Data;
using TPR10.Api.Scopes;

namespace TPR10.Api.FieldAdvances.Policies;

public sealed class PolicyWorkspaceService(Tpr10DbContext db, FinanceAccess access, ScopeAccess sessions, RequestSession session)
{
    public async Task<FinanceResult<PolicyScopeChoice[]>> ScopesAsync(int page, int pageSize, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct);
        var actor = session.Entity?.UserId ?? Guid.Empty;
        var identity = await sessions.ValidateSessionAsync(true, ct);
        if (identity.Status is not null || actor == Guid.Empty || identity.ActorId != actor) return PolicyReadGuard.Denied<PolicyScopeChoice[]>();
        if (page < 1 || pageSize is < 1 or > 100 || (long)(page - 1) * pageSize > int.MaxValue) return PolicyReadGuard.Invalid<PolicyScopeChoice[]>();
        // Candidate IDs belong to this account only. Authorization is re-evaluated, never inferred from proposals.
        var candidates = await db.Set<GrantProposal>().AsNoTracking().Where(x => x.RecipientId == actor)
            .Select(x => new { x.WorkspaceId, x.ProjectId }).Distinct().OrderBy(x => x.WorkspaceId).ThenBy(x => x.ProjectId).ToArrayAsync(ct);
        var choices = new List<PolicyScopeChoice>();
        foreach (var candidate in candidates)
        {
            var scope = new FinanceScope(candidate.WorkspaceId, candidate.ProjectId);
            var capabilities = await Capabilities(actor, scope, ct);
            if (capabilities.Length == 0) continue;
            var workspace = await db.Set<Workspace>().AsNoTracking().SingleAsync(x => x.Id == scope.WorkspaceId, ct);
            var project = scope.ProjectId is null ? null : await db.Set<Project>().AsNoTracking().SingleAsync(x => x.Id == scope.ProjectId && x.WorkspaceId == scope.WorkspaceId, ct);
            choices.Add(new(scope, workspace.Name, project?.Name, capabilities));
        }
        var selected = choices.Skip((page - 1) * pageSize).Take(pageSize).ToArray();
        foreach (var choice in choices)
            if (!(await Capabilities(actor, choice.Scope, ct)).SequenceEqual(choice.Capabilities)) return PolicyReadGuard.Denied<PolicyScopeChoice[]>();
        if ((await sessions.ValidateSessionAsync(true, ct)).Status is not null) return PolicyReadGuard.Denied<PolicyScopeChoice[]>();
        await tx.CommitAsync(ct);
        return new(200, "ok", selected);
    }
    private async Task<string[]> Capabilities(Guid actor, FinanceScope scope, CancellationToken ct)
    {
        var result = new List<string>();
        foreach (var capability in new[] { scope.ProjectId is null ? "policy.edit.company" : "policy.edit.project", "policy.review" })
            if ((await access.CheckAsync(actor, scope, capability, ct)).Allowed) result.Add(capability);
        return result.ToArray();
    }
}
