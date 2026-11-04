namespace TPR10.Api.FieldAdvances.Data;

// Each row retains its scope and command provenance; accepted facts are protected in PostgreSQL.

public sealed class SettlementRound
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid RequestId { get; set; }
    public long Version { get; set; } = 1;
    public Guid CommandId { get; set; }
    public Guid ProposedBy { get; set; }
    public Guid ProposedByPersonId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? ReviewedBy { get; set; }
    public Guid? ReviewedByPersonId { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public DateTimeOffset? AcceptedAtUtc { get; set; }
    public string State { get; set; } = "Draft";
    public string Reason { get; set; } = "";
    public string SnapshotJson { get; set; } = "{}";
    public Guid? SourceRoundId { get; set; }
    public string RoundKind { get; set; } = "Ordinary";
    public Guid? AdjustmentCaseId { get; set; }
}

public sealed class SettlementRevision
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid RequestId { get; set; }
    public long Version { get; set; } = 1;
    public Guid CommandId { get; set; }
    public Guid ProposedBy { get; set; }
    public Guid ProposedByPersonId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? ReviewedBy { get; set; }
    public Guid? ReviewedByPersonId { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public DateTimeOffset? AcceptedAtUtc { get; set; }
    public string State { get; set; } = "Draft";
    public string Reason { get; set; } = "";
    public string SnapshotJson { get; set; } = "{}";
    public Guid RoundId { get; set; }
    public int RevisionNumber { get; set; }
    public Guid? SourceRevisionId { get; set; }
    public Guid? ConfirmedBy { get; set; }
    public Guid? ConfirmedByPersonId { get; set; }
    public DateTimeOffset? ConfirmedAtUtc { get; set; }
    public DateTimeOffset? SubmittedAtUtc { get; set; }
    public DateOnly? CorrectionDueOn { get; set; }
    public decimal Total { get; set; }
}

public sealed class SettlementLine
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid RequestId { get; set; }
    public long Version { get; set; } = 1;
    public Guid CommandId { get; set; }
    public Guid ProposedBy { get; set; }
    public Guid ProposedByPersonId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? ReviewedBy { get; set; }
    public Guid? ReviewedByPersonId { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public DateTimeOffset? AcceptedAtUtc { get; set; }
    public string State { get; set; } = "Draft";
    public string Reason { get; set; } = "";
    public string SnapshotJson { get; set; } = "{}";
    public Guid RevisionId { get; set; }
    public Guid? SourceLineId { get; set; }
    public Guid CategoryId { get; set; }
    public string Description { get; set; } = "";
    public DateOnly IncurredOn { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? Rate { get; set; }
    public decimal? LumpSum { get; set; }
    public decimal Amount { get; set; }
}

public sealed class SettlementApprovalStep
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid RequestId { get; set; }
    public long Version { get; set; } = 1;
    public Guid CommandId { get; set; }
    public Guid ProposedBy { get; set; }
    public Guid ProposedByPersonId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? ReviewedBy { get; set; }
    public Guid? ReviewedByPersonId { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public DateTimeOffset? AcceptedAtUtc { get; set; }
    public string State { get; set; } = "Draft";
    public string Reason { get; set; } = "";
    public string SnapshotJson { get; set; } = "{}";
    public Guid RevisionId { get; set; }
    public int StepOrder { get; set; }
    public string LevelCode { get; set; } = "";
    public Guid? AssigneePersonId { get; set; }
}

public sealed class ExpenseDocument
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid RequestId { get; set; }
    public long Version { get; set; } = 1;
    public Guid CommandId { get; set; }
    public Guid ProposedBy { get; set; }
    public Guid ProposedByPersonId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? ReviewedBy { get; set; }
    public Guid? ReviewedByPersonId { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public DateTimeOffset? AcceptedAtUtc { get; set; }
    public string State { get; set; } = "Draft";
    public string Reason { get; set; } = "";
    public string SnapshotJson { get; set; } = "{}";
    public Guid EvidenceId { get; set; }
    public string Issuer { get; set; } = "";
    public string? DocumentNumber { get; set; }
    public bool HasNoNumber { get; set; }
    public DateOnly IssuedOn { get; set; }
    public decimal Amount { get; set; }
    public string Fingerprint { get; set; } = "";
}

public sealed class ExpenseAllocation
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid RequestId { get; set; }
    public long Version { get; set; } = 1;
    public Guid CommandId { get; set; }
    public Guid ProposedBy { get; set; }
    public Guid ProposedByPersonId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? ReviewedBy { get; set; }
    public Guid? ReviewedByPersonId { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public DateTimeOffset? AcceptedAtUtc { get; set; }
    public string State { get; set; } = "Draft";
    public string Reason { get; set; } = "";
    public string SnapshotJson { get; set; } = "{}";
    public Guid DocumentId { get; set; }
    public Guid LineId { get; set; }
    public Guid RevisionId { get; set; }
    public decimal Amount { get; set; }
}

public sealed class ExpenseDuplicateCase
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid RequestId { get; set; }
    public long Version { get; set; } = 1;
    public Guid CommandId { get; set; }
    public Guid ProposedBy { get; set; }
    public Guid ProposedByPersonId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? ReviewedBy { get; set; }
    public Guid? ReviewedByPersonId { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public DateTimeOffset? AcceptedAtUtc { get; set; }
    public string State { get; set; } = "Draft";
    public string Reason { get; set; } = "";
    public string SnapshotJson { get; set; } = "{}";
    public Guid DocumentId { get; set; }
    public Guid OtherWorkspaceId { get; set; }
    public Guid OtherProjectId { get; set; }
    public Guid OtherFamilyId { get; set; }
    public Guid OtherRequestId { get; set; }
    public Guid OtherDocumentId { get; set; }
    public long DocumentVersion { get; set; }
    public long OtherDocumentVersion { get; set; }
}

public sealed class WorkCompletion
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid RequestId { get; set; }
    public long Version { get; set; } = 1;
    public Guid CommandId { get; set; }
    public Guid ProposedBy { get; set; }
    public Guid ProposedByPersonId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? ReviewedBy { get; set; }
    public Guid? ReviewedByPersonId { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public DateTimeOffset? AcceptedAtUtc { get; set; }
    public string State { get; set; } = "Draft";
    public string Reason { get; set; } = "";
    public string SnapshotJson { get; set; } = "{}";
    public DateOnly CompletedOn { get; set; }
    public DateOnly ClearingDueOn { get; set; }
    public DateOnly RefundDueOn { get; set; }
    public int ClearingDays { get; set; }
    public Guid? SupersedesId { get; set; }
}

public sealed class SubmissionDeclaration
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid RequestId { get; set; }
    public long Version { get; set; } = 1;
    public Guid CommandId { get; set; }
    public Guid ProposedBy { get; set; }
    public Guid ProposedByPersonId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? ReviewedBy { get; set; }
    public Guid? ReviewedByPersonId { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public DateTimeOffset? AcceptedAtUtc { get; set; }
    public string State { get; set; } = "Draft";
    public string Reason { get; set; } = "";
    public string SnapshotJson { get; set; } = "{}";
    public Guid WorkCompletionId { get; set; }
    public Guid? SupersedesId { get; set; }
    public bool IsWithdrawal { get; set; }
}

public sealed class Refund
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid RequestId { get; set; }
    public long Version { get; set; } = 1;
    public Guid CommandId { get; set; }
    public Guid ProposedBy { get; set; }
    public Guid ProposedByPersonId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? ReviewedBy { get; set; }
    public Guid? ReviewedByPersonId { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public DateTimeOffset? AcceptedAtUtc { get; set; }
    public string State { get; set; } = "Draft";
    public string Reason { get; set; } = "";
    public string SnapshotJson { get; set; } = "{}";
    public decimal Amount { get; set; }
    public DateOnly RefundedOn { get; set; }
    public string Channel { get; set; } = "";
    public Guid EvidenceId { get; set; }
    public string DestinationSnapshotJson { get; set; } = "{}";
    public Guid? AdjustmentCaseId { get; set; }
}

public sealed class RefundCorrection
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid RequestId { get; set; }
    public long Version { get; set; } = 1;
    public Guid CommandId { get; set; }
    public Guid ProposedBy { get; set; }
    public Guid ProposedByPersonId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? ReviewedBy { get; set; }
    public Guid? ReviewedByPersonId { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public DateTimeOffset? AcceptedAtUtc { get; set; }
    public string State { get; set; } = "Draft";
    public string Reason { get; set; } = "";
    public string SnapshotJson { get; set; } = "{}";
    public Guid RefundId { get; set; }
    public Guid? SupersedesId { get; set; }
    public long BaseVersion { get; set; }
    public decimal BeforeAmount { get; set; }
    public decimal AfterAmount { get; set; }
    public decimal Delta { get; set; }
    public Guid EvidenceId { get; set; }
    public Guid? AdjustmentCaseId { get; set; }
}

public sealed class SettlementExpenseAdjustment
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid RequestId { get; set; }
    public long Version { get; set; } = 1;
    public Guid CommandId { get; set; }
    public Guid ProposedBy { get; set; }
    public Guid ProposedByPersonId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? ReviewedBy { get; set; }
    public Guid? ReviewedByPersonId { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public DateTimeOffset? AcceptedAtUtc { get; set; }
    public string State { get; set; } = "Draft";
    public string Reason { get; set; } = "";
    public string SnapshotJson { get; set; } = "{}";
    public Guid? AdjustmentCaseId { get; set; }
    public Guid SourceRevisionId { get; set; }
    public Guid RevisionId { get; set; }
    public long BaseVersion { get; set; }
    public decimal BeforeAmount { get; set; }
    public decimal AfterAmount { get; set; }
    public decimal Delta { get; set; }
}

public sealed class SettlementAdjustmentCase
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid RequestId { get; set; }
    public long Version { get; set; } = 1;
    public Guid CommandId { get; set; }
    public Guid ProposedBy { get; set; }
    public Guid ProposedByPersonId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? ReviewedBy { get; set; }
    public Guid? ReviewedByPersonId { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public DateTimeOffset? AcceptedAtUtc { get; set; }
    public string State { get; set; } = "Draft";
    public string Reason { get; set; } = "";
    public string SnapshotJson { get; set; } = "{}";
    public Guid ClosureRecordId { get; set; }
    public Guid? RootRefundId { get; set; }
    public Guid? RootExpenseRoundId { get; set; }
    public long? BaselineVersion { get; set; }
    public decimal? BaselineAmount { get; set; }
    public string Kind { get; set; } = "";
    public decimal ExternalPaidAmount { get; set; }
    public Guid? ExternalEvidenceId { get; set; }
}

public sealed class PaymentIncidentResolution
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid RequestId { get; set; }
    public long Version { get; set; } = 1;
    public Guid CommandId { get; set; }
    public Guid ProposedBy { get; set; }
    public Guid ProposedByPersonId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? ReviewedBy { get; set; }
    public Guid? ReviewedByPersonId { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public DateTimeOffset? AcceptedAtUtc { get; set; }
    public string State { get; set; } = "Draft";
    public string Reason { get; set; } = "";
    public string SnapshotJson { get; set; } = "{}";
    public Guid InstallmentId { get; set; }
    public Guid AttemptId { get; set; }
    public Guid HoldId { get; set; }
    public decimal OwnerReceived { get; set; }
    public decimal WrongRecipientReceived { get; set; }
    public Guid? WrongRecipientPersonId { get; set; }
    public Guid EvidenceId { get; set; }
}

public sealed class PaymentIncidentRecovery
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid RequestId { get; set; }
    public long Version { get; set; } = 1;
    public Guid CommandId { get; set; }
    public Guid ProposedBy { get; set; }
    public Guid ProposedByPersonId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? ReviewedBy { get; set; }
    public Guid? ReviewedByPersonId { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public DateTimeOffset? AcceptedAtUtc { get; set; }
    public string State { get; set; } = "Draft";
    public string Reason { get; set; } = "";
    public string SnapshotJson { get; set; } = "{}";
    public Guid ResolutionId { get; set; }
    public Guid ReturnedByPersonId { get; set; }
    public string ReturnKind { get; set; } = "";
    public decimal Amount { get; set; }
    public DateOnly ReturnedOn { get; set; }
    public Guid EvidenceId { get; set; }
}

public sealed class ClosureRecord
{
    public string ActorKind { get; set; } = "Human";
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid RequestId { get; set; }
    public long Version { get; set; } = 1;
    public Guid? CommandId { get; set; }
    public Guid? ProposedBy { get; set; }
    public Guid? ProposedByPersonId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? ReviewedBy { get; set; }
    public Guid? ReviewedByPersonId { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public DateTimeOffset? AcceptedAtUtc { get; set; }
    public string State { get; set; } = "Draft";
    public string Reason { get; set; } = "";
    public string SnapshotJson { get; set; } = "{}";
    public long RequestVersion { get; set; }
    public long FamilyVersion { get; set; }
    public Guid WorkCompletionId { get; set; }
    public Guid DeclarationId { get; set; }
}

public sealed class SettlementCasePayment
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid RequestId { get; set; }
    public long Version { get; set; } = 1;
    public Guid CommandId { get; set; }
    public Guid ProposedBy { get; set; }
    public Guid ProposedByPersonId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? ReviewedBy { get; set; }
    public Guid? ReviewedByPersonId { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public DateTimeOffset? AcceptedAtUtc { get; set; }
    public string State { get; set; } = "Draft";
    public string Reason { get; set; } = "";
    public string SnapshotJson { get; set; } = "{}";
    public Guid CaseId { get; set; }
    public Guid? SupersedesId { get; set; }
    public decimal Amount { get; set; }
    public DateOnly PaidOn { get; set; }
    public Guid EvidenceId { get; set; }
}

public sealed class SettlementCaseClosure
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid RequestId { get; set; }
    public long Version { get; set; } = 1;
    public Guid CommandId { get; set; }
    public Guid ProposedBy { get; set; }
    public Guid ProposedByPersonId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? ReviewedBy { get; set; }
    public Guid? ReviewedByPersonId { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public DateTimeOffset? AcceptedAtUtc { get; set; }
    public string State { get; set; } = "Draft";
    public string Reason { get; set; } = "";
    public string SnapshotJson { get; set; } = "{}";
    public Guid CaseId { get; set; }
    public decimal Outstanding { get; set; }
}

public sealed class SettlementExcessCase
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid RequestId { get; set; }
    public long Version { get; set; } = 1;
    public Guid CommandId { get; set; }
    public Guid ProposedBy { get; set; }
    public Guid ProposedByPersonId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? ReviewedBy { get; set; }
    public Guid? ReviewedByPersonId { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public DateTimeOffset? AcceptedAtUtc { get; set; }
    public string State { get; set; } = "Draft";
    public string Reason { get; set; } = "";
    public string SnapshotJson { get; set; } = "{}";
    public Guid DocumentId { get; set; }
    public Guid SourceRevisionId { get; set; }
    public decimal Amount { get; set; }
    public decimal ClaimedAmount { get; set; }
    public decimal CapAmount { get; set; }
}

public sealed class SettlementExcessPayment
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid RequestId { get; set; }
    public long Version { get; set; } = 1;
    public Guid CommandId { get; set; }
    public Guid ProposedBy { get; set; }
    public Guid ProposedByPersonId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? ReviewedBy { get; set; }
    public Guid? ReviewedByPersonId { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public DateTimeOffset? AcceptedAtUtc { get; set; }
    public string State { get; set; } = "Draft";
    public string Reason { get; set; } = "";
    public string SnapshotJson { get; set; } = "{}";
    public Guid ExcessId { get; set; }
    public Guid RecipientPersonId { get; set; }
    public Guid? SupersedesId { get; set; }
    public decimal Amount { get; set; }
    public DateOnly PaidOn { get; set; }
    public Guid EvidenceId { get; set; }
}

public sealed class SettlementExcessDisposition
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid RequestId { get; set; }
    public long Version { get; set; } = 1;
    public Guid CommandId { get; set; }
    public Guid ProposedBy { get; set; }
    public Guid ProposedByPersonId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? ReviewedBy { get; set; }
    public Guid? ReviewedByPersonId { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public DateTimeOffset? AcceptedAtUtc { get; set; }
    public string State { get; set; } = "Draft";
    public string Reason { get; set; } = "";
    public string SnapshotJson { get; set; } = "{}";
    public Guid ExcessId { get; set; }
    public string Code { get; set; } = "";
    public Guid EvidenceId { get; set; }
}
