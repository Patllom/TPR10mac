using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Access;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Attendance.Recording;
using TPR10.Api.Data;

namespace TPR10.Api.Attendance.History;

internal static class AttendanceEvidenceSubject
{
    // Both photos inherit the immutable check-in snapshot, never the checkout's employment or Site.
    internal static Task<EmploymentSnapshot?> ResolveAsync(Tpr10DbContext db, Guid evidenceId, CancellationToken ct) =>
        (from binding in db.Set<EvidenceBinding>().AsNoTracking()
         join evidence in db.Set<EvidenceObject>() on binding.EvidenceId equals evidence.Id
         join attendance in db.Set<AttendanceEvent>() on binding.EventId equals attendance.Id
         join day in db.Set<AttendanceDay>() on attendance.DayId equals day.Id
         where binding.EvidenceId == evidenceId && evidence.State == EvidenceState.Published
             && attendance.EvidenceId == evidence.Id && attendance.ActorId == evidence.OwnerId
             && attendance.Action == evidence.Action && attendance.OccurredAtUtc == evidence.OccurredAtUtc
             && attendance.ActorId == day.EmployeeId
         select new EmploymentSnapshot(day.MembershipId, day.EmployeeId, day.WorkspaceId,
             day.DepartmentId, day.SnapshotAtUtc)).SingleOrDefaultAsync(ct);
}
