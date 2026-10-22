using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.Identity.Sessions;

namespace TPR10.Api.FieldAdvances.Policies;

public sealed class PolicyReadService(Tpr10DbContext db, FinanceAccess access, RequestSession session, TimeProvider clock, PolicyResolver resolver)
{
    public Task<FinanceResult<PolicyView[]>> ListAsync(Guid actorId, FinanceScope scope, int page, int pageSize, CancellationToken ct)
        => Read(actorId, scope, false, () => List(scope, page, pageSize, false, ct), ct);
    public Task<FinanceResult<PolicyView>> DetailsAsync(Guid actorId, FinanceScope scope, Guid id, CancellationToken ct)
        => Read(actorId, scope, false, async () =>
        {
            var row = await Scoped(scope).SingleOrDefaultAsync(x => x.Id == id, ct);
            return row is null ? PolicyReadGuard.Denied<PolicyView>() : await View(row, await Pointer(scope, ct), ct);
        }, ct);
    public Task<FinanceResult<PolicyHistoryView[]>> HistoryAsync(Guid actorId, FinanceScope scope, Guid id, int page, int pageSize, CancellationToken ct)
        => Read(actorId, scope, false, async () =>
        {
            if (!ValidPage(page, pageSize)) return PolicyReadGuard.Invalid<PolicyHistoryView[]>();
            if (!await Scoped(scope).AnyAsync(x => x.Id == id, ct)) return PolicyReadGuard.Denied<PolicyHistoryView[]>();
            var rows = await db.Set<PolicyReview>().AsNoTracking().Where(x => x.WorkspaceId == scope.WorkspaceId && x.PolicyRevisionId == id)
                .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.ReviewedVersion).ThenBy(x => x.Id)
                .Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(ct);
            var result = new List<PolicyHistoryView>();
            foreach (var row in rows)
            {
                var unknown = row.ReviewedVersion is null && row.ReviewedContentHash is null && row.ReviewedContentJson is null;
                var evidence = PolicyReadData.Evidence(row, scope);
                if (!unknown && evidence is null) return PolicyReadGuard.Conflict<PolicyHistoryView[]>();
                result.Add(new(row.Id, id, row.Outcome, row.CreatedAtUtc, row.ReviewedVersion, row.ReviewedContentHash, evidence?.Content)
                { ReviewerId = row.ReviewerId, Reason = row.Reason });
            }
            return new(200, "ok", result.ToArray());
        }, ct);
    public Task<FinanceResult<PolicyView[]>> ReviewQueueAsync(Guid actorId, FinanceScope scope, int page, int pageSize, CancellationToken ct)
        => Read(actorId, scope, true, () => List(scope, page, pageSize, true, ct), ct);
    public Task<FinanceResult<string[]>> PreviewAsync(Guid actorId, FinanceScope scope, decimal total, CancellationToken ct)
        => Read(actorId, scope, false, async () =>
        {
            var snapshot = await resolver.ResolveAuthorizedAsync(scope, clock.GetUtcNow(), ct);
            return snapshot.Status == 200 ? ApprovalLevelEvaluator.Evaluate(total, snapshot.Value!)
                : new(snapshot.Status, snapshot.Code, null);
        }, ct);

    private Task<FinanceResult<T>> Read<T>(Guid actor, FinanceScope scope, bool queue, Func<Task<FinanceResult<T>>> action, CancellationToken ct)
        => new PolicyReadGuard(db, access, session).Execute(actor, scope, queue, action, ct);
    private IQueryable<PolicyRevision> Scoped(FinanceScope scope) => db.Set<PolicyRevision>().AsNoTracking()
        .Where(x => x.WorkspaceId == scope.WorkspaceId && x.ProjectId == scope.ProjectId);
    private Task<ActivePolicy?> Pointer(FinanceScope scope, CancellationToken ct) => db.Set<ActivePolicy>().AsNoTracking()
        .SingleOrDefaultAsync(x => x.WorkspaceId == scope.WorkspaceId && x.ProjectId == scope.ProjectId, ct);
    private static bool ValidPage(int page, int size) => page >= 1 && size is >= 1 and <= 100 && (long)(page - 1) * size <= int.MaxValue;

    private async Task<FinanceResult<PolicyView[]>> List(FinanceScope scope, int page, int size, bool queue, CancellationToken ct)
    {
        if (!ValidPage(page, size)) return PolicyReadGuard.Invalid<PolicyView[]>();
        var rows = await Scoped(scope).Where(x => !queue || x.State == "Pending")
            .OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id).Skip((page - 1) * size).Take(size).ToArrayAsync(ct);
        var pointer = await Pointer(scope, ct);
        var contents = await PolicyReadData.Contents(db, rows, ct);
        var values = new List<PolicyView>();
        foreach (var row in rows)
        {
            var view = View(row, pointer, contents[row.Id]);
            if (view.Status != 200) return new(view.Status, view.Code, null);
            values.Add(view.Value!);
        }
        return new(200, "ok", values.ToArray());
    }

    private async Task<FinanceResult<PolicyView>> View(PolicyRevision row, ActivePolicy? pointer, CancellationToken ct)
        => View(row, pointer, await PolicyReadData.Content(db, row, ct));

    private static FinanceResult<PolicyView> View(PolicyRevision row, ActivePolicy? pointer, PolicyContent content)
    {
        // Unknown legacy provenance cannot silently become Company/Override.
        if (row.Mode is null || PolicyValidation.Validate(content).Length != 0
            || row.ContentHash != PolicyService.ContentHash(new(row.WorkspaceId, row.ProjectId), row.Mode, row.BaseCompanyVersionId, content))
            return PolicyReadGuard.Conflict<PolicyView>();
        return new(200, "ok", new(row.Id, new(row.WorkspaceId, row.ProjectId), row.State, row.Version,
            row.BaseCompanyVersionId, PolicyService.Copy(content), row.ContentHash)
        {
            Mode = row.Mode,
            SourcePolicyRevisionId = row.SourcePolicyRevisionId,
            ExpectedActiveRevisionId = row.ExpectedActiveRevisionId,
            ExpectedActiveVersion = row.ExpectedActiveVersion,
            ActiveRevisionId = pointer?.PolicyRevisionId,
            ActiveVersion = pointer?.Version ?? 0
        });
    }
}
