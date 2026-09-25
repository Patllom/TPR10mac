namespace TPR10.Api.Attendance.Evidence;

public sealed class EvidenceObject
{
    public Guid Id { get; set; }
    public Guid OperationId { get; set; }
    public Guid OwnerId { get; set; }
    public Guid MembershipId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid DepartmentId { get; set; }
    public DateTimeOffset SnapshotAtUtc { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public EvidenceAction Action { get; set; }
    public Guid StorageId { get; set; }
    public long StorageVersion { get; set; }
    public string ObjectKey { get; set; } = "";
    public string? InputSha256 { get; set; }
    public string? Sha256 { get; set; }
    public long? Length { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? ThumbnailSha256 { get; set; }
    public long? ThumbnailLength { get; set; }
    public int? ThumbnailWidth { get; set; }
    public int? ThumbnailHeight { get; set; }
    public EvidenceState State { get; set; } = EvidenceState.Reserved;
    public long Version { get; set; } = 1;
    public long FencingVersion { get; set; } = 1;
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class EvidenceLocation
{
    public Guid Id { get; set; }
    public Guid EvidenceId { get; set; }
    public Guid StorageId { get; set; }
    public string ObjectKey { get; set; } = "";
    public string Variant { get; set; } = "full";
    public string Sha256 { get; set; } = "";
    public long Length { get; set; }
    public CopyState State { get; set; } = CopyState.Pending;
    public DateTimeOffset? VerifiedAtUtc { get; set; }
    public long Version { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

// AttendanceEvent is introduced in 6C; its migration must add the event FK.
public sealed class EvidenceBinding
{
    public Guid EvidenceId { get; set; }
    public Guid EventId { get; set; }
    public DateTimeOffset PublishedAtUtc { get; set; }
}
