using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Access;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Data;
using TPR10.Api.Identity.Authorization;

namespace TPR10.Api.Attendance.Corrections;

// The caller holds the identity lock throughout authorization and the routing write.
public sealed class CorrectionRouting(Tpr10DbContext db, PermissionMutationGuard guard,
    IAttendanceRouteResolver resolver, TimeProvider clock)
{
    public async Task<bool> CanRerouteAsync(Guid actor, CorrectionRequest request, CancellationToken ct) =>
        await guard.AllowsAsync(actor, DirectoryService.Capability, ct)
        && await DirectoryQueries.Hr(db, DirectoryQueries.Now(clock)).AnyAsync(x => x.UserId == actor
            && x.WorkspaceId == request.WorkspaceId && x.DepartmentId == request.DepartmentId, ct);

    public Task<AttendanceRouteDecision> ResolveAsync(CorrectionRequest request, CancellationToken ct) =>
        resolver.ResolveAsync(new(request.MembershipId, request.EmployeeId, request.WorkspaceId,
            request.DepartmentId, request.SnapshotAtUtc), ct);
}
