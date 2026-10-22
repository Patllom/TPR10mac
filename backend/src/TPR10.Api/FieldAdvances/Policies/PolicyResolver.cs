using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.Identity.Sessions;

namespace TPR10.Api.FieldAdvances.Policies;

public sealed class PolicyResolver(Tpr10DbContext db, FinanceAccess access, RequestSession session, TimeProvider clock)
{
    public Task<FinanceResult<PolicyCompanyContext>> CompanyContextAsync(FinanceScope scope, Guid? policyId, CancellationToken ct)
        => new PolicyReadGuard(db, access, session).Execute(session.Entity?.UserId ?? Guid.Empty, scope, false, async () =>
        {
            if (scope.ProjectId is null) return PolicyReadGuard.Invalid<PolicyCompanyContext>();
            var company = await Timeline(new(scope.WorkspaceId, null), ct);
            if (company is null || company.Length == 0) return PolicyReadGuard.Conflict<PolicyCompanyContext>();
            Guid? basisId;
            if (policyId is { } id)
            {
                var row = await db.Set<PolicyRevision>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == scope.WorkspaceId && x.ProjectId == scope.ProjectId, ct);
                if (row is null) return PolicyReadGuard.Denied<PolicyCompanyContext>();
                basisId = row.BaseCompanyVersionId;
            }
            else
            {
                var effective = await ResolveAuthorizedAsync(scope, clock.GetUtcNow(), ct);
                if (effective.Status != 200) return new(effective.Status, effective.Code, null);
                basisId = effective.Value!.CompanyVersionId;
            }
            var basis = company.SingleOrDefault(x => x.View.Id == basisId);
            if (basis is null) return PolicyReadGuard.Conflict<PolicyCompanyContext>();
            static PolicyCompanyRevision Minimal(Activation x) => new(x.View.Id, PolicyWireContent.From(x.View.Content), x.View.ContentHash);
            return new(200, "ok", new(Minimal(basis), Minimal(company[^1])));
        }, ct);

    public Task<FinanceResult<PolicySnapshot>> ResolveAsync(FinanceScope scope, DateTimeOffset asOf, CancellationToken ct)
        => new PolicyReadGuard(db, access, session).Execute(session.Entity?.UserId ?? Guid.Empty, scope, false,
            () => ResolveAuthorizedAsync(scope, asOf, ct), ct);

    // Only used by an already authorized reader holding the same transaction/identity lock.
    internal async Task<FinanceResult<PolicySnapshot>> ResolveAuthorizedAsync(FinanceScope scope, DateTimeOffset asOf, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Policy resolution requires an authorized transaction.");
        if (asOf > clock.GetUtcNow()) return PolicyReadGuard.Invalid<PolicySnapshot>();
        var company = await Timeline(new(scope.WorkspaceId, null), ct);
        var project = scope.ProjectId is null ? [] : await Timeline(scope, ct);
        if (company is null || project is null) return PolicyReadGuard.Conflict<PolicySnapshot>();
        var currentCompany = company.LastOrDefault(x => x.At <= asOf);
        if (currentCompany is null) return PolicyReadGuard.Conflict<PolicySnapshot>();
        var selected = project.LastOrDefault(x => x.At <= asOf);
        var basis = currentCompany;
        var content = currentCompany.View.Content;
        var hash = currentCompany.View.ContentHash;
        if (selected is not null)
        {
            var approvedBase = company.SingleOrDefault(x => x.View.Id == selected.View.BaseCompanyVersionId);
            if (approvedBase is null || approvedBase.At > selected.At) return PolicyReadGuard.Conflict<PolicySnapshot>();
            if (selected.View.Mode == "Override")
            {
                basis = approvedBase;
                content = selected.View.Content;
                hash = selected.View.ContentHash;
            }
            else if (selected.View.Mode != "Inherit") return PolicyReadGuard.Conflict<PolicySnapshot>();
        }
        return new(200, "ok", new(basis.View.Id, selected?.View.Id, PolicyService.Copy(content), hash)
        {
            Scope = scope,
            RequiresReview = selected?.View.Mode == "Override" && basis.View.Id != currentCompany.View.Id
        });
    }

    private sealed record Activation(PolicyView View, DateTimeOffset At);

    private async Task<Activation[]?> Timeline(FinanceScope scope, CancellationToken ct)
    {
        // Review timestamps are activation evidence; ActivePolicy.CreatedAtUtc only dates
        // the pointer's initial creation. Its revision/version chain disambiguates equal timestamps.
        var rows = await db.Set<PolicyRevision>().AsNoTracking()
            .Where(x => x.WorkspaceId == scope.WorkspaceId && x.ProjectId == scope.ProjectId).ToArrayAsync(ct);
        var pointer = await db.Set<ActivePolicy>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkspaceId == scope.WorkspaceId && x.ProjectId == scope.ProjectId, ct);
        var approved = await (from review in db.Set<PolicyReview>().AsNoTracking()
                              join row in db.Set<PolicyRevision>() on review.PolicyRevisionId equals row.Id
                              where row.WorkspaceId == scope.WorkspaceId && row.ProjectId == scope.ProjectId
                                  && review.WorkspaceId == scope.WorkspaceId && review.Outcome == "Approved"
                              select review).ToArrayAsync(ct);
        var activated = rows.Where(x => x.State is "Active" or "Retired" or "Approved").ToArray();
        if (pointer is null) return approved.Length == 0 && activated.Length == 0 ? [] : null;
        if (pointer.Version != approved.Length || activated.Length != approved.Length) return null;
        var byId = activated.ToDictionary(x => x.Id);
        var contents = await PolicyReadData.Contents(db, activated, ct);
        var result = new List<Activation>();
        foreach (var review in approved)
        {
            var row = byId.GetValueOrDefault(review.PolicyRevisionId);
            var view = PolicyReadData.Evidence(review, scope);
            if (row is null || view is null || row.Mode is null || row.Mode != view.Mode
                || row.BaseCompanyVersionId != view.BaseCompanyVersionId || row.SourcePolicyRevisionId != view.SourcePolicyRevisionId
                || row.ExpectedActiveRevisionId != view.ExpectedActiveRevisionId || row.ExpectedActiveVersion != view.ExpectedActiveVersion
                || row.ContentHash != view.ContentHash || row.CreatedAtUtc > review.CreatedAtUtc || review.CreatedAtUtc > clock.GetUtcNow()
                || row.Version != view.Version + (row.State == "Active" ? 1 : 2)
                || row.State is not ("Active" or "Retired")) return null;
            var content = contents[row.Id];
            if (PolicyValidation.Validate(content).Length != 0
                || row.ContentHash != PolicyService.ContentHash(scope, row.Mode, row.BaseCompanyVersionId, content)) return null;
            result.Add(new(view, review.CreatedAtUtc));
        }
        var ordered = result.OrderBy(x => x.View.ExpectedActiveVersion).ToArray();
        for (var i = 0; i < ordered.Length; i++)
        {
            var item = ordered[i];
            if (item.View.ExpectedActiveVersion != i || item.View.ExpectedActiveRevisionId != (i == 0 ? (Guid?)null : ordered[i - 1].View.Id)
                || i > 0 && item.At < ordered[i - 1].At) return null;
            if (byId[item.View.Id].State != (i == ordered.Length - 1 ? "Active" : "Retired")) return null;
        }
        return ordered.Length > 0 && ordered[^1].View.Id == pointer.PolicyRevisionId ? ordered : null;
    }
}
