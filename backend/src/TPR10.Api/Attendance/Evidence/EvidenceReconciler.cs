using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.Scopes;

namespace TPR10.Api.Attendance.Evidence;

// Report only. Neither grace-period expiry nor a missing binding authorizes file deletion.
public sealed class EvidenceReconciler(Tpr10DbContext db, TimeProvider clock)
{
    public async Task<int> ReportAsync(CancellationToken ct)
    {
        await using var tx = await ScopeOperation.BeginAsync(db, ct);
        var now = clock.GetUtcNow();
        return await db.Set<EvidenceObject>().AsNoTracking().CountAsync(x => x.CreatedAtUtc <= now.AddHours(-24)
            && (x.LeaseUntilUtc == null || x.LeaseUntilUtc <= now)
            && x.State != EvidenceState.Published && !db.Set<EvidenceBinding>().Any(b => b.EvidenceId == x.Id), ct);
    }
}
