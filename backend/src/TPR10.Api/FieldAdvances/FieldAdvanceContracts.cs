namespace TPR10.Api.FieldAdvances;

public sealed record FinanceScope(Guid WorkspaceId, Guid? ProjectId);
public sealed record FinanceDecision(bool Allowed, string Code);
public sealed record ApprovalBand(int Order, string LevelCode, decimal UpperInclusive);
public sealed record CategoryRule(Guid Id, string Name, bool Active, string? Unit,
    decimal? StandardRate, string[] EvidenceTypes);
public sealed record PolicyContent(int ClearingDays, ApprovalBand[] Bands, CategoryRule[] Categories);
public sealed record PolicySnapshot(Guid CompanyVersionId, Guid? ProjectVersionId,
    PolicyContent Content, string ContentHash)
{
    public FinanceScope? Scope { get; init; }
    public bool RequiresReview { get; init; }
}
public sealed record CommandMeta(Guid CommandId, long ExpectedVersion, string Reason)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PolicyCommandOptions? Policy { get; init; }
}
public sealed record PolicyCommandOptions(string Mode, Guid? SourcePolicyRevisionId = null,
    Guid? BaseCompanyVersionId = null, Guid? ExpectedActiveRevisionId = null,
    long ExpectedActiveVersion = 0, string? ExpectedContentHash = null);
public sealed record FinanceResult<T>(int Status, string Code, T? Value);
public sealed record PolicyView(Guid Id, FinanceScope Scope, string State, long Version,
    Guid? BaseCompanyVersionId, PolicyContent Content, string ContentHash)
{
    public string Mode { get; init; } = "Company";
    public Guid? SourcePolicyRevisionId { get; init; }
    public Guid? ExpectedActiveRevisionId { get; init; }
    public long ExpectedActiveVersion { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ActiveRevisionId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public long? ActiveVersion { get; init; }
}
public sealed record PolicyHistoryView(Guid Id, Guid PolicyRevisionId, string Outcome,
    DateTimeOffset ReviewedAt, long? ReviewedVersion, string? ContentHash, PolicyContent? Content)
{
    public Guid ReviewerId { get; init; }
    public string Reason { get; init; } = "";
}
// RecipientId is an account. A later server resolver must obtain its explicit person identity.
public sealed record GrantSpec(Guid RecipientId, FinanceScope Scope, string[] Capabilities,
    string[] DelegableCapabilities, decimal? ApprovalLimit,
    DateTimeOffset StartsAt, DateTimeOffset? EndsAt)
{
    public string[] ApprovalLevels { get; init; } = [];
    public string[] DelegableApprovalLevels { get; init; } = [];
    public string[] PrimaryApprovalLevels { get; init; } = [];
    public string[] DelegablePrimaryApprovalLevels { get; init; } = [];
}
public sealed record GrantView(Guid Id, string State, long Version, GrantSpec Spec);
public sealed record RequestParticipation(Guid OwnerPersonId, Guid? DelegatePersonId,
    Guid[] ApproverPersonIds, Guid[] CheckerPersonIds, Guid[] PayerPersonIds);
