namespace TPR10.Api.Scopes.Data;

public sealed class ScopeAssignment
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? SiteId { get; set; }
    public Guid RoleId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid CreatedBy { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public Guid? RevokedBy { get; set; }
    public string? RevocationReason { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class ScopeProbeRecord
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? SiteId { get; set; }
    public required string Note { get; set; }
    public string? RestrictedNote { get; set; }
    public long Version { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
}
