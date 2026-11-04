using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
namespace TPR10.Api.FieldAdvances.Settlements;

internal static class SettlementPaymentProof
{
    internal static Task LockAsync(Tpr10DbContext db, CancellationToken ct)
        => db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241006)", ct);
    internal static async Task<bool> DuplicateAsync(Tpr10DbContext db, Guid workspace, RefundEvidenceSnapshot[] evidence, Guid? excluded, CancellationToken ct)
    {
        // Every claim/reservation/release holds the shared gate before family/request locks.
        var cases = await db.Set<SettlementCasePayment>().AsNoTracking().Where(x => x.WorkspaceId == workspace && x.Id != excluded
            && (x.AcceptedAtUtc != null || x.State == "Pending" || x.State == "Returned" || x.State == "Draft")).ToArrayAsync(ct);
        var excess = await db.Set<SettlementExcessPayment>().AsNoTracking().Where(x => x.WorkspaceId == workspace && x.Id != excluded
            && (x.AcceptedAtUtc != null || x.State == "Pending" || x.State == "Returned" || x.State == "Draft")).ToArrayAsync(ct);
        var claims = cases.Select(x => AdjustmentCaseService.ReadPayment(x)?.Evidence)
            .Concat(excess.Select(x => ExpenseExcessService.ReadPayment(x)?.Evidence));
        return claims.Any(claim => claim is null || claim.Any(old => evidence.Any(current => current.Id == old.Id || current.Sha256 == old.Sha256)));
    }
}
