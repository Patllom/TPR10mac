using System.Text.Json.Serialization;
using System.Globalization;

namespace TPR10.Api.FieldAdvances.Policies;

// HTTP-only types: domain hashing, stored evidence and receipts keep their original serializer.
public sealed record PolicyHttpBand(int Order, string LevelCode,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)] decimal UpperInclusive);
public sealed record PolicyHttpCategory(Guid Id, string Name, bool Active, string? Unit,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)] decimal? StandardRate,
    string[] EvidenceTypes);
public sealed record PolicyHttpContent(int ClearingDays, PolicyHttpBand[] Bands, PolicyHttpCategory[] Categories)
{
    internal PolicyContent Domain() => new(ClearingDays,
        Bands?.Select(x => x is null ? null! : new ApprovalBand(x.Order, x.LevelCode, x.UpperInclusive)).ToArray()!,
        Categories?.Select(x => x is null ? null! : new CategoryRule(x.Id, x.Name, x.Active, x.Unit, x.StandardRate, x.EvidenceTypes)).ToArray()!);
    internal static PolicyHttpContent From(PolicyContent c) => new(c.ClearingDays,
        c.Bands.Select(x => new PolicyHttpBand(x.Order, x.LevelCode, x.UpperInclusive)).ToArray(),
        c.Categories.Select(x => new PolicyHttpCategory(x.Id, x.Name, x.Active, x.Unit, x.StandardRate, x.EvidenceTypes)).ToArray());
}
public sealed record CreatePolicyHttpRequest(FinanceScope Scope, Guid? BaseCompanyVersionId, PolicyHttpContent Content, CommandMeta Meta);
public sealed record UpdatePolicyHttpRequest(PolicyHttpContent Content, CommandMeta Meta);
public sealed record PreviewPolicyHttpRequest(FinanceScope Scope,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)] decimal Total);
public sealed record PolicyWireBand(int Order, string LevelCode, string UpperInclusive);
public sealed record PolicyWireCategory(Guid Id, string Name, bool Active, string? Unit, string? StandardRate, string[] EvidenceTypes);
public sealed record PolicyWireContent(int ClearingDays, PolicyWireBand[] Bands, PolicyWireCategory[] Categories)
{
    internal static PolicyWireContent From(PolicyContent c) => new(c.ClearingDays,
        c.Bands.Select(x => new PolicyWireBand(x.Order, x.LevelCode, x.UpperInclusive.ToString("0.00", CultureInfo.InvariantCulture))).ToArray(),
        c.Categories.Select(x => new PolicyWireCategory(x.Id, x.Name, x.Active, x.Unit, x.StandardRate?.ToString("0.00", CultureInfo.InvariantCulture), x.EvidenceTypes)).ToArray());
}
public sealed record PolicyHttpView(Guid Id, FinanceScope Scope, string State, long Version, Guid? BaseCompanyVersionId,
    PolicyWireContent Content, string ContentHash, string Mode, Guid? SourcePolicyRevisionId,
    Guid? ExpectedActiveRevisionId, long ExpectedActiveVersion, Guid? ActiveRevisionId, long? ActiveVersion)
{
    internal static PolicyHttpView From(PolicyView v) => new(v.Id, v.Scope, v.State, v.Version, v.BaseCompanyVersionId,
        PolicyWireContent.From(v.Content), v.ContentHash, v.Mode, v.SourcePolicyRevisionId,
        v.ExpectedActiveRevisionId, v.ExpectedActiveVersion, v.ActiveRevisionId, v.ActiveVersion);
}
public sealed record PolicyHttpSnapshot(Guid CompanyVersionId, Guid? ProjectVersionId, PolicyWireContent Content,
    string ContentHash, FinanceScope? Scope, bool RequiresReview);
public sealed record PolicyHttpHistory(Guid Id, Guid PolicyRevisionId, string Outcome, DateTimeOffset ReviewedAt,
    long? ReviewedVersion, string? ContentHash, PolicyWireContent? Content, Guid ReviewerId, string Reason);
public sealed record PolicyCompanyRevision(Guid Id, PolicyWireContent Content, string ContentHash);
public sealed record PolicyCompanyContext(PolicyCompanyRevision Basis, PolicyCompanyRevision Latest);
public sealed record PolicyScopeChoice(FinanceScope Scope, string WorkspaceName, string? ProjectName, string[] Capabilities);

internal static class PolicyHttp
{
    internal static object? Wire<T>(T value) => value switch
    {
        PolicyView v => PolicyHttpView.From(v),
        PolicyView[] rows => rows.Select(PolicyHttpView.From).ToArray(),
        PolicySnapshot s => new PolicyHttpSnapshot(s.CompanyVersionId, s.ProjectVersionId, PolicyWireContent.From(s.Content), s.ContentHash, s.Scope, s.RequiresReview),
        PolicyHistoryView[] rows => rows.Select(x => new PolicyHttpHistory(x.Id, x.PolicyRevisionId, x.Outcome, x.ReviewedAt,
            x.ReviewedVersion, x.ContentHash, x.Content is null ? null : PolicyWireContent.From(x.Content), x.ReviewerId, x.Reason)).ToArray(),
        _ => value
    };
}
