using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Payments;
using static TPR10.Api.FieldAdvances.Requests.RequestCommandExecutor;

namespace TPR10.Api.FieldAdvances.Requests;

public sealed class SupplementService(Tpr10DbContext db, RequestAccess access, RequestDraftService drafts,
    IAuditEventWriter audit, TimeProvider clock)
{
    private readonly RequestCommandExecutor executor = new(db, access, audit, clock);

    public Task<FinanceResult<RequestView>> CreateAsync(Guid actor, Guid sourceRequestId, RequestContent content,
        RequestCommand meta, CancellationToken ct) => executor.ExecuteAsync(async () =>
    {
        if (!RequestDraftService.ValidMeta(meta)) return Invalid();
        var source = await executor.LockAsync(sourceRequestId, ct);
        if (source is null) return Denied();
        var scope = new FinanceScope(source.WorkspaceId, source.ProjectId);
        var person = await drafts.AuthorizeCreateAsync(actor, scope, source.OwnerPersonId, ct);
        if (person is null || !(await access.CheckAsync(actor, sourceRequestId, "read", ct)).Allowed) return Denied();
        const string action = "supplement.create";
        var hash = Hash(new { action, actor, sourceRequestId, content, meta });
        var replay = await executor.ReplayAsync(actor, scope, action, meta, hash, ct);
        if (replay is not null) return replay;
        if (meta.ExpectedVersion != 0 || !await EligibleAsync(db, source.FamilyId, ct)) return Conflict();
        var history = await FamilyParticipation.ReadHistoryAsync(db, source.FamilyId, ct);
        // Becoming a delegate must not erase an earlier incompatible role in this family.
        if (history.ApprovedPersonIds.Contains(person.Value) || history.CheckerPersonIds.Contains(person.Value)
            || history.PayerPersonIds.Contains(person.Value)) return Denied();
        var calculated = await drafts.CalculateAsync(scope, content, ct);
        if (calculated.Value is null) return new(calculated.Status, calculated.Code, null);
        var (estimate, policy) = calculated.Value;
        var root = await db.Set<AdvanceRequest>().SingleAsync(x => x.FamilyId == source.FamilyId && x.RelationKind == "Normal", ct);
        var request = new AdvanceRequest
        {
            Id = Guid.NewGuid(),
            WorkspaceId = source.WorkspaceId,
            ProjectId = source.ProjectId,
            FamilyId = source.FamilyId,
            OwnerPersonId = source.OwnerPersonId,
            DelegatePersonId = person == source.OwnerPersonId ? null : person,
            RelationKind = "Supplement",
            SupplementOfRequestId = root.Id,
            CreatedBy = actor,
            CreatedAtUtc = clock.GetUtcNow()
        };
        var revision = new AdvanceRequestRevision
        {
            Id = Guid.NewGuid(),
            WorkspaceId = request.WorkspaceId,
            ProjectId = request.ProjectId,
            RequestId = request.Id,
            RevisionNumber = 1,
            IsCurrent = true,
            ContentJson = JsonSerializer.Serialize(content),
            ContentHash = Hash(content),
            Total = estimate.Total,
            CreatedBy = actor,
            CreatedByPersonId = person.Value,
            CreatedAtUtc = clock.GetUtcNow()
        };
        // Draft basis is provisional and does not join the approved basis or reserve the pending slot.
        revision.ApprovalBasisAmount = estimate.Total;
        revision.ApprovalBasisJson = JsonSerializer.Serialize(new ApprovalBasis(estimate.Total,
            [new(request.Id, revision.Id, estimate.Total)]));
        db.AddRange(request, revision);
        foreach (var participant in new[] { (request.OwnerPersonId, "Owner"), (person.Value, "Delegate") }.Distinct())
        {
            if (participant.Item2 == "Delegate" && request.DelegatePersonId is null) continue;
            db.Add(new AdvanceRequestParticipant
            {
                Id = Guid.NewGuid(),
                WorkspaceId = request.WorkspaceId,
                ProjectId = request.ProjectId,
                RequestId = request.Id,
                PersonId = participant.Item1,
                Role = participant.Item2,
                CreatedBy = actor,
                CreatedAtUtc = clock.GetUtcNow()
            });
        }
        drafts.AddLines(request, revision, estimate, policy);
        var family = await db.Set<AdvanceFamily>().SingleAsync(x => x.Id == source.FamilyId, ct);
        family.Version = checked(family.Version + 1);
        return await executor.FinishAsync(actor, person.Value, request, revision, action, "SupplementCreated", meta, hash,
            content, estimate, 201, async () => await drafts.AuthorizeCreateAsync(actor, scope, source.OwnerPersonId, ct) == person
                && (await access.CheckAsync(actor, sourceRequestId, "read", ct)).Allowed, ct);
    }, ct);

    internal static async Task<bool> EligibleAsync(Tpr10DbContext db, Guid familyId, CancellationToken ct)
    {
        var root = await db.Set<AdvanceRequest>().SingleOrDefaultAsync(x => x.FamilyId == familyId && x.RelationKind == "Normal", ct);
        return root is not null && root.State is not ("Rejected" or "Withdrawn" or "Closed")
            && await db.Set<AdvancePaymentAccount>().AnyAsync(x => x.RequestId == root.Id && x.ApprovedRevisionId != null
                && x.EverPaid && x.Approved > 0, ct);
    }

    internal static async Task ReleaseAsync(Tpr10DbContext db, AdvanceRequest request, CancellationToken ct)
    {
        if (request.RelationKind != "Supplement") return;
        var family = await db.Set<AdvanceFamily>().SingleAsync(x => x.Id == request.FamilyId, ct);
        if (family.PendingSupplementId == request.Id)
        {
            family.PendingSupplementId = null;
            family.Version = checked(family.Version + 1);
        }
    }
}
