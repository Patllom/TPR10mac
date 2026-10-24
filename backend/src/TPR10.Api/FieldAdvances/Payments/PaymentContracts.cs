namespace TPR10.Api.FieldAdvances.Payments;

public sealed record PaymentCommand(Guid CommandId, long FamilyVersion, long RequestVersion, long? InstallmentVersion, string Reason);
public sealed record PaymentDestination(string Channel, Guid RecipientPersonId, string? BankCode, string? AccountNumber, string? AccountHolder);
public sealed record InstallmentInput(string Amount, PaymentDestination Destination);
public sealed record ActualPaymentInput(string Amount, Guid RecipientPersonId, DateOnly PaidOn, string? Reference, Guid[] EvidenceIds);
public sealed record PaymentMetadataInput(DateOnly PaidOn, string? Reference, Guid[] EvidenceIds);
public sealed record PaymentOutcomeInput(string Outcome, ActualPaymentInput? Actual, Guid[] EvidenceIds);
public sealed record PaymentView(Guid RequestId, Guid? InstallmentId, long FamilyVersion, long RequestVersion,
    long? InstallmentVersion, string State, decimal Approved, decimal Spendable, decimal Reserved, decimal Paid, decimal Available, string[] HoldCodes);
public sealed record ApprovalBasis(decimal Amount, ApprovalBasisItem[] Items);
public sealed record ApprovalBasisItem(Guid RequestId, Guid RevisionId, decimal ApprovedAmount);
public sealed record PaymentAccessDecision(bool Allowed, Guid? PersonId);
public sealed record EvidenceTicket(Guid Id, string Sha256, long Length, string ContentType, string State);
public sealed record BalanceSnapshot(decimal Approved, decimal Spendable, decimal Reserved, decimal Paid);
