using System.Text.Json.Serialization;

namespace TPR10.Api.Scopes.Assignments;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GrantAssignment([property: JsonRequired] Guid UserId, [property: JsonRequired] ScopeKey Scope,
    [property: JsonRequired] Guid RoleId, [property: JsonRequired] string Reason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReplaceAssignment([property: JsonRequired] ScopeKey Scope, [property: JsonRequired] Guid RoleId,
    [property: JsonRequired] long ExpectedVersion, [property: JsonRequired] string Reason);

public sealed record AssignmentView(Guid Id, Guid UserId, ScopeKey Scope, Guid RoleId, long Version, DateTimeOffset? RevokedAtUtc);
public sealed record AssignmentUserOption(Guid Id, string Username, bool IsActive);
public sealed record AssignmentRoleOption(Guid Id, string Name, string RoleClass, string[] BusinessCapabilities);
