namespace TPR10.Api.Attendance.Storage;

public sealed class StorageLocation
{
    public Guid Id { get; set; }
    public string Alias { get; set; } = "";
    public string Kind { get; set; } = "local-folder";
    public string ConfigFingerprint { get; set; } = "";
    public long Version { get; set; } = 1;
    public bool AcceptWrites { get; set; }
    public string Health { get; set; } = "unknown";
    public DateTimeOffset? CheckedAtUtc { get; set; }
    public long? FreeBytes { get; set; }
    public long? TotalBytes { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class StorageWriteTarget
{
    public int Id { get; set; } = 1;
    public Guid? StorageId { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class MigrationJob
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public Guid SourceId { get; set; }
    public Guid TargetId { get; set; }
    public long SourceVersion { get; set; }
    public long TargetVersion { get; set; }
    public Guid RequestedBy { get; set; }
    public string Reason { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public long Version { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class MigrationItem
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public Guid EvidenceId { get; set; }
    public string Variant { get; set; } = "full";
    public string ExpectedSha256 { get; set; } = "";
    public long ExpectedLength { get; set; }
    public Guid SourceId { get; set; }
    public Guid TargetId { get; set; }
    public string Status { get; set; } = "Pending";
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAtUtc { get; set; }
    public Guid? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public long FencingVersion { get; set; } = 1;
    public long Version { get; set; } = 1;
    public string? ErrorCode { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
