using TPR10.Api.FieldAdvances.Payments;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TPR10.Api.FieldAdvances.Policies;

namespace TPR10.Api.FieldAdvances.Requests;

public sealed record CreateRequestHttp(CreateRequest Input, RequestCommand Meta);
public sealed record UpdateRequestHttp(RequestContent Content, RequestCommand Meta);
public sealed record ConfirmRequestHttp(string ConfirmationHash, RequestCommand Meta);
public sealed record ReviewRequestHttp(RequestDecision Decision, RequestCommand Meta);
public sealed record RequestStepView(Guid Id, string LevelCode, int Order, Guid? AssigneePersonId, string? HoldCode);
public sealed record RequestEstimateLineHttp(Guid Id, Guid CategoryId, string Description, string Mode,
    string? Quantity, string? UnitRate, string Amount);
public sealed record RequestEstimateHttp(RequestEstimateLineHttp[] Lines, string Total)
{
    internal static RequestEstimateHttp From(Estimate e) => new(e.Lines.Select(x => new RequestEstimateLineHttp(
        x.Id, x.CategoryId, x.Description, x.Mode, x.Quantity?.ToString("0.####", CultureInfo.InvariantCulture),
        x.UnitRate?.ToString("0.00", CultureInfo.InvariantCulture), x.Amount.ToString("0.00", CultureInfo.InvariantCulture))).ToArray(),
        e.Total.ToString("0.00", CultureInfo.InvariantCulture));
}
public sealed record RequestHttpView(Guid Id, Guid RevisionId, long Version, string State, FinanceScope Scope,
    Guid OwnerPersonId, Guid? DelegatePersonId, RequestContent Content, RequestEstimateHttp? Estimate,
    string? HoldCode, RequestStepView? CurrentStep)
{
    public ApprovalBasisHttp? Basis { get; init; }
    internal static RequestHttpView From(RequestView v) => new(v.Id, v.RevisionId, v.Version, v.State, v.Scope,
        v.OwnerPersonId, v.DelegatePersonId, v.Content, v.Estimate is null ? null : RequestEstimateHttp.From(v.Estimate), v.HoldCode, v.CurrentStep)
    { Basis = v.Basis is null ? null : ApprovalBasisHttp.From(v.Basis) };
}
public sealed record RequestQuoteHttp(Guid RequestId, Guid RevisionId, long Version, string ConfirmationHash,
    RequestEstimateHttp Estimate, PolicyHttpSnapshot Policy, string[] Levels)
{
    public ApprovalBasisHttp Basis { get; init; } = null!;
    public long FamilyVersion { get; init; }
}
public sealed record ApprovalBasisItemHttp(Guid RequestId, Guid RevisionId, string ApprovedAmount);
public sealed record ApprovalBasisHttp(string Amount, ApprovalBasisItemHttp[] Items)
{
    internal static ApprovalBasisHttp From(ApprovalBasis b) => new(b.Amount.ToString("0.00", CultureInfo.InvariantCulture),
        b.Items.Select(x => new ApprovalBasisItemHttp(x.RequestId, x.RevisionId,
            x.ApprovedAmount.ToString("0.00", CultureInfo.InvariantCulture))).ToArray());
}
public sealed record RequestHistory(Guid EventId, Guid RevisionId, string Action, Guid? Actor,
    DateTimeOffset Time, string Reason, Guid? StepId, long Version, JsonElement Metadata);
public sealed record RequestScopeChoice(FinanceScope Scope, string WorkspaceName, string ProjectName, string[] Capabilities);
public sealed record RequestPersonChoice(Guid Id, string Name);
public sealed record RequestScreen(Guid RequestId, Guid RevisionId, long Version, int RevisionNumber,
    RequestContent? PreviousContent, PolicyHttpSnapshot? FrozenPolicy, string[] Actions);
public sealed record RequestRevisionSummary(Guid RequestId, Guid RevisionId, int RevisionNumber, DateTimeOffset? SubmittedAtUtc);
public sealed record RequestRevisionStep(Guid Id, string LevelCode, int Order, string? Outcome,
    Guid? DecidedByPersonId, DateTimeOffset? DecidedAtUtc, string? DecisionReason);
public sealed record RequestRevisionDetail(Guid RequestId, Guid RevisionId, int RevisionNumber, FinanceScope Scope,
    DateTimeOffset? SubmittedAtUtc, RequestContent Content, RequestEstimateHttp? Estimate,
    PolicyHttpSnapshot? FrozenPolicy, RequestRevisionStep[] Steps)
{
    public ApprovalBasisHttp? Basis { get; init; }
}

internal static class RequestHttp
{
    // Local strict input options: do not change legacy policy/grant or retained receipt serializers.
    internal static readonly JsonSerializerOptions InputOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        RespectRequiredConstructorParameters = true
    };
    internal static IResult Result<T>(FinanceResult<T> result) => result.Status is >= 200 and < 300
        ? Results.Json(Wire(result.Value), statusCode: result.Status)
        : Results.Problem(statusCode: result.Status, title: result.Status switch
        {
            400 => "ข้อมูลคำขอไม่ถูกต้อง",
            403 => "ไม่มีสิทธิ์ดำเนินการคำขอนี้",
            _ => "สถานะหรือรุ่นข้อมูลเปลี่ยน กรุณาโหลดใหม่"
        }, extensions: new Dictionary<string, object?> { ["code"] = result.Code });
    private static object? Wire<T>(T value) => value switch
    {
        RequestView v => RequestHttpView.From(v),
        RequestView[] rows => rows.Select(RequestHttpView.From).ToArray(),
        PolicySnapshot p => new PolicyHttpSnapshot(p.CompanyVersionId, p.ProjectVersionId, PolicyWireContent.From(p.Content), p.ContentHash, p.Scope, p.RequiresReview),
        ConfirmationQuote q => new RequestQuoteHttp(q.RequestId, q.RevisionId, q.Version, q.ConfirmationHash,
            RequestEstimateHttp.From(q.Estimate), new(q.Policy.CompanyVersionId, q.Policy.ProjectVersionId,
                PolicyWireContent.From(q.Policy.Content), q.Policy.ContentHash, q.Policy.Scope, q.Policy.RequiresReview), q.Levels)
        { Basis = ApprovalBasisHttp.From(q.Basis), FamilyVersion = q.FamilyVersion },
        _ => value
    };
}
