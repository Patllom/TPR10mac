using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;

namespace TPR10.Api.FieldAdvances.Policies;

internal static class PolicyReadData
{
    internal static async Task<Dictionary<Guid, PolicyContent>> Contents(Tpr10DbContext db, PolicyRevision[] rows, CancellationToken ct)
    {
        var ids = rows.Select(x => x.Id).ToArray();
        if (ids.Length == 0) return [];
        var bands = (await db.Set<PolicyApprovalBand>().AsNoTracking().Where(x => ids.Contains(x.PolicyRevisionId))
            .OrderBy(x => x.Order).ToArrayAsync(ct)).ToLookup(x => x.PolicyRevisionId);
        var categories = (await db.Set<PolicyCategoryRule>().AsNoTracking().Where(x => ids.Contains(x.PolicyRevisionId))
            .OrderBy(x => x.Id).ToArrayAsync(ct)).ToLookup(x => x.PolicyRevisionId);
        return rows.ToDictionary(x => x.Id, x => new PolicyContent(x.ClearingDays,
            bands[x.Id].Select(b => new ApprovalBand(b.Order, b.LevelCode, b.UpperInclusive)).ToArray(),
            categories[x.Id].Select(c => new CategoryRule(c.Id, c.Name, c.Active, c.Unit, c.StandardRate, c.EvidenceTypes)).ToArray()));
    }

    internal static async Task<PolicyContent> Content(Tpr10DbContext db, PolicyRevision row, CancellationToken ct) => new(row.ClearingDays,
        await db.Set<PolicyApprovalBand>().AsNoTracking().Where(x => x.PolicyRevisionId == row.Id).OrderBy(x => x.Order)
            .Select(x => new ApprovalBand(x.Order, x.LevelCode, x.UpperInclusive)).ToArrayAsync(ct),
        await db.Set<PolicyCategoryRule>().AsNoTracking().Where(x => x.PolicyRevisionId == row.Id).OrderBy(x => x.Id)
            .Select(x => new CategoryRule(x.Id, x.Name, x.Active, x.Unit, x.StandardRate, x.EvidenceTypes)).ToArrayAsync(ct));

    internal static bool ValidMode(PolicyView view) => view.Scope.ProjectId is null
        ? view.Mode == "Company" && view.BaseCompanyVersionId is null
        : view.Mode is "Override" or "Inherit" && view.BaseCompanyVersionId is { } basis && basis != Guid.Empty;

    internal static PolicyView? Evidence(PolicyReview review, FinanceScope scope)
    {
        if (review.WorkspaceId != scope.WorkspaceId || review.ReviewedVersion is not >= 1
            || review.ReviewedContentHash is null || review.ReviewedContentJson is null) return null;
        try
        {
            var view = JsonSerializer.Deserialize<PolicyView>(review.ReviewedContentJson);
            if (view is null || view.Scope != scope || view.Id != review.PolicyRevisionId
                || view.State != "Pending" || view.Version != review.ReviewedVersion
                || view.ContentHash != review.ReviewedContentHash || !ValidMode(view)
                || PolicyValidation.Validate(view.Content).Length != 0
                || view.ContentHash != PolicyService.ContentHash(scope, view.Mode, view.BaseCompanyVersionId, view.Content)) return null;
            return view with { Content = PolicyService.Copy(view.Content) };
        }
        catch (JsonException) { return null; }
    }
}
