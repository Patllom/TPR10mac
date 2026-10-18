using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;

namespace TPR10.Api.Attendance.Corrections;

// Called inside the shared identity/employee transaction. A new source date cannot
// hide an unresolved request for the affected date, including its retained revisions.
internal sealed class CorrectionTargetGuard(Tpr10DbContext db, TimeProvider clock)
{
    public async Task<int?> ValidateAsync(Guid employee, Guid? requestId, DateOnly original, DateOnly target,
        bool filing, CancellationToken ct)
    {
        if (await db.Set<CorrectionRequest>().AnyAsync(r => r.EmployeeId == employee && r.Id != requestId
            && r.State != CorrectionState.Approved && (r.OriginalWorkDate == target
                || db.Set<CorrectionRevision>().Any(v => v.RequestId == r.Id && v.WorkDate == target)), ct)) return 409;
        // Waiting for approval does not expire a timely submission. A returned revision
        // retains its new window only for dates already submitted on this same request.
        if (filing && target != original && clock.GetUtcNow() >= CorrectionDeadline.First(target)
            && !await db.Set<CorrectionRevision>().AnyAsync(v => v.RequestId == requestId && v.WorkDate == target
                && db.Set<CorrectionRouteSnapshot>().Any(s => s.RequestId == v.RequestId && s.Revision == v.Revision), ct)) return 400;
        return null;
    }
}
