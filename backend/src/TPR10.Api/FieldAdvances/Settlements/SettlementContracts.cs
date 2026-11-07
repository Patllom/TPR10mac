namespace TPR10.Api.FieldAdvances.Settlements;

public sealed record SettlementCommand(Guid CommandId, long RequestVersion, long FamilyVersion,
    long? EntityVersion, string Reason)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ConfirmationHash { get; init; }
}
public sealed record SettlementCommandResult(Guid RequestId, Guid? EntityId, string State,
    long RequestVersion, long FamilyVersion, long? EntityVersion);
public sealed record SettlementTotals(decimal OwnerReceived, decimal ApprovedExpense,
    decimal ConfirmedRefund, decimal PendingRefund, decimal OwnerOutstanding,
    decimal WrongRecipientOutstanding, bool HasOpenCase);
public sealed record SettlementAccessDecision(bool Allowed, Guid? PersonId, string Code);
public sealed record EvidenceAllocation(Guid DocumentId, decimal Amount);
public sealed record ExpenseLineInput(Guid? SourceLineId, Guid CategoryId, string Description,
    DateOnly IncurredOn, decimal? Quantity, decimal? Rate, decimal? LumpSum,
    EvidenceAllocation[] Allocations);
public sealed record RoundInput(ExpenseLineInput[] Lines);
public sealed record ReviewInput(string Decision, string Reason);

public sealed record SettlementPreview(Guid RevisionId, long RevisionVersion, string ConfirmationHash, string SnapshotJson)
{
    public Guid RequestId { get; init; }
    public Guid RoundId { get; init; }
    public long RequestVersion { get; init; }
    public long FamilyVersion { get; init; }
    public long RoundVersion { get; init; }
}
public sealed record SettlementRoute(TPR10.Api.FieldAdvances.Requests.RouteChoice Supervisor,
    TPR10.Api.FieldAdvances.Requests.RouteChoice ProjectManager, TPR10.Api.FieldAdvances.Requests.RouteChoice FinanceChecker);

public sealed record SettlementSubmissionTiming(DateTimeOffset InitialSubmittedAtUtc, bool? InitialSubmissionOnTime,
    DateTimeOffset SubmittedAtUtc, Guid? WorkCompletionId, DateOnly? OriginalDueOn, DateOnly? EffectiveDueOn,
    bool? SubmissionOnTime, DateOnly? CorrectionDueOn, bool? CorrectionOnTime);

public sealed record RefundInput(decimal Amount, DateOnly ReturnedOn, string Channel, string CompanyRecipient,
    Guid[] EvidenceIds, Guid? AdjustmentCaseId);

public sealed record RefundCorrectionInput(Guid RefundId, decimal CorrectAmount, Guid[] EvidenceIds)
{
    public Guid? SupersedesId { get; init; }
}
public sealed record CasePaymentInput(Guid CaseId, decimal Amount, DateOnly PaidOn, Guid[] EvidenceIds)
{
    public Guid? SupersedesId { get; init; }
}

public abstract record AdjustmentEvidenceTarget;
public sealed record CorrectionEvidenceTarget(Guid RefundId) : AdjustmentEvidenceTarget;
public sealed record CaseRefundEvidenceTarget(Guid CaseId) : AdjustmentEvidenceTarget;
public sealed record CasePaymentEvidenceTarget(Guid CaseId) : AdjustmentEvidenceTarget;

public sealed record DispositionInput(string Disposition, Guid[] EvidenceIds, Guid[] PaymentIds);
public sealed record ExcessPaymentInput(Guid ExcessId, Guid RecipientPersonId, decimal Amount, DateOnly PaidOn, Guid[] EvidenceIds)
{
    public Guid? SupersedesId { get; init; }
}
public sealed record ExpenseEvidenceTarget(Guid SourceRoundId) : AdjustmentEvidenceTarget;
public sealed record ExcessPaymentEvidenceTarget(Guid ExcessId) : AdjustmentEvidenceTarget;
public sealed record ExcessDispositionEvidenceTarget(Guid ExcessId) : AdjustmentEvidenceTarget;
public sealed record IncidentRecoveryEvidenceTarget(Guid IncidentId) : AdjustmentEvidenceTarget;
