namespace TPR10.Api.Auditing;

public sealed record SecurityAuditRequest(Guid? ActorId, Guid? ActingRoleId,
    Guid? WorkspaceId, Guid? ProjectId, Guid? SiteId, string Action, string TargetType,
    Guid? TargetId, string Outcome, IReadOnlyDictionary<string, string> Metadata);
