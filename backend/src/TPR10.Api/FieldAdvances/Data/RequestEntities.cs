namespace TPR10.Api.FieldAdvances.Data;

public sealed class AdvanceRequest
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid OwnerPersonId { get; set; }
    public Guid? DelegatePersonId { get; set; }
    public Guid? SourceRequestId { get; set; }
    public Guid FamilyId { get; set; }
    public string RelationKind { get; set; } = "Normal";
    public Guid? SupplementOfRequestId { get; set; }
    public string State { get; set; } = "Draft";
    public string? HoldCode { get; set; }
    public long Version { get; set; } = 1;
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class AdvanceRequestRevision
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid RequestId { get; set; }
    public int RevisionNumber { get; set; }
    public bool IsCurrent { get; set; }
    public required string ContentJson { get; set; }
    public required string ContentHash { get; set; }
    public decimal Total { get; set; }
    public decimal ApprovalBasisAmount { get; set; }
    public string ApprovalBasisJson { get; set; } = "{}";
    public Guid? CompanyPolicyRevisionId { get; set; }
    public Guid? ProjectPolicyRevisionId { get; set; }
    public string? PolicySnapshotJson { get; set; }
    public string? PolicyHash { get; set; }
    public DateTimeOffset? SubmittedAtUtc { get; set; }
    public Guid? ConfirmedByPersonId { get; set; }
    public Guid CreatedBy { get; set; }
    public Guid CreatedByPersonId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class AdvanceRequestLine
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid RequestId { get; set; }
    public Guid RevisionId { get; set; }
    public int LineOrder { get; set; }
    public Guid CategoryId { get; set; }
    public required string CategoryName { get; set; }
    public required string Description { get; set; }
    public required string Mode { get; set; }
    public string? Unit { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? UnitRate { get; set; }
    public decimal Amount { get; set; }
}

public sealed class AdvanceApprovalStep
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid RequestId { get; set; }
    public Guid RevisionId { get; set; }
    public int StepOrder { get; set; }
    public required string LevelCode { get; set; }
    // Mutable routing is separate from the frozen role/content/policy snapshot.
    public Guid? AssigneePersonId { get; set; }
    public string? HoldCode { get; set; }
    public string? AssignmentReason { get; set; }
    public string? Outcome { get; set; }
    public Guid? DecidedByPersonId { get; set; }
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAtUtc { get; set; }
    public string? DecisionReason { get; set; }
}

public sealed class AdvanceRequestEvent
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid RequestId { get; set; }
    public Guid RevisionId { get; set; }
    public Guid? StepId { get; set; }
    public Guid CommandId { get; set; }
    public Guid ActorId { get; set; }
    public Guid ActorPersonId { get; set; }
    public required string Kind { get; set; }
    public required string DataJson { get; set; }
    public required string Reason { get; set; }
    public long AggregateVersion { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class AdvanceRequestParticipant
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid RequestId { get; set; }
    public Guid PersonId { get; set; }
    public required string Role { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
