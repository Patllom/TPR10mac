namespace TPR10.Api.Attendance.Access;

public sealed record EmploymentSnapshot(Guid MembershipId, Guid EmployeeId, Guid WorkspaceId, Guid DepartmentId, DateTimeOffset OccurredAtUtc);
public enum AttendanceReadBasis { None, Own, Supervisor, Hr }
public sealed record AttendanceReadDecision(bool CanRead, bool CanReadGps, bool CanReadPhoto, AttendanceReadBasis Basis, int? Status);
public sealed record AttendanceRouteDecision(Guid? SupervisorId, Guid[] HrCandidateIds, Guid WorkspaceId, Guid DepartmentId, long MembershipVersion, long? ReportingVersion, int? Status);
public sealed record AttendanceAccessView(bool CanRecord, bool CanReadTeam, bool CanReadHr, bool CanApproveSupervisor, bool CanApproveHr, bool CanManageDirectory);
