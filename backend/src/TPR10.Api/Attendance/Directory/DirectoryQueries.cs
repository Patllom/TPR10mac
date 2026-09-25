using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.Identity.Data;
using TPR10.Api.Organization.Data;

namespace TPR10.Api.Attendance.Directory;

internal static class DirectoryQueries
{
    internal static DateTimeOffset Now(TimeProvider clock)
    {
        var ticks = clock.GetUtcNow().UtcTicks;
        return new DateTimeOffset(ticks - ticks % 10, TimeSpan.Zero);
    }

    internal static IQueryable<EmployeeMembership> Employment(Tpr10DbContext db, DateTimeOffset at) =>
        db.Set<EmployeeMembership>().Where(x => x.ValidFromUtc <= at && (x.ValidToUtc == null || at < x.ValidToUtc)
            && db.Set<IdentityUser>().Any(u => u.Id == x.UserId && u.IsActive)
            && db.Set<Workspace>().Any(w => w.Id == x.WorkspaceId && w.IsActive)
            && db.Set<Department>().Any(d => d.Id == x.DepartmentId && d.WorkspaceId == x.WorkspaceId && d.IsActive));

    internal static IQueryable<ReportingLine> Reporting(Tpr10DbContext db, DateTimeOffset at) =>
        from line in db.Set<ReportingLine>()
        join employee in Employment(db, at) on line.EmployeeMembershipId equals employee.Id
        join manager in Employment(db, at) on line.SupervisorUserId equals manager.UserId
        where line.EmployeeUserId == employee.UserId && employee.WorkspaceId == manager.WorkspaceId
            && line.ValidFromUtc <= at && (line.ValidToUtc == null || at < line.ValidToUtc)
        select line;

    internal static IQueryable<HrAssignment> Hr(Tpr10DbContext db, DateTimeOffset at) =>
        db.Set<HrAssignment>().Where(x => x.ValidFromUtc <= at && (x.ValidToUtc == null || at < x.ValidToUtc)
            && db.Set<IdentityUser>().Any(u => u.Id == x.UserId && u.IsActive)
            && db.Set<Workspace>().Any(w => w.Id == x.WorkspaceId && w.IsActive)
            && db.Set<Department>().Any(d => d.Id == x.DepartmentId && d.WorkspaceId == x.WorkspaceId && d.IsActive));
}
