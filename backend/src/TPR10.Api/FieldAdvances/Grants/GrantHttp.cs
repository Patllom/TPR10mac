using System.Globalization;
using System.Text.Json.Serialization;
using TPR10.Api.Identity.Sessions;

namespace TPR10.Api.FieldAdvances.Grants;

public sealed record GrantWireSpec(Guid RecipientId, FinanceScope Scope, string[] Capabilities, string[] DelegableCapabilities,
    string? ApprovalLimit, DateTimeOffset StartsAt, DateTimeOffset? EndsAt)
{
    public string[] ApprovalLevels { get; init; } = [];
    public string[] DelegableApprovalLevels { get; init; } = [];
    public string[] PrimaryApprovalLevels { get; init; } = [];
    public string[] DelegablePrimaryApprovalLevels { get; init; } = [];
    internal static GrantWireSpec From(GrantSpec s) => new(s.RecipientId, s.Scope, s.Capabilities, s.DelegableCapabilities,
        s.ApprovalLimit?.ToString("0.00", CultureInfo.InvariantCulture), s.StartsAt, s.EndsAt)
    {
        ApprovalLevels = s.ApprovalLevels,
        DelegableApprovalLevels = s.DelegableApprovalLevels,
        PrimaryApprovalLevels = s.PrimaryApprovalLevels,
        DelegablePrimaryApprovalLevels = s.DelegablePrimaryApprovalLevels
    };
}
public sealed record GrantHttpSpec(Guid RecipientId, FinanceScope Scope, string[] Capabilities, string[] DelegableCapabilities,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] decimal? ApprovalLimit, DateTimeOffset StartsAt, DateTimeOffset? EndsAt)
{
    public string[] ApprovalLevels { get; init; } = [];
    public string[] DelegableApprovalLevels { get; init; } = [];
    public string[] PrimaryApprovalLevels { get; init; } = [];
    public string[] DelegablePrimaryApprovalLevels { get; init; } = [];
    internal GrantSpec Domain() => new(RecipientId, Scope, Capabilities, DelegableCapabilities, ApprovalLimit, StartsAt, EndsAt)
    {
        ApprovalLevels = ApprovalLevels,
        DelegableApprovalLevels = DelegableApprovalLevels,
        PrimaryApprovalLevels = PrimaryApprovalLevels,
        DelegablePrimaryApprovalLevels = DelegablePrimaryApprovalLevels
    };
}
public sealed record ProposeGrantHttpRequest(GrantHttpSpec Spec, CommandMeta Meta);
public sealed record GrantHttpView(Guid Id, string State, long Version, GrantWireSpec Spec);

public static class GrantHttp
{
    public static async Task<IResult> Propose(GrantService service, RequestSession session, ProposeGrantHttpRequest request, CancellationToken ct) =>
        Result(await service.ProposeAsync(session.Entity?.UserId ?? Guid.Empty, request.Spec?.Domain()!, request.Meta, ct));
    public static async Task<IResult> Review(GrantService service, RequestSession session, Guid id, ReviewGrantRequest request, CancellationToken ct) =>
        Result(await service.ReviewAsync(session.Entity?.UserId ?? Guid.Empty, id, request.Decision, request.Meta, ct));
    public static async Task<IResult> Revoke(GrantLifecycle service, RequestSession session, Guid id, CommandMeta request, CancellationToken ct) =>
        Result(await service.RevokeAsync(session.Entity?.UserId ?? Guid.Empty, id, request, ct));
    public static async Task<IResult> Scopes(GrantReadService s, int? page, int? pageSize, CancellationToken ct) => Result(await s.ScopesAsync(page ?? 1, pageSize ?? 100, ct));
    public static async Task<IResult> Recipients(GrantReadService s, Guid workspaceId, Guid? projectId, string? prefix, int? page, int? pageSize, CancellationToken ct) => Result(await s.RecipientsAsync(new(workspaceId, projectId), prefix ?? "", page ?? 1, pageSize ?? 20, ct));
    public static async Task<IResult> List(GrantReadService s, Guid workspaceId, Guid? projectId, int? page, int? pageSize, CancellationToken ct) => Result(await s.ListAsync(new(workspaceId, projectId), false, page ?? 1, pageSize ?? 20, ct));
    public static async Task<IResult> Queue(GrantReadService s, Guid workspaceId, Guid? projectId, int? page, int? pageSize, CancellationToken ct) => Result(await s.ListAsync(new(workspaceId, projectId), true, page ?? 1, pageSize ?? 20, ct));
    public static async Task<IResult> Details(GrantReadService s, Guid id, Guid workspaceId, Guid? projectId, CancellationToken ct) => Result(await s.DetailsAsync(new(workspaceId, projectId), id, ct));
    public static async Task<IResult> History(GrantReadService s, Guid id, Guid workspaceId, Guid? projectId, int? page, int? pageSize, CancellationToken ct) => Result(await s.HistoryAsync(new(workspaceId, projectId), id, page ?? 1, pageSize ?? 20, ct));
    private static IResult Result<T>(FinanceResult<T> result) => result.Status is >= 200 and < 300
        ? Results.Json(result.Value is GrantView v ? (object)new GrantHttpView(v.Id, v.State, v.Version, GrantWireSpec.From(v.Spec)) : result.Value, statusCode: result.Status)
        : Results.Problem(statusCode: result.Status, title: result.Status == 403 ? "ไม่มีสิทธิ์ดำเนินการมอบหมายนี้" : result.Status == 400 ? "ข้อมูลหรือเหตุผลไม่ถูกต้อง" : "สถานะหรือรุ่นข้อมูลเปลี่ยน กรุณาโหลดใหม่",
            extensions: new Dictionary<string, object?> { ["code"] = result.Code });
}
