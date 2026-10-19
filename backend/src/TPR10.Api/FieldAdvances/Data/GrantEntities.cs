namespace TPR10.Api.FieldAdvances.Data;

public sealed class GrantProposal
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid RecipientId { get; set; }
    public Guid RecipientPersonId { get; set; }
    public Guid ProposedBy { get; set; }
    public Guid ProposedByPersonId { get; set; }
    public required string State { get; set; }
    public long Version { get; set; } = 1;
    public required string[] Capabilities { get; set; }
    public required string[] DelegableCapabilities { get; set; }
    public decimal? ApprovalLimit { get; set; }
    public DateTimeOffset StartsAtUtc { get; set; }
    public DateTimeOffset? EndsAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public required string Reason { get; set; }
}

public sealed class GrantReview
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProposalId { get; set; }
    public Guid ReviewerId { get; set; }
    public Guid ReviewerPersonId { get; set; }
    public required string Outcome { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public required string Reason { get; set; }
}

// The approved proposal is the retained grant specification; revocation does not delete it.
public sealed class GrantAssignment
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid ProposalId { get; set; }
    public Guid ReviewId { get; set; }
    public long Version { get; set; } = 1;
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public Guid? RevokedBy { get; set; }
    public string? RevocationReason { get; set; }
}

public sealed class FinanceCommandReceipt
{
    public Guid CommandId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ActorId { get; set; }
    public required string Action { get; set; }
    public required string PayloadSha256 { get; set; }
    public int Status { get; set; }
    public required string ResultJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class FinanceBootstrapReceipt
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid CommandId { get; set; }
    public Guid GrantAssignmentId { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public required string Reason { get; set; }
}
