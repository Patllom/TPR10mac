using System.Text.Json.Serialization;

namespace TPR10.Api.Attendance.Directory;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SetMembership([property: JsonRequired] Guid UserId, [property: JsonRequired] Guid WorkspaceId,
    [property: JsonRequired] Guid DepartmentId, [property: JsonRequired] long? ExpectedVersion, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SetReportingLine([property: JsonRequired] Guid EmployeeUserId, [property: JsonRequired] Guid SupervisorUserId,
    [property: JsonRequired] long? ExpectedVersion, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GrantHrAssignment([property: JsonRequired] Guid UserId, [property: JsonRequired] Guid WorkspaceId,
    [property: JsonRequired] Guid DepartmentId, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EndDirectoryRow([property: JsonRequired] long ExpectedVersion, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MembershipView([property: JsonRequired] Guid Id, [property: JsonRequired] Guid UserId,
    [property: JsonRequired] Guid WorkspaceId, [property: JsonRequired] Guid DepartmentId,
    [property: JsonRequired] DateTimeOffset ValidFromUtc, [property: JsonRequired] DateTimeOffset? ValidToUtc, [property: JsonRequired] long Version);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReportingView([property: JsonRequired] Guid Id, [property: JsonRequired] Guid EmployeeUserId,
    [property: JsonRequired] Guid SupervisorUserId, [property: JsonRequired] DateTimeOffset ValidFromUtc,
    [property: JsonRequired] DateTimeOffset? ValidToUtc, [property: JsonRequired] long Version);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HrView([property: JsonRequired] Guid Id, [property: JsonRequired] Guid UserId,
    [property: JsonRequired] Guid WorkspaceId, [property: JsonRequired] Guid DepartmentId,
    [property: JsonRequired] DateTimeOffset ValidFromUtc, [property: JsonRequired] DateTimeOffset? ValidToUtc, [property: JsonRequired] long Version);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DirectoryPage<T>([property: JsonRequired] T[] Items, [property: JsonRequired] int Page,
    [property: JsonRequired] int PageSize, [property: JsonRequired] int Total);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DirectoryOption([property: JsonRequired] Guid Id, [property: JsonRequired] string Label);
