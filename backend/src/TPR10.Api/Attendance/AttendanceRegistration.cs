using TPR10.Api.Attendance.Directory;
using TPR10.Api.Attendance.Access;
using TPR10.Api.Data;
using TPR10.Api.Scopes;
using TPR10.Api.Auditing;

namespace TPR10.Api.Attendance;

public static class AttendanceRegistration
{
    public static IServiceCollection AddAttendance(this IServiceCollection services)
    {
        services.AddScoped<DirectoryService>();
        services.AddScoped<Access.IAttendanceAccess, Access.AttendanceAccess>();
        services.AddScoped<Access.IAttendanceRouteResolver, Access.AttendanceRouteResolver>();
        return services;
    }

    public static IEndpointRouteBuilder MapAttendanceAccess(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/attendance/access", DescribeAsync).RequireAuthorization()
            .Produces<AttendanceAccessView>().WithMetadata(new DirectoryEndpointMetadata("attendance-access", "", false));
        return endpoints;
    }

    private static async Task<IResult> DescribeAsync(Tpr10DbContext db, ScopeAccess session, IAttendanceAccess access, IAuditEventWriter audit, CancellationToken ct)
    {
        try
        {
            await using var tx = await ScopeOperation.BeginAsync(db, ct);
            var identity = await session.ValidateSessionAsync(false, ct);
            var result = identity.Status is { } status ? ScopeOperation.Problem(status) : Results.Ok(await access.DescribeAsync(ct));
            await audit.WriteAsync(new SecurityAuditRequest(identity.ActorId == Guid.Empty ? null : identity.ActorId, null, null, null, null,
                "attendance.access.describe", "attendance-access", null, identity.Status is null ? "success" : "denied", new Dictionary<string, string>()), ct);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return result;
        }
        catch (Exception error) when (ScopeOperation.IsDatabaseFault(error)) { db.ChangeTracker.Clear(); return DirectoryService.Problem(503); }
    }
}
