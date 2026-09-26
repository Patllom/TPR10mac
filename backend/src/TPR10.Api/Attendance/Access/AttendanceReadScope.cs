using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Attendance.History;
using TPR10.Api.Attendance.Recording;
using TPR10.Api.Data;
using TPR10.Api.Scopes;

namespace TPR10.Api.Attendance.Access;

internal sealed class AttendanceReadDeniedException(int status) : Exception("attendance-read-denied")
{
    internal int Status { get; } = status;
}

public sealed class AttendanceReadScope(Tpr10DbContext db, ScopeAccess access, TimeProvider clock)
{
    // The caller must enumerate inside its identity transaction, not return this query to HTTP.
    public async Task<IQueryable<AttendanceDay>> VisibleDaysAsync(AttendanceAudience audience, CancellationToken ct)
    {
        if (!Enum.IsDefined(audience)) throw new AttendanceReadDeniedException(400);
        var identity = await access.ValidateSessionAsync(audience != AttendanceAudience.Mine, ct);
        if (identity.Status is { } status) throw new AttendanceReadDeniedException(status);
        var days = ValidDays(DirectoryQueries.Now(clock));
        if (audience == AttendanceAudience.Mine) return days.Where(d => d.EmployeeId == identity.ActorId);
        var capability = audience == AttendanceAudience.Team ? "attendance:team-read" : "attendance:hr-read";
        if (!await AttendanceAccess.EligibleUsers(db, capability).ContainsAsync(identity.ActorId, ct)) throw new AttendanceReadDeniedException(403);
        var memberships = audience == AttendanceAudience.Team
            ? AttendanceAccess.TeamMemberships(db, identity.ActorId, DirectoryQueries.Now(clock))
            : AttendanceAccess.HrMemberships(db, identity.ActorId, DirectoryQueries.Now(clock));
        return days.Where(d => memberships.Any(m => m.Id == d.MembershipId));
    }

    internal async Task<IQueryable<AttendanceDay>> ReadableDaysAsync(CancellationToken ct)
    {
        var identity = await access.ValidateSessionAsync(false, ct);
        if (identity.Status is { } status) throw new AttendanceReadDeniedException(status);
        var now = DirectoryQueries.Now(clock);
        var hr = AttendanceAccess.HrMemberships(db, identity.ActorId, now);
        var team = AttendanceAccess.TeamMemberships(db, identity.ActorId, now);
        return ValidDays(now).Where(d => d.EmployeeId == identity.ActorId || identity.RecentMfa
            && (hr.Any(m => m.Id == d.MembershipId) || team.Any(m => m.Id == d.MembershipId)));
    }

    private IQueryable<AttendanceDay> ValidDays(DateTimeOffset now) =>
        db.Set<AttendanceDay>().AsNoTracking().Where(d => d.SnapshotAtUtc <= now && db.Set<EmployeeMembership>().Any(m =>
            m.Id == d.MembershipId && m.UserId == d.EmployeeId && m.WorkspaceId == d.WorkspaceId && m.DepartmentId == d.DepartmentId
            && m.ValidFromUtc <= d.SnapshotAtUtc && (m.ValidToUtc == null || d.SnapshotAtUtc < m.ValidToUtc)));
}
