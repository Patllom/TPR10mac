namespace TPR10.Api.FieldAdvances.Data;

// This company-local identity is explicitly established, never derived from account or role IDs.
public sealed class FinancePerson
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public long Version { get; set; } = 1;
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public required string Reason { get; set; }
}

public sealed class FinanceAccountLink
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid UserId { get; set; }
    public Guid PersonId { get; set; }
    public long Version { get; set; } = 1;
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset? EndedAtUtc { get; set; }
    public Guid? EndedBy { get; set; }
    public string? EndReason { get; set; }
}

public sealed class PolicyRevision
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? BaseCompanyVersionId { get; set; }
    // Null is retained for foundation-era rows whose lifecycle metadata was not recorded.
    public string? Mode { get; set; }
    public Guid? SourcePolicyRevisionId { get; set; }
    public Guid? ExpectedActiveRevisionId { get; set; }
    public long ExpectedActiveVersion { get; set; }
    public required string State { get; set; }
    public long Version { get; set; } = 1;
    public int ClearingDays { get; set; }
    public required string ContentHash { get; set; }
    public Guid CreatedBy { get; set; }
    public Guid CreatedByPersonId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public required string Reason { get; set; }
}

public sealed class PolicyApprovalBand
{
    public Guid PolicyRevisionId { get; set; }
    public int Order { get; set; }
    public required string LevelCode { get; set; }
    public decimal UpperInclusive { get; set; }
}

public sealed class PolicyCategoryRule
{
    public Guid PolicyRevisionId { get; set; }
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public bool Active { get; set; }
    public string? Unit { get; set; }
    public decimal? StandardRate { get; set; }
    public required string[] EvidenceTypes { get; set; }
}

public sealed class PolicyContributor
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid PolicyRevisionId { get; set; }
    public Guid PersonId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public required string Reason { get; set; }
}

public sealed class PolicyReview
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid PolicyRevisionId { get; set; }
    public Guid ReviewerId { get; set; }
    public Guid ReviewerPersonId { get; set; }
    public required string Outcome { get; set; }
    // Historical reviews remain explicitly unknown; never infer these from the current revision.
    public long? ReviewedVersion { get; set; }
    public string? ReviewedContentHash { get; set; }
    public string? ReviewedContentJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public required string Reason { get; set; }
}

public sealed class ActivePolicy
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid PolicyRevisionId { get; set; }
    public long Version { get; set; } = 1;
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
