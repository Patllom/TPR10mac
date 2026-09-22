using TPR10.Api.Identity.Csrf;

namespace TPR10.Api.Identity;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/auth/csrf", async (HttpContext context, CsrfService csrf, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(new { token = await csrf.IssueAsync(context, cancellationToken) }); }
            catch (PreAuthCapacityException) { return Results.Problem(statusCode: 503, title: "ระบบยังไม่พร้อมออกโทเคน กรุณาลองใหม่ภายหลัง"); }
        }).WithMetadata(new CsrfIssuerMetadata());
    }
}

public sealed class CsrfIssuerMetadata;
