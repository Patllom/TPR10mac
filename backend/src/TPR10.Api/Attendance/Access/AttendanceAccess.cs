using TPR10.Api.Data;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Identity.Data;
using TPR10.Api.Scopes;

namespace TPR10.Api.Attendance.Access;

public interface IAttendanceAccess
{
    Task<EmploymentSnapshot?> CurrentEmploymentAsync(Guid employeeId, DateTimeOffset at, CancellationToken ct);
    Task<AttendanceReadDecision> ReadAsync(EmploymentSnapshot subject, CancellationToken ct);
    Task<AttendanceAccessView> DescribeAsync(CancellationToken ct);
}

public sealed class AttendanceAccess(Tpr10DbContext db, ScopeAccess session, TimeProvider clock) : IAttendanceAccess
{
    public async Task<EmploymentSnapshot?> CurrentEmploymentAsync(Guid employeeId, DateTimeOffset at, CancellationToken ct)
    {
        var identity = await session.ValidateSessionAsync(false, ct);
        if (identity.Status is not null || employeeId == Guid.Empty || at > DirectoryQueries.Now(clock)) return null;
        return await DirectoryQueries.Employment(db, at).AsNoTracking().Where(x => x.UserId == employeeId)
            .Select(x => new EmploymentSnapshot(x.Id, x.UserId, x.WorkspaceId, x.DepartmentId, at)).SingleOrDefaultAsync(ct);
    }
    public async Task<AttendanceReadDecision> ReadAsync(EmploymentSnapshot subject, CancellationToken ct)
    {
        var identity = await session.ValidateSessionAsync(false, ct);
        if (identity.Status is { } status) return Denied(status);
        var now = DirectoryQueries.Now(clock);
        if (!await ValidSnapshotAsync(db, subject, now, ct)) return Denied(404);
        if (subject.EmployeeId == identity.ActorId) return new(true, true, true, AttendanceReadBasis.Own, null);
        if (!identity.RecentMfa) return Denied(403);
        if (await EligibleUsers(db, "attendance:hr-read").ContainsAsync(identity.ActorId, ct)
            && await DirectoryQueries.Hr(db, now).AnyAsync(x => x.UserId == identity.ActorId && x.WorkspaceId == subject.WorkspaceId && x.DepartmentId == subject.DepartmentId, ct))
            return new(true, true, true, AttendanceReadBasis.Hr, null);
        if (await EligibleUsers(db, "attendance:team-read").ContainsAsync(identity.ActorId, ct)
            && await DirectoryQueries.Reporting(db, now).AnyAsync(x => x.SupervisorUserId == identity.ActorId && x.EmployeeUserId == subject.EmployeeId && x.EmployeeMembershipId == subject.MembershipId, ct))
            return new(true, true, false, AttendanceReadBasis.Supervisor, null);
        return Denied(404);
    }
    public async Task<AttendanceAccessView> DescribeAsync(CancellationToken ct)
    {
        var identity = await session.ValidateSessionAsync(false, ct);
        if (identity.Status is not null) return new(false, false, false, false, false, false);
        var actor = identity.ActorId; var now = DirectoryQueries.Now(clock);
        var employed = await DirectoryQueries.Employment(db, now).AnyAsync(x => x.UserId == actor, ct);
        var record = employed && await (from a in session.ActiveAssignments(actor)
                                        join rp in db.Set<RolePermission>() on a.RoleId equals rp.RoleId
                                        join p in db.Set<IdentityPermission>() on rp.PermissionId equals p.Id
                                        where a.ProjectId != null && a.SiteId != null && p.Domain == ScopeCatalog.BusinessDomain && p.Capability == "attendance:record"
                                        select a.Id).AnyAsync(ct);
        var team = identity.RecentMfa && await DirectoryQueries.Reporting(db, now).AnyAsync(x => x.SupervisorUserId == actor, ct);
        var hr = identity.RecentMfa && await DirectoryQueries.Hr(db, now).AnyAsync(x => x.UserId == actor, ct);
        var manage = identity.RecentMfa && await (from ur in db.Set<UserRole>()
                                                  join rp in db.Set<RolePermission>() on ur.RoleId equals rp.RoleId
                                                  join p in db.Set<IdentityPermission>() on rp.PermissionId equals p.Id
                                                  where ur.UserId == actor && p.Domain == "system" && p.Capability == DirectoryService.Capability
                                                  select p.Id).AnyAsync(ct);
        return new(record, team && await EligibleUsers(db, "attendance:team-read").ContainsAsync(actor, ct),
            hr && await EligibleUsers(db, "attendance:hr-read").ContainsAsync(actor, ct),
            team && await EligibleUsers(db, "attendance:approve-supervisor").ContainsAsync(actor, ct),
            hr && await EligibleUsers(db, "attendance:approve-hr").ContainsAsync(actor, ct), manage);
    }

    internal static Task<bool> ValidSnapshotAsync(Tpr10DbContext db, EmploymentSnapshot subject, DateTimeOffset now, CancellationToken ct) =>
        db.Set<EmployeeMembership>().AnyAsync(x => subject.OccurredAtUtc <= now && x.Id == subject.MembershipId && x.UserId == subject.EmployeeId
            && x.WorkspaceId == subject.WorkspaceId && x.DepartmentId == subject.DepartmentId && x.ValidFromUtc <= subject.OccurredAtUtc
            && (x.ValidToUtc == null || subject.OccurredAtUtc < x.ValidToUtc), ct);

    internal static IQueryable<Guid> EligibleUsers(Tpr10DbContext db, string capability) =>
        (from user in db.Set<IdentityUser>()
         join ur in db.Set<UserRole>() on user.Id equals ur.UserId
         join role in db.Set<IdentityRole>() on ur.RoleId equals role.Id
         join rp in db.Set<RolePermission>() on role.Id equals rp.RoleId
         join p in db.Set<IdentityPermission>() on rp.PermissionId equals p.Id
         where user.IsActive && (role.RoleClass == "approval" || role.RoleClass == "accounting" || role.RoleClass == "finance-data-access")
            && p.Domain == AttendanceCatalog.Domain && p.Capability == capability
         select user.Id).Distinct();

    private static AttendanceReadDecision Denied(int status) => new(false, false, false, AttendanceReadBasis.None, status);
}
