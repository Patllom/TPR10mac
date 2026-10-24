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
        var candidates = await FinanceScopeQueries.Candidates(db, actor).ToArrayAsync(ct);
        var allowed = await access.CheckManyAsync(actor, candidates.Select(x => new FinanceScope(x.WorkspaceId, x.ProjectId)).ToArray(),
            ["policy.edit.company", "policy.edit.project", "policy.review"], ct);
        var choices = new List<PolicyScopeChoice>();
        foreach (var candidate in candidates)
        {
            var scope = new FinanceScope(candidate.WorkspaceId, candidate.ProjectId);
            var capabilities = Relevant(scope, allowed[scope]);
            if (capabilities.Length == 0) continue;
            choices.Add(new(scope, candidate.WorkspaceName, candidate.ProjectName, capabilities));
        }
        var selected = choices.Skip((page - 1) * pageSize).Take(pageSize).ToArray();
        var final = await access.CheckManyAsync(actor, choices.Select(x => x.Scope).ToArray(),
            ["policy.edit.company", "policy.edit.project", "policy.review"], ct);
        foreach (var choice in choices)
            if (!Relevant(choice.Scope, final[choice.Scope]).SequenceEqual(choice.Capabilities)) return PolicyReadGuard.Denied<PolicyScopeChoice[]>();
        if ((await sessions.ValidateSessionAsync(true, ct)).Status is not null) return PolicyReadGuard.Denied<PolicyScopeChoice[]>();
        await tx.CommitAsync(ct);
        return new(200, "ok", selected);
    }
    private static string[] Relevant(FinanceScope scope, string[] allowed) =>
        allowed.Where(x => x == "policy.review" || x == (scope.ProjectId is null ? "policy.edit.company" : "policy.edit.project")).ToArray();
}
