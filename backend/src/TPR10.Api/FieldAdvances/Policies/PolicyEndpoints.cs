using TPR10.Api.Identity.Sessions;

namespace TPR10.Api.FieldAdvances.Policies;

public sealed record CreatePolicyRequest(FinanceScope Scope, Guid? BaseCompanyVersionId, PolicyContent Content, CommandMeta Meta);
public sealed record UpdatePolicyRequest(PolicyContent Content, CommandMeta Meta);
public sealed record ReviewPolicyRequest(string Decision, CommandMeta Meta);

public static class PolicyEndpoints
{
    public static async Task<IResult> CreateAsync(PolicyService service, RequestSession session, CreatePolicyRequest request, CancellationToken ct) =>
        Http(await service.CreateDraftAsync(session.Entity?.UserId ?? Guid.Empty, request.Scope, request.BaseCompanyVersionId, request.Content, request.Meta, ct));
    public static async Task<IResult> UpdateAsync(PolicyService service, RequestSession session, Guid id, UpdatePolicyRequest request, CancellationToken ct) =>
        Http(await service.UpdateDraftAsync(session.Entity?.UserId ?? Guid.Empty, id, request.Content, request.Meta, ct));
    public static async Task<IResult> SubmitAsync(PolicyService service, RequestSession session, Guid id, CommandMeta meta, CancellationToken ct) =>
        Http(await service.SubmitAsync(session.Entity?.UserId ?? Guid.Empty, id, meta, ct));
    public static async Task<IResult> ReviewAsync(PolicyService service, RequestSession session, Guid id, ReviewPolicyRequest request, CancellationToken ct) =>
        Http(await service.ReviewAsync(session.Entity?.UserId ?? Guid.Empty, id, request.Decision, request.Meta, ct));
    private static IResult Http(FinanceResult<PolicyView> result) => result.Status is >= 200 and < 300
        ? Results.Json(result.Value, statusCode: result.Status)
        : Results.Problem(statusCode: result.Status, title: result.Status switch
        {
            400 => "ข้อมูลหรือเหตุผลกติกาไม่ถูกต้อง",
            403 => "ไม่มีสิทธิ์ดำเนินการกับกติกานี้",
            _ => "เนื้อหา สถานะ หรือฉบับกติกาเปลี่ยนแปลง กรุณาโหลดใหม่และส่งตรวจอีกครั้ง"
        }, extensions: new Dictionary<string, object?> { ["code"] = result.Code });
}
