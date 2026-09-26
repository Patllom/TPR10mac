using TPR10.Api.Attendance.Evidence;

namespace TPR10.Api.Attendance.Recording;

public sealed class AttendanceDay
{
    public Guid Id { get; set; }
    public Guid EmployeeId { get; set; }
    public DateOnly WorkDate { get; set; }
    public Guid MembershipId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid DepartmentId { get; set; }
    public DateTimeOffset SnapshotAtUtc { get; set; }
    public DateTimeOffset EffectiveInUtc { get; set; }
    public DateTimeOffset? EffectiveOutUtc { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class AttendanceEvent
{
    public Guid Id { get; set; }
    public Guid DayId { get; set; }
    public Guid ActorId { get; set; }
    public EvidenceAction Action { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset DeviceCapturedAtUtc { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid SiteId { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double AccuracyMeters { get; set; }
    public DateTimeOffset GpsSampledAtUtc { get; set; }
    public Guid EvidenceId { get; set; }
    public string Source { get; set; } = "live";
}

public sealed class CaptureChallenge
{
    public Guid Id { get; set; }
    public Guid ActorId { get; set; }
    public Guid SessionId { get; set; }
    public EvidenceAction Action { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid SiteId { get; set; }
    public Guid IdempotencyKey { get; set; }
    public DateTimeOffset IssuedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public Guid? ConsumedOperationId { get; set; }
}

public enum AttendanceOperationState { Processing, Committed, Expired, Failed }

public sealed class AttendanceOperation
{
    public Guid Id { get; set; }
    public Guid ChallengeId { get; set; }
    public Guid ActorId { get; set; }
    public Guid SessionId { get; set; }
    public EvidenceAction Action { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid SiteId { get; set; }
    public Guid IdempotencyKey { get; set; }
    public string PayloadSha256 { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; }
    public Guid MembershipId { get; set; }
    public Guid MembershipWorkspaceId { get; set; }
    public Guid DepartmentId { get; set; }
    // Check-in allocates its day ID before the day exists; only the committed result has an event FK.
    public Guid TargetDayId { get; set; }
    public long? ExpectedDayVersion { get; set; }
    public AttendanceOperationState State { get; set; } = AttendanceOperationState.Processing;
    public DateTimeOffset LeaseUntilUtc { get; set; }
    public long FencingVersion { get; set; } = 1;
    public long Version { get; set; } = 1;
    public Guid? ResultEventId { get; set; }
    public string? ResultJson { get; set; }
}
