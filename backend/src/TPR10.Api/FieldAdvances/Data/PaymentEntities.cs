namespace TPR10.Api.FieldAdvances.Data;

public sealed class AdvanceFamily
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid OwnerPersonId { get; set; }
    public long Version { get; set; } = 1;
    public Guid? PendingSupplementId { get; set; }
}

public sealed class AdvancePaymentAccount
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid RequestId { get; set; }
    public Guid? ApprovedRevisionId { get; set; }
    public decimal Approved { get; set; }
    public decimal Spendable { get; set; }
    public decimal Reserved { get; set; }
    public decimal Paid { get; set; }
    public bool EverPaid { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class AdvanceInstallment
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid RequestId { get; set; }
    public string State { get; set; } = "Draft";
    public long Version { get; set; } = 1;
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class AdvanceInstallmentRevision
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid RequestId { get; set; }
    public Guid InstallmentId { get; set; }
    public int RevisionNumber { get; set; }
    public bool IsCurrent { get; set; }
    public decimal Amount { get; set; }
    public required string Channel { get; set; }
    public Guid RecipientPersonId { get; set; }
    public string? BankCode { get; set; }
    public string? AccountNumber { get; set; }
    public string? AccountHolder { get; set; }
    public required string ContentHash { get; set; }
    public Guid? ReviewedByPersonId { get; set; }
    public Guid? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public required string Reason { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class AdvanceReservationEntry
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid RequestId { get; set; }
    public Guid InstallmentId { get; set; }
    public Guid CommandId { get; set; }
    public required string Action { get; set; }
    public decimal Amount { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class AdvancePaymentAttempt
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid RequestId { get; set; }
    public Guid InstallmentId { get; set; }
    public Guid RevisionId { get; set; }
    public Guid CommandId { get; set; }
    public Guid PayerPersonId { get; set; }
    public Guid PayerId { get; set; }
    public string State { get; set; } = "Open";
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class AdvancePaymentResult
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid RequestId { get; set; }
    public Guid InstallmentId { get; set; }
    public Guid AttemptId { get; set; }
    public Guid CommandId { get; set; }
    public decimal Amount { get; set; }
    public Guid RecipientPersonId { get; set; }
    public DateOnly PaidOn { get; set; }
    public string? Reference { get; set; }
    public string EvidenceIdsJson { get; set; } = "[]";
    public Guid RecordedBy { get; set; }
    public Guid RecordedByPersonId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class AdvancePaymentProposal
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid RequestId { get; set; }
    public Guid? InstallmentId { get; set; }
    public Guid CommandId { get; set; }
    public required string Kind { get; set; }
    public string State { get; set; } = "Pending";
    public long FamilyVersion { get; set; }
    public long RequestVersion { get; set; }
    public long? InstallmentVersion { get; set; }
    public required string ContentHash { get; set; }
    public required string BeforeJson { get; set; }
    public required string AfterJson { get; set; }
    public string EvidenceIdsJson { get; set; } = "[]";
    public Guid ProposedBy { get; set; }
    public Guid ProposedByPersonId { get; set; }
    public Guid? ReviewedBy { get; set; }
    public Guid? ReviewedByPersonId { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class AdvancePaymentParticipation
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid PersonId { get; set; }
    public required string Role { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class AdvancePaymentHold
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid? RequestId { get; set; }
    public Guid? InstallmentId { get; set; }
    public required string Code { get; set; }
    public required string Reason { get; set; }
    public Guid CreatedBy { get; set; }
    public Guid CreatedByPersonId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? ReleasedByPersonId { get; set; }
    public DateTimeOffset? ReleasedAtUtc { get; set; }
}

public sealed class AdvancePaymentEvent
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid RequestId { get; set; }
    public Guid? InstallmentId { get; set; }
    public Guid CommandId { get; set; }
    public Guid ActorId { get; set; }
    public Guid ActorPersonId { get; set; }
    public required string Kind { get; set; }
    public required string DataJson { get; set; }
    public required string Reason { get; set; }
    public long FamilyVersion { get; set; }
    public long RequestVersion { get; set; }
    public long? InstallmentVersion { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class AdvanceEvidence
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid RequestId { get; set; }
    public Guid? InstallmentId { get; set; }
    public required string Sha256 { get; set; }
    public long Length { get; set; }
    public required string ContentType { get; set; }
    public string State { get; set; } = "Pending";
    public Guid CreatedBy { get; set; }
    public Guid CreatedByPersonId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class AdvanceEvidenceLocation
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FamilyId { get; set; }
    public Guid RequestId { get; set; }
    public Guid EvidenceId { get; set; }
    public required string StorageKey { get; set; }
    public bool IsCurrent { get; set; }
    public DateTimeOffset? VerifiedAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
