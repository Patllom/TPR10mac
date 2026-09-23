using TPR10.Api.Identity.Csrf;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Identity.Data;
using TPR10.Api.Data;
using TPR10.Api.Auditing;
using Microsoft.EntityFrameworkCore;

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
        endpoints.MapPost("/api/v1/auth/login", (LoginRequest request, HttpContext context, LoginService login, CancellationToken ct)
            => login.LoginAsync(request, context, ct));
        endpoints.MapGet("/api/v1/auth/session", (RequestSession session) => Results.Ok(session.View)).RequireAuthorization();
        endpoints.MapPost("/api/v1/auth/logout", async (HttpContext context, RequestSession session, Tpr10DbContext db,
            IAuditEventWriter audit, TimeProvider clock, CancellationToken ct) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var entity = await db.Set<IdentitySession>().SingleAsync(x => x.Id == session.Entity!.Id, ct);
            entity.RevokedAtUtc = clock.GetUtcNow();
            await audit.WriteAsync("identity.logout", entity.UserId, new Dictionary<string, string> { ["outcome"] = "success" }, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            context.Response.Cookies.Delete(CsrfService.SessionCookieName, SessionAuthenticationHandler.CookieOptions());
            context.Response.Cookies.Delete(CsrfService.CookieName, SessionAuthenticationHandler.CookieOptions());
            return Results.NoContent();
        }).RequireAuthorization();
    }
}

public sealed class CsrfIssuerMetadata;
public sealed record LoginRequest(string Username, string Password);
