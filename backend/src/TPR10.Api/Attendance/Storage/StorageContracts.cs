namespace TPR10.Api.Attendance.Storage;

public sealed record StoredCopy(string Sha256, long Length);
// Deployment configuration only. Never return this type from an HTTP endpoint.
public sealed record StorageDefinition(string Alias, string Kind, string RootPath, string ExpectedVolumeId, Guid MarkerId);
public sealed record StorageView(Guid Id, string Alias, string Kind, long Version, bool AcceptWrites, string Health, DateTimeOffset? CheckedAtUtc);
public sealed record StorageTargetView(Guid? StorageId, long Version);
public sealed record RegisterStorage(string Alias, string Reason);
public sealed record StorageOptionView(string Alias, string Kind);
public sealed record ProbeStorage(long ExpectedVersion, string Reason);
public sealed record SwitchWriteTarget(Guid StorageId, long ExpectedVersion, string Reason);
public sealed record StartMigration(Guid RequestId, Guid SourceId, Guid TargetId, long ExpectedSourceVersion, long ExpectedTargetVersion, string Reason);
public sealed record ResumeMigration(long ExpectedVersion, string Reason);
public sealed record MigrationView(Guid Id, string Status, long Version, int Total, int Verified, int Blocked);
public sealed record StorageHealthView(Guid StorageId, string Status, long? FreeBytes, long? TotalBytes, int MissingObjects, int OrphanObjects, DateTimeOffset CheckedAtUtc);
public sealed record Page<T>(T[] Items, int Offset, bool HasMore);
