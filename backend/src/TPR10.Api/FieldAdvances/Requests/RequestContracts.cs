using TPR10.Api.FieldAdvances.Payments;
namespace TPR10.Api.FieldAdvances.Requests;

public sealed record EstimateLineInput(Guid Id, Guid CategoryId, string Description, string Mode,
    string? Quantity, string? UnitRate, string? LumpSum);
public sealed record EstimateLine(Guid Id, Guid CategoryId, string Description, string Mode,
    decimal? Quantity, decimal? UnitRate, decimal Amount);
public sealed record Estimate(EstimateLine[] Lines, decimal Total);
public sealed record RequestContent(string Kind, string Purpose, DateOnly FieldStart, DateOnly FieldEnd,
    DateOnly NeededOn, Guid[] TeamPersonIds, EstimateLineInput[] Lines);
public sealed record CreateRequest(FinanceScope Scope, Guid OwnerPersonId, RequestContent Content);
public sealed record RequestCommand(Guid CommandId, long ExpectedVersion, string Reason);
public sealed record RequestView(Guid Id, Guid RevisionId, long Version, string State, FinanceScope Scope,
    Guid OwnerPersonId, Guid? DelegatePersonId, RequestContent Content, Estimate? Estimate, string? HoldCode)
{
    public ApprovalBasis? Basis { get; init; }
    // Read-only enrichment; command receipts remain historical and never imply current authority.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RequestStepView? CurrentStep { get; init; }
}
public sealed record ConfirmationQuote(Guid RequestId, Guid RevisionId, long Version,
    string ConfirmationHash, Estimate Estimate, PolicySnapshot Policy, string[] Levels)
{
    public ApprovalBasis Basis { get; init; } = null!;
    public long FamilyVersion { get; init; }
}
public sealed record RequestDecision(string Outcome, Guid StepId);
public sealed record RequestParticipationHistory(Guid[] OwnerPersonIds, Guid[] DelegatePersonIds,
    Guid[] ApprovedPersonIds, Guid[] CheckerPersonIds, Guid[] PayerPersonIds);
