using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Payments;

namespace TPR10.Api.FieldAdvances.Requests;

public sealed class ApprovalBasisCalculator(Tpr10DbContext db)
{
    // Internal calculation, not an authorization boundary. The caller retains the family lock.
    public async Task<ApprovalBasis> CalculateAsync(Guid requestId, decimal requested, CancellationToken ct)
    {
        var request = await FamilyParticipation.LockRequestAsync(db, requestId, ct)
            ?? throw new InvalidOperationException("ไม่พบชุดคำขอ");
        var revision = await db.Set<AdvanceRequestRevision>().SingleAsync(x => x.RequestId == requestId && x.IsCurrent, ct);
        var items = new List<ApprovalBasisItem>();
        if (request.RelationKind == "Supplement")
        {
            // The approved account points to the spendable frozen revision, never an unapproved draft.
            // Approved stays historical even when Spendable is terminated or payments are made.
            var approved = await (from a in db.Set<AdvancePaymentAccount>().AsNoTracking()
                                  join r in db.Set<AdvanceRequestRevision>().AsNoTracking() on a.ApprovedRevisionId equals r.Id
                                  where a.FamilyId == request.FamilyId && a.RequestId != requestId
                                      && a.WorkspaceId == request.WorkspaceId && a.ProjectId == request.ProjectId
                                      && r.RequestId == a.RequestId && a.Approved > 0 && r.Total == a.Approved
                                      && r.SubmittedAtUtc != null
                                  orderby a.RequestId
                                  select new ApprovalBasisItem(a.RequestId, r.Id, a.Approved)).ToArrayAsync(ct);
            items.AddRange(approved);
        }
        items.Add(new(requestId, revision.Id, requested));
        var amount = items.Aggregate(0m, (total, item) => checked(total + item.ApprovedAmount));
        // Matches the existing database money domain; no additional business ceiling.
        if (requested <= 0 || amount > 9999999999999999.99m || decimal.Round(amount, 2) != amount)
            throw new OverflowException("ยอดฐานอำนาจเกินขอบเขตข้อมูล");
        return new(amount, items.ToArray());
    }
}
