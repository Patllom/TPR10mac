namespace TPR10.Api.Attendance.Corrections;

public sealed class CorrectionRequest
{
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    public DateOnly OriginalWorkDate { get; set; }
    public Guid? DayId { get; set; }
    public Guid MembershipId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid DepartmentId { get; set; }
    public DateTimeOffset SnapshotAtUtc { get; set; }
    public DateTimeOffset DeadlineAnchorUtc { get; set; }
    public DateTimeOffset DeadlineUtc { get; set; }
    public CorrectionState State { get; set; } = CorrectionState.Draft;
    public int CurrentRevision { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class CorrectionRevision
{
    public Guid RequestId { get; set; }
    public int Revision { get; set; }
    public Guid? RecoveryDayId { get; set; }
    public long? BaselineDayVersion { get; set; }
    public DateOnly WorkDate { get; set; }
    public DateTimeOffset InUtc { get; set; }
    public DateTimeOffset? OutUtc { get; set; }
    public Guid InWorkspaceId { get; set; }
    public Guid InProjectId { get; set; }
    public Guid InSiteId { get; set; }
    public Guid? OutWorkspaceId { get; set; }
    public Guid? OutProjectId { get; set; }
    public Guid? OutSiteId { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class CorrectionRouteSnapshot
{
    public Guid RequestId { get; set; }
    public int Revision { get; set; }
    public Guid SupervisorId { get; set; }
    public Guid[] HrCandidateIds { get; set; } = [];
    public long MembershipVersion { get; set; }
    public long ReportingVersion { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class CorrectionApprovalAction
{
    public Guid RequestId { get; set; }
    public int Revision { get; set; }
    public string Step { get; set; } = "";
    public Guid ActorId { get; set; }
    public string Outcome { get; set; } = "";
    public string? Reason { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

public sealed class AttendanceAdjustment
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public int Revision { get; set; }
    public Guid DayId { get; set; }
    public long? BeforeVersion { get; set; }
    public DateOnly? BeforeWorkDate { get; set; }
    public DateTimeOffset? BeforeInUtc { get; set; }
    public DateTimeOffset? BeforeOutUtc { get; set; }
    public long AfterVersion { get; set; }
    public DateOnly AfterWorkDate { get; set; }
    public DateTimeOffset AfterInUtc { get; set; }
    public DateTimeOffset? AfterOutUtc { get; set; }
    public DateTimeOffset AppliedAtUtc { get; set; }
}

public sealed class CorrectionCommandReceipt
{
    public Guid CommandId { get; set; }
    public Guid ActorId { get; set; }
    public Guid RequestId { get; set; }
    public string Action { get; set; } = "";
    public string PayloadSha256 { get; set; } = "";
    public int Status { get; set; }
    public string ResultJson { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
}
