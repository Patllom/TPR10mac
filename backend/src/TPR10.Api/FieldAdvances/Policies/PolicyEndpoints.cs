using TPR10.Api.Identity.Sessions;

namespace TPR10.Api.FieldAdvances.Policies;

public sealed record CreatePolicyRequest(FinanceScope Scope, Guid? BaseCompanyVersionId, PolicyContent Content, CommandMeta Meta);
public sealed record UpdatePolicyRequest(PolicyContent Content, CommandMeta Meta);
public sealed record ReviewPolicyRequest(string Decision, CommandMeta Meta);
public sealed record PreviewPolicyRequest(FinanceScope Scope, decimal Total);

public static class PolicyEndpoints
{
    public static async Task<IResult> ListAsync(PolicyReadService service, RequestSession session, Guid workspaceId,
        Guid? projectId, int? page, int? pageSize, CancellationToken ct) =>
        Http(await service.ListAsync(session.Entity?.UserId ?? Guid.Empty, new(workspaceId, projectId), page ?? 1, pageSize ?? 20, ct));
    public static async Task<IResult> DetailsAsync(PolicyReadService service, RequestSession session, Guid id,
        Guid workspaceId, Guid? projectId, CancellationToken ct) =>
        Http(await service.DetailsAsync(session.Entity?.UserId ?? Guid.Empty, new(workspaceId, projectId), id, ct));
    public static async Task<IResult> HistoryAsync(PolicyReadService service, RequestSession session, Guid id,
        Guid workspaceId, Guid? projectId, int? page, int? pageSize, CancellationToken ct) =>
        Http(await service.HistoryAsync(session.Entity?.UserId ?? Guid.Empty, new(workspaceId, projectId), id, page ?? 1, pageSize ?? 20, ct));
    public static async Task<IResult> ReviewQueueAsync(PolicyReadService service, RequestSession session, Guid workspaceId,
        Guid? projectId, int? page, int? pageSize, CancellationToken ct) =>
        Http(await service.ReviewQueueAsync(session.Entity?.UserId ?? Guid.Empty, new(workspaceId, projectId), page ?? 1, pageSize ?? 20, ct));
    public static async Task<IResult> EffectiveAsync(PolicyResolver resolver, TimeProvider clock, Guid workspaceId,
        Guid? projectId, DateTimeOffset? asOf, CancellationToken ct) =>
        Http(await resolver.ResolveAsync(new(workspaceId, projectId), asOf ?? clock.GetUtcNow(), ct));
    public static async Task<IResult> PreviewAsync(PolicyReadService service, RequestSession session, PreviewPolicyRequest request, CancellationToken ct) =>
        Http(await service.PreviewAsync(session.Entity?.UserId ?? Guid.Empty, request.Scope, request.Total, ct));
    public static async Task<IResult> CreateAsync(PolicyService service, RequestSession session, CreatePolicyRequest request, CancellationToken ct) =>
        Http(await service.CreateDraftAsync(session.Entity?.UserId ?? Guid.Empty, request.Scope, request.BaseCompanyVersionId, request.Content, request.Meta, ct));
    public static async Task<IResult> UpdateAsync(PolicyService service, RequestSession session, Guid id, UpdatePolicyRequest request, CancellationToken ct) =>
        Http(await service.UpdateDraftAsync(session.Entity?.UserId ?? Guid.Empty, id, request.Content, request.Meta, ct));
    public static async Task<IResult> SubmitAsync(PolicyService service, RequestSession session, Guid id, CommandMeta meta, CancellationToken ct) =>
        Http(await service.SubmitAsync(session.Entity?.UserId ?? Guid.Empty, id, meta, ct));
    public static async Task<IResult> ReviewAsync(PolicyService service, RequestSession session, Guid id, ReviewPolicyRequest request, CancellationToken ct) =>
        Http(await service.ReviewAsync(session.Entity?.UserId ?? Guid.Empty, id, request.Decision, request.Meta, ct));
    private static IResult Http<T>(FinanceResult<T> result) => result.Status is >= 200 and < 300
        ? Results.Json(result.Value, statusCode: result.Status)
        : Results.Problem(statusCode: result.Status, title: result.Status switch
        {
            400 => "ข้อมูลหรือเหตุผลกติกาไม่ถูกต้อง",
            403 => "ไม่มีสิทธิ์ดำเนินการกับกติกานี้",
            _ => "เนื้อหา สถานะ หรือฉบับกติกาเปลี่ยนแปลง กรุณาโหลดใหม่และส่งตรวจอีกครั้ง"
        }, extensions: new Dictionary<string, object?> { ["code"] = result.Code });
}
