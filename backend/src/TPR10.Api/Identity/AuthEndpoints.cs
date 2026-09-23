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
        endpoints.MapPost("/api/v1/auth/logout-all", async (HttpContext context, ISessionService sessions,
            Tpr10DbContext db, IAuditEventWriter audit, CancellationToken ct) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct);
            var token = context.Request.Cookies[CsrfService.SessionCookieName];
            var current = token is null ? null : await sessions.ValidateAsync(token, ct);
            if (current is null) return Results.Unauthorized();
            await sessions.RevokeUserAsync(current.UserId, "self-sign-out", ct);
            await audit.WriteAsync("identity.logout-all", current.UserId,
                new Dictionary<string, string> { ["outcome"] = "success" }, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            context.Response.Cookies.Delete(CsrfService.SessionCookieName, SessionAuthenticationHandler.CookieOptions());
            context.Response.Cookies.Delete(CsrfService.CookieName, SessionAuthenticationHandler.CookieOptions());
            return Results.NoContent();
        }).RequireAuthorization().WithMetadata(AllowedSessionStages.Common);
        endpoints.MapPost("/api/v1/auth/password/change", (Reset.PasswordChangeRequest request, Reset.PasswordResetService reset,
            HttpContext context, RequestSession current, CancellationToken ct) => reset.ChangeAsync(request, context, current, ct))
            .RequireAuthorization().WithMetadata(new AllowedSessionStages(SessionStage.PasswordChangeRequired, SessionStage.Active));
        endpoints.MapPost("/api/v1/auth/password-reset/request", (Reset.ResetRequest request, Reset.PasswordResetService reset, CancellationToken ct)
            => reset.RequestAsync(request, ct));
        endpoints.MapPost("/api/v1/auth/password-reset/complete", (Reset.ResetCompleteRequest request, Reset.PasswordResetService reset, RequestSession current, CancellationToken ct)
            => reset.CompleteAsync(request, current, ct));
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
