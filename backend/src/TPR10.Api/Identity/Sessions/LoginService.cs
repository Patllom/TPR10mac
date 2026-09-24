using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.Identity.Csrf;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Passwords;

namespace TPR10.Api.Identity.Sessions;

public sealed class LoginService(Tpr10DbContext db, IIdentityProvider provider, ISessionService sessions,
    CsrfService csrf, IAuditEventWriter audit, TimeProvider clock, IEffectiveRolePolicy roles)
{
    public async Task<IResult> LoginAsync(LoginRequest request, HttpContext context, CancellationToken ct)
    {
        if (context.User.Identity?.IsAuthenticated == true) return Results.Conflict();
        var normalized = UsernameNormalizer.Normalize(request.Username);
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(normalized ?? "invalid-identifier"));
        var now = clock.GetUtcNow();
        // Keep the capacity lock short. Password work is serialized only for this identifier.
        await using (var admission = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241004)", ct);
            await db.Set<LoginAttemptWindow>().Where(x => x.ExpiresAtUtc <= now).ExecuteDeleteAsync(ct);
            if (!await db.Set<LoginAttemptWindow>().AnyAsync(x => x.IdentifierHash == key, ct))
            {
                if (await db.Set<LoginAttemptWindow>().CountAsync(ct) >= 10000) return Limited(context, 60);
                db.Add(new LoginAttemptWindow { IdentifierHash = key, WindowStartedAtUtc = now, ExpiresAtUtc = now.AddMinutes(30) });
                await db.SaveChangesAsync(ct);
            }
            await admission.CommitAsync(ct);
        }
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var attempts = await db.Set<LoginAttemptWindow>().FromSqlInterpolated($"SELECT * FROM login_attempt_windows WHERE identifier_hash={key} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (attempts is null) return Limited(context, 1);
        now = clock.GetUtcNow();
        if (attempts.LockedUntilUtc > now)
        {
            await audit.WriteAsync("identity.login.throttled", Guid.Empty, new Dictionary<string, string> { ["outcome"] = "denied" }, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Limited(context, (int)Math.Ceiling((attempts.LockedUntilUtc.Value - now).TotalSeconds));
        }
        if (attempts.WindowStartedAtUtc <= now.AddMinutes(-15) || attempts.LockedUntilUtc is not null)
        {
            attempts.FailedAttempts = 0;
            attempts.WindowStartedAtUtc = now;
            attempts.LockedUntilUtc = null;
        }
        var user = await db.Set<IdentityUser>().FromSqlInterpolated($"SELECT * FROM users WHERE normalized_username={normalized} FOR UPDATE").SingleOrDefaultAsync(ct);
        var credential = user is null ? null : await db.Set<LocalCredential>()
            .FromSqlInterpolated($"SELECT * FROM local_credentials WHERE user_id={user.Id} FOR UPDATE").SingleOrDefaultAsync(ct);
        var check = await provider.VerifyAsync(request.Username, request.Password, ct);
        if (check is null || user is null || credential is null || check.UserId != user.Id || !user.IsActive)
        {
            attempts.FailedAttempts++;
            if (attempts.FailedAttempts == 5) attempts.LockedUntilUtc = now.AddMinutes(15);
            attempts.ExpiresAtUtc = now.AddMinutes(30);
            if (credential is not null)
            {
                credential.FailedAttempts = attempts.FailedAttempts;
                credential.FailureWindowStartedAtUtc = attempts.WindowStartedAtUtc;
                credential.LockedUntilUtc = attempts.LockedUntilUtc;
            }
            await audit.WriteAsync("identity.login.failed", Guid.Empty, new Dictionary<string, string> { ["outcome"] = "denied" }, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Problem(statusCode: 401, title: "ไม่สามารถเข้าสู่ระบบได้");
        }
        if (!await csrf.ConsumePreAuthAsync(context, ct)) return Results.Problem(statusCode: 403, title: "คำขอไม่ผ่านการตรวจสอบความปลอดภัย");
        var stage = credential.MustChangePassword ? SessionStage.PasswordChangeRequired : SessionStage.Active;
        if (!credential.MustChangePassword)
        {
            var confirmed = await db.Set<MfaFactor>().AnyAsync(x => x.UserId == user.Id && x.ConfirmedAtUtc != null && x.RevokedAtUtc == null, ct);
            var mandatory = await roles.RequiresMfaAsync(user.Id, ct);
            stage = confirmed ? SessionStage.MfaChallengeRequired : mandatory ? SessionStage.MfaEnrollmentRequired : SessionStage.Active;
        }
        var issued = await sessions.IssueAsync(user.Id, stage, ct);
        if (credential.TemporaryExpiresAtUtc is not null) credential.TemporaryConsumedAtUtc = clock.GetUtcNow();
        credential.FailedAttempts = attempts.FailedAttempts = 0;
        credential.LockedUntilUtc = attempts.LockedUntilUtc = null;
        credential.FailureWindowStartedAtUtc = null;
        attempts.WindowStartedAtUtc = now;
        await audit.WriteAsync("identity.login", user.Id, new Dictionary<string, string> { ["outcome"] = "success" }, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        context.Response.Cookies.Append(CsrfService.SessionCookieName, issued.Token, SessionAuthenticationHandler.CookieOptions());
        context.Response.Cookies.Delete(CsrfService.CookieName, SessionAuthenticationHandler.CookieOptions());
        return Results.Ok(issued.View);
    }

    private static IResult Limited(HttpContext context, int seconds)
    {
        context.Response.Headers.RetryAfter = Math.Max(1, seconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.Problem(statusCode: 429, title: "ไม่สามารถเข้าสู่ระบบได้ กรุณาลองใหม่ภายหลัง");
    }
}
