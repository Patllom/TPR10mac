using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.Organization.Data;
using TPR10.Api.Scopes;

namespace TPR10.Api.FieldAdvances.Access;

public sealed class FinanceAccess(Tpr10DbContext db, ScopeAccess session, FinancePersonResolver people, TimeProvider clock)
{
    // The caller retains the same transaction and identity lock as scalar authorization.
    // Every invocation reads fresh evidence; final checks must invoke this again.
    public async Task<Dictionary<FinanceScope, string[]>> CheckManyAsync(Guid actorId, FinanceScope[] scopes,
        string[] capabilities, CancellationToken ct)
    {
        var result = scopes.Distinct().ToDictionary(x => x, _ => Array.Empty<string>());
        var identity = await session.ValidateSessionAsync(requireMfa: true, ct);
        if (identity.Status is not null || identity.ActorId != actorId || actorId == Guid.Empty) return result;
        var requested = capabilities.Where(FinanceCapabilities.Contains).Distinct().ToArray();
        foreach (var batch in result.Keys.Where(x => x.WorkspaceId != Guid.Empty && x.ProjectId != Guid.Empty).ToArray().Chunk(100))
        {
            var workspaces = batch.Select(x => x.WorkspaceId).Distinct().ToArray();
            var projects = batch.Where(x => x.ProjectId != null).Select(x => x.ProjectId!.Value).Distinct().ToArray();
            var company = batch.Any(x => x.ProjectId == null);
            var persons = await people.ResolveManyAsync(workspaces.Select(x => (actorId, x)).ToArray(), ct);
            var now = clock.GetUtcNow();
            var evidence = await FinanceGrantQueries.Current(db, now).Where(x => x.RecipientId == actorId
                && workspaces.Contains(x.WorkspaceId)
                && (x.ProjectId == null ? company : projects.Contains(x.ProjectId.Value))
                && (x.ProjectId == null || db.Set<Project>().Any(p => p.Id == x.ProjectId && p.WorkspaceId == x.WorkspaceId && p.IsActive)))
                .Select(x => new { x.WorkspaceId, x.ProjectId, x.RecipientPersonId, x.Capabilities }).ToArrayAsync(ct);
            foreach (var scope in batch)
            {
                if (!persons.TryGetValue((actorId, scope.WorkspaceId), out var person)) continue;
                var allowed = evidence.Where(x => x.WorkspaceId == scope.WorkspaceId && x.ProjectId == scope.ProjectId
                    && x.RecipientPersonId == person).SelectMany(x => x.Capabilities).ToHashSet();
                result[scope] = requested.Where(x => allowed.Contains(x)
                    && !(scope.ProjectId is null && x is "request.create" or "request.delegate")).ToArray();
            }
        }
        return result;
    }

    // Caller owns the transaction and identity advisory lock 7241002 through the protected operation.
    // This is capability access only; request operations must also apply server-owned participation history.
    public async Task<FinanceDecision> CheckAsync(Guid actorId, FinanceScope scope, string capability, CancellationToken ct)
    {
        var identity = await session.ValidateSessionAsync(requireMfa: true, ct);
        if (identity.Status is not null || identity.ActorId != actorId || actorId == Guid.Empty
            || scope is null || scope.WorkspaceId == Guid.Empty || scope.ProjectId == Guid.Empty
            || scope.ProjectId is null && capability is "request.create" or "request.delegate"
            || !FinanceCapabilities.Contains(capability)) return Denied();
        var personId = await people.ResolveAsync(actorId, scope.WorkspaceId, ct);
        if (personId is null) return Denied();
        if (scope.ProjectId is { } projectId && !await db.Set<Project>().AnyAsync(p => p.Id == projectId
                && p.WorkspaceId == scope.WorkspaceId && p.IsActive, ct)) return Denied();
        var now = clock.GetUtcNow();
        var allowed = await FinanceGrantQueries.Current(db, scope, capability, now)
            .AnyAsync(proposal => proposal.RecipientId == actorId && proposal.RecipientPersonId == personId, ct);
        return allowed ? new(true, "allowed") : Denied();
    }

    private static FinanceDecision Denied() => new(false, "finance-access-denied");
}
