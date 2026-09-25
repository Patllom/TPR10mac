using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Data;
using TPR10.Api.Scopes;

namespace TPR10.Api.Attendance.Access;

public interface IAttendanceRouteResolver
{
    Task<AttendanceRouteDecision> ResolveAsync(EmploymentSnapshot subject, CancellationToken ct);
}

public sealed class AttendanceRouteResolver(Tpr10DbContext db, ScopeAccess session, TimeProvider clock) : IAttendanceRouteResolver
{
    public async Task<AttendanceRouteDecision> ResolveAsync(EmploymentSnapshot subject, CancellationToken ct)
    {
        var identity = await session.ValidateSessionAsync(false, ct);
        if (identity.Status is { } status) return Denied(subject, status);
        var now = DirectoryQueries.Now(clock);
        if (!await AttendanceAccess.ValidSnapshotAsync(db, subject, now, ct)) return Denied(subject, 409);
        var membership = await DirectoryQueries.Employment(db, now).AsNoTracking().SingleOrDefaultAsync(x => x.UserId == subject.EmployeeId, ct);
        if (membership is null || membership.WorkspaceId != subject.WorkspaceId || membership.DepartmentId != subject.DepartmentId) return Denied(subject, 409);
        var line = await DirectoryQueries.Reporting(db, now).AsNoTracking().SingleOrDefaultAsync(x => x.EmployeeMembershipId == membership.Id, ct);
        if (line is null || line.SupervisorUserId == subject.EmployeeId
            || !await AttendanceAccess.EligibleUsers(db, "attendance:approve-supervisor").ContainsAsync(line.SupervisorUserId, ct)) return Denied(subject, 409);
        var graph = await DirectoryQueries.Reporting(db, now).AsNoTracking().ToDictionaryAsync(x => x.EmployeeUserId, x => x.SupervisorUserId, ct);
        if (DirectoryRules.CreatesCycle(subject.EmployeeId, line.SupervisorUserId, graph)) return Denied(subject, 409);
        var hr = await DirectoryQueries.Hr(db, now).Where(x => x.WorkspaceId == subject.WorkspaceId && x.DepartmentId == subject.DepartmentId
            && x.UserId != subject.EmployeeId && x.UserId != line.SupervisorUserId
            && AttendanceAccess.EligibleUsers(db, "attendance:approve-hr").Contains(x.UserId)).Select(x => x.UserId).Distinct().OrderBy(x => x).ToArrayAsync(ct);
        return hr.Length == 0 ? Denied(subject, 409) : new(line.SupervisorUserId, hr, subject.WorkspaceId, subject.DepartmentId, membership.Version, line.Version, null);
    }
    private static AttendanceRouteDecision Denied(EmploymentSnapshot subject, int status) => new(null, [], subject.WorkspaceId, subject.DepartmentId, 0, null, status);
}
