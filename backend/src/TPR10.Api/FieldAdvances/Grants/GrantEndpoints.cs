using TPR10.Api.Identity.Sessions;

namespace TPR10.Api.FieldAdvances.Grants;

public sealed record ProposeGrantRequest(GrantSpec Spec, CommandMeta Meta);
public sealed record ReviewGrantRequest(string Decision, CommandMeta Meta);

// Task 6 wires DI, route registration and the HTTP security boundary.
public static class GrantEndpoints
{
    public static async Task<IResult> ProposeAsync(GrantService service, RequestSession session, ProposeGrantRequest request, CancellationToken ct) =>
        Http(await service.ProposeAsync(session.Entity?.UserId ?? Guid.Empty, request.Spec, request.Meta, ct));
    public static async Task<IResult> ReviewAsync(GrantService service, RequestSession session, Guid id, ReviewGrantRequest request, CancellationToken ct) =>
        Http(await service.ReviewAsync(session.Entity?.UserId ?? Guid.Empty, id, request.Decision, request.Meta, ct));
    public static async Task<IResult> RevokeAsync(GrantLifecycle lifecycle, RequestSession session, Guid id, CommandMeta meta, CancellationToken ct) =>
        Http(await lifecycle.RevokeAsync(session.Entity?.UserId ?? Guid.Empty, id, meta, ct));

    private static IResult Http(FinanceResult<GrantView> result) => result.Status is >= 200 and < 300
        ? Results.Json(result.Value, statusCode: result.Status)
        : Results.Problem(statusCode: result.Status, title: result.Status switch
        {
            400 => "ข้อมูลหรือเหตุผลการมอบหมายสิทธิ์ไม่ถูกต้อง",
            403 => "ไม่มีสิทธิ์ดำเนินการมอบหมายนี้",
            _ => "คำสั่งซ้ำต่างเนื้อหา หรือสถานะและรุ่นข้อมูลเปลี่ยนแปลง กรุณาโหลดใหม่"
        }, extensions: new Dictionary<string, object?> { ["code"] = result.Code });
}
