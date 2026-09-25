namespace TPR10.Api.Attendance.Directory;

public sealed record DirectoryEndpointMetadata(string Domain = "attendance-directory", string Capability = DirectoryService.Capability, bool RequireMfa = true);

public static class DirectoryEndpoints
{
    public static IEndpointRouteBuilder MapAttendanceDirectory(this IEndpointRouteBuilder endpoints)
    {
        var g = endpoints.MapGroup("/api/v1/attendance/directory").RequireAuthorization(DirectoryService.Capability)
            .WithMetadata(new DirectoryBindingBoundary(), new DirectoryEndpointMetadata());
        g.AddEndpointFilter(async (context, next) =>
        {
            if (context.HttpContext.Response.StatusCode == 400) return Results.Empty;
            context.HttpContext.Items[typeof(DirectoryBindingBoundary)] = true;
            return await next(context);
        });
        g.MapGet("/memberships", (DirectoryService s, Guid? userId, Guid? workspaceId, Guid? departmentId, bool? includeEnded, int? page, int? pageSize, CancellationToken ct) =>
            s.ListMembershipsAsync(userId, workspaceId, departmentId, includeEnded ?? false, page ?? 1, pageSize ?? 25, ct)).Produces<DirectoryPage<MembershipView>>();
        g.MapPost("/memberships", (DirectoryService s, SetMembership request, CancellationToken ct) => s.SetMembershipAsync(request, ct)).Produces<MembershipView>().Produces<MembershipView>(201);
        g.MapPost("/memberships/{id}/end", (DirectoryService s, Guid id, EndDirectoryRow request, CancellationToken ct) => s.EndMembershipAsync(id, request, ct)).Produces(204);
        g.MapGet("/reporting-lines", (DirectoryService s, Guid? employeeUserId, bool? includeEnded, int? page, int? pageSize, CancellationToken ct) =>
            s.ListReportingAsync(employeeUserId, includeEnded ?? false, page ?? 1, pageSize ?? 25, ct)).Produces<DirectoryPage<ReportingView>>();
        g.MapPost("/reporting-lines", (DirectoryService s, SetReportingLine request, CancellationToken ct) => s.SetReportingAsync(request, ct)).Produces<ReportingView>().Produces<ReportingView>(201);
        g.MapPost("/reporting-lines/{id}/end", (DirectoryService s, Guid id, EndDirectoryRow request, CancellationToken ct) => s.EndReportingAsync(id, request, ct)).Produces(204);
        g.MapGet("/hr-assignments", (DirectoryService s, Guid? userId, Guid? workspaceId, Guid? departmentId, bool? includeEnded, int? page, int? pageSize, CancellationToken ct) =>
            s.ListHrAsync(userId, workspaceId, departmentId, includeEnded ?? false, page ?? 1, pageSize ?? 25, ct)).Produces<DirectoryPage<HrView>>();
        g.MapPost("/hr-assignments", (DirectoryService s, GrantHrAssignment request, CancellationToken ct) => s.GrantHrAsync(request, ct)).Produces<HrView>(201);
        g.MapPost("/hr-assignments/{id}/end", (DirectoryService s, Guid id, EndDirectoryRow request, CancellationToken ct) => s.EndHrAsync(id, request, ct)).Produces(204);
        g.MapGet("/options/users", (DirectoryService s, string? prefix, int? page, int? pageSize, CancellationToken ct) => s.UserOptionsAsync(prefix ?? "", page ?? 1, pageSize ?? 25, ct)).Produces<DirectoryPage<DirectoryOption>>();
        g.MapGet("/options/workspaces", (DirectoryService s, string? prefix, int? page, int? pageSize, CancellationToken ct) => s.WorkspaceOptionsAsync(prefix ?? "", page ?? 1, pageSize ?? 25, ct)).Produces<DirectoryPage<DirectoryOption>>();
        g.MapGet("/options/departments", (DirectoryService s, Guid workspaceId, string? prefix, int? page, int? pageSize, CancellationToken ct) => s.DepartmentOptionsAsync(workspaceId, prefix ?? "", page ?? 1, pageSize ?? 25, ct)).Produces<DirectoryPage<DirectoryOption>>();
        return endpoints;
    }
}
