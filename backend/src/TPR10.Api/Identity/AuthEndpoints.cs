using TPR10.Api.Identity.Csrf;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Identity.Data;
using TPR10.Api.Data;
using TPR10.Api.Auditing;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Identity.Mfa;

namespace TPR10.Api.Identity;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMfaEndpoints();
        endpoints.MapGet("/api/v1/auth/csrf", async (HttpContext context, CsrfService csrf, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(new { token = await csrf.IssueAsync(context, cancellationToken) }); }
            catch (PreAuthCapacityException) { return Results.Problem(statusCode: 503, title: "ระบบยังไม่พร้อมออกโทเคน กรุณาลองใหม่ภายหลัง"); }
        }).WithMetadata(new CsrfIssuerMetadata(), AllowedSessionStages.Common);
        endpoints.MapPost("/api/v1/auth/login", (LoginRequest request, HttpContext context, LoginService login, CancellationToken ct)
            => login.LoginAsync(request, context, ct));
        endpoints.MapGet("/api/v1/auth/session", (RequestSession session) => Results.Ok(session.View)).RequireAuthorization().WithMetadata(AllowedSessionStages.Common);
        endpoints.MapPost("/api/v1/auth/logout", async (HttpContext context, RequestSession session, Tpr10DbContext db,
            IAuditEventWriter audit, TimeProvider clock, CancellationToken ct) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var entity = session.Entity!;
            var now = clock.GetUtcNow();
            // UPDATE locks and rechecks the current row; a winning rotation must not yield false logout success.
            var revoked = await db.Set<IdentitySession>().Where(x => x.Id == entity.Id && x.RevokedAtUtc == null
                && x.ExpiresAtUtc > now && db.Set<IdentityUser>().Any(u => u.Id == x.UserId && u.IsActive && u.SecurityVersion == x.SecurityVersion))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAtUtc, now), ct);
            if (revoked != 1) return Results.Problem(statusCode: 401, title: "Session เปลี่ยนแปลงแล้ว กรุณาตรวจสถานะอีกครั้ง");
            await audit.WriteAsync("identity.logout", entity.UserId, new Dictionary<string, string> { ["outcome"] = "success" }, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            context.Response.Cookies.Delete(CsrfService.SessionCookieName, SessionAuthenticationHandler.CookieOptions());
            context.Response.Cookies.Delete(CsrfService.CookieName, SessionAuthenticationHandler.CookieOptions());
            return Results.NoContent();
        }).RequireAuthorization().WithMetadata(AllowedSessionStages.Common);
    }
}

public sealed class CsrfIssuerMetadata;
public sealed record LoginRequest(string Username, string Password);
