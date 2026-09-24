using System.Text.Json.Serialization;

namespace TPR10.Api.Organization;

public enum OrganizationKind { Workspace, Department, Project, Site }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateOrganization([property: JsonRequired] string Code, [property: JsonRequired] string Name);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateOrganization([property: JsonRequired] string Name, [property: JsonRequired] bool IsActive,
    [property: JsonRequired] long ExpectedVersion, [property: JsonRequired] string Reason);

public sealed record OrganizationView(Guid Id, Guid WorkspaceId, Guid? ProjectId,
    string Code, string Name, bool IsActive, long Version);
public sealed record OrganizationPage(OrganizationView[] Items, int Total, int Page, int PageSize);
