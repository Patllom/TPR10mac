using Microsoft.EntityFrameworkCore;
using TPR10.Api.Identity.Data;
using TPR10.Api.Organization.Data;

namespace TPR10.Api.Attendance.Directory;

public sealed partial class DirectoryService
{
    public Task<IResult> ListMembershipsAsync(Guid? userId, Guid? workspaceId, Guid? departmentId, bool includeEnded, int page, int pageSize, CancellationToken ct) =>
        RunAsync("membership-list", null, async () =>
        {
            if (!Filters(userId, workspaceId, departmentId)) return Reject(400);
            var now = DirectoryQueries.Now(clock);
            var query = db.Set<EmployeeMembership>().AsNoTracking().Where(x => (userId == null || x.UserId == userId)
                && (workspaceId == null || x.WorkspaceId == workspaceId) && (departmentId == null || x.DepartmentId == departmentId)
                && (includeEnded || x.ValidFromUtc <= now && (x.ValidToUtc == null || now < x.ValidToUtc)));
            return await PageAsync(query.OrderBy(x => x.Id).Select(x => new MembershipView(x.Id, x.UserId, x.WorkspaceId, x.DepartmentId, x.ValidFromUtc, x.ValidToUtc, x.Version)), page, pageSize, ct);
        }, ct);

    public Task<IResult> ListReportingAsync(Guid? employeeUserId, bool includeEnded, int page, int pageSize, CancellationToken ct) =>
        RunAsync("reporting-list", null, async () =>
        {
            if (!Filters(employeeUserId)) return Reject(400);
            var now = DirectoryQueries.Now(clock);
            var query = db.Set<ReportingLine>().AsNoTracking().Where(x => (employeeUserId == null || x.EmployeeUserId == employeeUserId)
                && (includeEnded || x.ValidFromUtc <= now && (x.ValidToUtc == null || now < x.ValidToUtc)));
            return await PageAsync(query.OrderBy(x => x.Id).Select(x => new ReportingView(x.Id, x.EmployeeUserId, x.SupervisorUserId, x.ValidFromUtc, x.ValidToUtc, x.Version)), page, pageSize, ct);
        }, ct);

    public Task<IResult> ListHrAsync(Guid? userId, Guid? workspaceId, Guid? departmentId, bool includeEnded, int page, int pageSize, CancellationToken ct) =>
        RunAsync("hr-list", null, async () =>
        {
            if (!Filters(userId, workspaceId, departmentId)) return Reject(400);
            var now = DirectoryQueries.Now(clock);
            var query = db.Set<HrAssignment>().AsNoTracking().Where(x => (userId == null || x.UserId == userId)
                && (workspaceId == null || x.WorkspaceId == workspaceId) && (departmentId == null || x.DepartmentId == departmentId)
                && (includeEnded || x.ValidFromUtc <= now && (x.ValidToUtc == null || now < x.ValidToUtc)));
            return await PageAsync(query.OrderBy(x => x.Id).Select(x => new HrView(x.Id, x.UserId, x.WorkspaceId, x.DepartmentId, x.ValidFromUtc, x.ValidToUtc, x.Version)), page, pageSize, ct);
        }, ct);

    public Task<IResult> UserOptionsAsync(string prefix, int page, int pageSize, CancellationToken ct) => RunAsync("user-options", null, async () =>
        !Prefix(prefix) ? Reject(400) : await PageAsync(db.Set<IdentityUser>().AsNoTracking().Where(x => x.IsActive && x.Username.StartsWith(prefix))
            .OrderBy(x => x.Username).ThenBy(x => x.Id).Select(x => new DirectoryOption(x.Id, x.Username)), page, pageSize, ct), ct);

    public Task<IResult> WorkspaceOptionsAsync(string prefix, int page, int pageSize, CancellationToken ct) => RunAsync("workspace-options", null, async () =>
        !Prefix(prefix) ? Reject(400) : await PageAsync(db.Set<Workspace>().AsNoTracking().Where(x => x.IsActive && x.Name.StartsWith(prefix))
            .OrderBy(x => x.Name).ThenBy(x => x.Id).Select(x => new DirectoryOption(x.Id, x.Name)), page, pageSize, ct), ct);

    public Task<IResult> DepartmentOptionsAsync(Guid workspaceId, string prefix, int page, int pageSize, CancellationToken ct) => RunAsync("department-options", null, async () =>
    {
        if (workspaceId == Guid.Empty || !Prefix(prefix)) return Reject(400);
        if (!await db.Set<Workspace>().AnyAsync(x => x.Id == workspaceId && x.IsActive, ct)) return Reject(404);
        return await PageAsync(db.Set<Department>().AsNoTracking().Where(x => x.WorkspaceId == workspaceId && x.IsActive && x.Name.StartsWith(prefix))
            .OrderBy(x => x.Name).ThenBy(x => x.Id).Select(x => new DirectoryOption(x.Id, x.Name)), page, pageSize, ct);
    }, ct);

    private static async Task<Change> PageAsync<T>(IQueryable<T> query, int page, int pageSize, CancellationToken ct)
    {
        pageSize = Math.Min(pageSize, 100);
        var skip = ((long)page - 1) * pageSize;
        if (page < 1 || pageSize < 1 || skip > int.MaxValue) return Reject(400);
        var total = await query.CountAsync(ct);
        var items = await query.Skip((int)skip).Take(pageSize).ToArrayAsync(ct);
        return new(Results.Ok(new DirectoryPage<T>(items, page, pageSize, total)));
    }
    private static bool Filters(params Guid?[] values) => values.All(x => x != Guid.Empty);
    private static bool Prefix(string? value)
    {
        if (value is null || value.Any(char.IsControl)) return false;
        var remaining = value.AsSpan();
        var count = 0;
        while (!remaining.IsEmpty)
        {
            if (System.Text.Rune.DecodeFromUtf16(remaining, out _, out var consumed) != System.Buffers.OperationStatus.Done || ++count > 100) return false;
            remaining = remaining[consumed..];
        }
        return true;
    }
}
