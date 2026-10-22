using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.Organization.Data;
using TPR10.Api.Scopes;

namespace TPR10.Api.FieldAdvances.Access;

public sealed class FinanceAccess(Tpr10DbContext db, ScopeAccess session, FinancePersonResolver people, TimeProvider clock)
{
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
