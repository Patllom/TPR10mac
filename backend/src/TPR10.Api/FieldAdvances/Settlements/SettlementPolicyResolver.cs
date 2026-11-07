using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Policies;

namespace TPR10.Api.FieldAdvances.Settlements;

public sealed record CorrectionWindow(int Days, Guid PolicyRevisionId, string ContentHash);

// Internal policy lookup for an already authorized settlement command. The caller must
// retain its transaction/identity lock through resolution and persistence of the round.
// This service grants no access and does not create or transition a settlement round.
public sealed class SettlementPolicyResolver(Tpr10DbContext db, PolicyResolver policies, TimeProvider clock)
{
    internal async Task<FinanceResult<CorrectionWindow>> ResolveCorrectionAsync(Guid requestId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Correction policy resolution requires an authorized transaction.");
        var request = await db.Set<AdvanceRequest>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == requestId, ct);
        if (request is null) return new(403, "finance-access-denied", null);
        var result = await policies.ResolveAuthorizedAsync(new(request.WorkspaceId, request.ProjectId), clock.GetUtcNow(), ct);
        if (result.Status != 200) return new(result.Status, result.Code, null);
        var snapshot = result.Value!;
        if (snapshot.Content.CorrectionDays is not > 0)
            return new(409, "correction-policy-not-ready", null);
        // Inherit follows the current company content; its project revision is approval
        // provenance, not the revision that supplied these days/hash.
        var sourceId = snapshot.CompanyVersionId;
        if (snapshot.ProjectVersionId is { } projectId
            && await db.Set<PolicyRevision>().AsNoTracking().AnyAsync(x => x.Id == projectId
                && x.WorkspaceId == request.WorkspaceId && x.ProjectId == request.ProjectId && x.Mode == "Override", ct))
            sourceId = projectId;
        return new(200, "ok", new(snapshot.Content.CorrectionDays.Value, sourceId, snapshot.ContentHash));
    }
}
