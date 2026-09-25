using System.Text.Json.Serialization;

namespace TPR10.Api.Attendance.Storage;

public sealed record StoredCopy(string Sha256, long Length);
// Deployment configuration only. Never return this type from an HTTP endpoint.
public sealed record StorageDefinition(string Alias, string Kind, string RootPath, string ExpectedVolumeId, Guid MarkerId);
public sealed record StorageView(Guid Id, string Alias, string Kind, long Version, bool AcceptWrites, string Health, DateTimeOffset? CheckedAtUtc);
public sealed record StorageTargetView(Guid? StorageId, long Version);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegisterStorage([property: JsonRequired] string Alias, [property: JsonRequired] string Reason);
public sealed record StorageOptionView(string Alias, string Kind);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProbeStorage([property: JsonRequired] long ExpectedVersion, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SwitchWriteTarget([property: JsonRequired] Guid StorageId, [property: JsonRequired] long ExpectedVersion, [property: JsonRequired] string Reason);
public sealed record StartMigration(Guid RequestId, Guid SourceId, Guid TargetId, long ExpectedSourceVersion, long ExpectedTargetVersion, string Reason);
public sealed record ResumeMigration(long ExpectedVersion, string Reason);
public sealed record MigrationView(Guid Id, string Status, long Version, int Total, int Verified, int Blocked);
public sealed record StorageHealthView(Guid StorageId, string Status, long? FreeBytes, long? TotalBytes, int MissingObjects, int OrphanObjects, DateTimeOffset CheckedAtUtc, string? ErrorCode = null);
public sealed record Page<T>(T[] Items, int Offset, bool HasMore);
