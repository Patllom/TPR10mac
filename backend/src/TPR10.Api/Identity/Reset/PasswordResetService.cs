using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.Identity.Csrf;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Passwords;
using TPR10.Api.Identity.Sessions;

namespace TPR10.Api.Identity.Reset;

public sealed record ResetRequest(string Username);
public sealed record ResetCompleteRequest(string Token, string Password);
public sealed record PasswordChangeRequest(string CurrentPassword, string NewPassword);

public sealed class PasswordResetService(Tpr10DbContext db, IPasswordHasher hasher, ISessionService sessions,
    IResetDelivery delivery, IDataProtectionProvider protection, IOptions<CsrfOptions> options,
    IHostEnvironment environment, IAuditEventWriter audit, TimeProvider clock,
    Authorization.PermissionMutationGuard guard, Authorization.PermissionContext permission, IOptions<ResetOptions> resetOptions)
{
    private static IResult Accepted() => Results.Text("หากบัญชีรองรับการกู้คืน ระบบจะดำเนินการตามช่องทางที่กำหนด", statusCode: 202);
    private static IResult Invalid() => Results.Problem(statusCode: 400, type: "urn:tpr10:reset-invalid", title: "ไม่สามารถใช้คำขอกู้คืนรหัสผ่านนี้ได้");

    public async Task<IResult> RequestAsync(ResetRequest request, CancellationToken ct)
    {
        // Module 5 supplies production transport. Never imply that this local queue sent email.
        if (!resetOptions.Value.EmailEnabled || !(environment.IsDevelopment() || environment.IsEnvironment("Testing")) || options.Value.KeyRingPath is null) return Accepted();
        var normalized = UsernameNormalizer.Normalize(request.Username);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct);
        var user = await db.Set<IdentityUser>().FromSqlInterpolated($"SELECT * FROM users WHERE normalized_username={normalized} FOR UPDATE").SingleOrDefaultAsync(ct);
        var now = clock.GetUtcNow();
        if (user is null || !user.IsActive || string.IsNullOrWhiteSpace(user.Email)
            || !await db.Set<LocalCredential>().AnyAsync(x => x.UserId == user.Id, ct)) return Accepted();
        // Bound issuance for a recipient; repeated requests neither invalidate nor mint another token.
        if (await db.Set<PasswordResetRequest>().AnyAsync(x => x.UserId == user.Id && x.ExpiresAtUtc > now && x.ConsumedAtUtc == null && x.RevokedAtUtc == null, ct)) return Accepted();
        var raw = RandomNumberGenerator.GetBytes(32);
        var id = Guid.NewGuid();
        db.Add(new PasswordResetRequest { Id = id, UserId = user.Id, TokenHash = SHA256.HashData(raw), CreatedAtUtc = now, ExpiresAtUtc = now.AddMinutes(15) });
        var payload = protection.CreateProtector("TPR10.Reset.Delivery.v1", id.ToString("D")).Protect(WebEncoders.Base64UrlEncode(raw));
        await delivery.EnqueueAsync(id, user.Email, payload, ct);
        await AuditAsync("identity.password-reset.request", null, user.Id, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Accepted();
    }

    public async Task<IResult> CompleteAsync(ResetCompleteRequest request, RequestSession current, CancellationToken ct)
    {
        var hash = SessionService.HashToken(request.Token);
        if (hash is null) return Invalid();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct);
        var candidate = await db.Set<PasswordResetRequest>().AsNoTracking().SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
        if (candidate is null || current.Entity is { } caller && caller.UserId != candidate.UserId) return Invalid();
        var user = await db.Set<IdentityUser>().FromSqlInterpolated($"SELECT * FROM users WHERE id={candidate.UserId} FOR UPDATE").SingleAsync(ct);
        var reset = await db.Set<PasswordResetRequest>().FromSqlInterpolated($"SELECT * FROM password_reset_requests WHERE id={candidate.Id} FOR UPDATE").SingleAsync(ct);
        var credential = await db.Set<LocalCredential>().SingleOrDefaultAsync(x => x.UserId == user.Id, ct);
        if (!user.IsActive || credential is null || reset.ExpiresAtUtc <= clock.GetUtcNow() || reset.ConsumedAtUtc is not null || reset.RevokedAtUtc is not null) return Invalid();
        string encoded;
        try { encoded = await hasher.HashAsync(request.Password, ct); }
        catch (ArgumentException) { return Invalid(); }
        credential.PasswordHash = encoded;
        credential.MustChangePassword = false;
        credential.TemporaryExpiresAtUtc = credential.TemporaryConsumedAtUtc = null;
        credential.PasswordChangedAtUtc = clock.GetUtcNow();
        reset.ConsumedAtUtc = clock.GetUtcNow();
        await InvalidateAsync(user.Id, ct);
        await AuditAsync("identity.password-reset.complete", null, user.Id, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    private async Task InvalidateAsync(Guid userId, CancellationToken ct)
    {
        await sessions.RevokeUserAsync(userId, "password-reset", ct);
        foreach (var row in await db.Set<PasswordResetRequest>().Where(x => x.UserId == userId && x.RevokedAtUtc == null && x.ConsumedAtUtc == null).ToListAsync(ct))
            row.RevokedAtUtc = clock.GetUtcNow();
    }

    public async Task<IResult> ChangeAsync(PasswordChangeRequest request, HttpContext context, RequestSession current, CancellationToken ct)
    {
        if (current.Entity is not { } candidate) return Results.Unauthorized();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct);
        var user = await db.Set<IdentityUser>().FromSqlInterpolated($"SELECT * FROM users WHERE id={candidate.UserId} FOR UPDATE").SingleAsync(ct);
        var session = await db.Set<IdentitySession>().FromSqlInterpolated($"SELECT * FROM sessions WHERE id={candidate.Id} FOR UPDATE").SingleAsync(ct);
        var now = clock.GetUtcNow();
        if (!user.IsActive || session.SecurityVersion != user.SecurityVersion || session.RevokedAtUtc is not null
            || session.ExpiresAtUtc <= now || session.LastSeenAtUtc <= now.AddMinutes(-30)) return Results.Unauthorized();
        if (session.Stage is not (SessionStage.Active or SessionStage.PasswordChangeRequired)) return Results.Forbid();
        var credential = await db.Set<LocalCredential>().SingleAsync(x => x.UserId == user.Id, ct);
        if (!await hasher.VerifyAsync(request.CurrentPassword, credential.PasswordHash, ct) || request.CurrentPassword == request.NewPassword) return Invalid();
        string encoded;
        try { encoded = await hasher.HashAsync(request.NewPassword, ct); }
        catch (ArgumentException) { return Invalid(); }
        credential.PasswordHash = encoded;
        credential.MustChangePassword = false;
        credential.TemporaryExpiresAtUtc = credential.TemporaryConsumedAtUtc = null;
        credential.PasswordChangedAtUtc = now;
        await InvalidateAsync(user.Id, ct);
        await AuditAsync("identity.password.change", user.Id, user.Id, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        context.Response.Cookies.Delete(CsrfService.SessionCookieName, SessionAuthenticationHandler.CookieOptions());
        context.Response.Cookies.Delete(CsrfService.CookieName, SessionAuthenticationHandler.CookieOptions());
        return Results.NoContent();
    }

    public async Task<IResult> AdminResetAsync(Guid actorId, Guid targetId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct);
        if (!await guard.AllowsAsync(actorId, "users:manage", ct)) return Results.Problem(statusCode: 403, title: "ไม่มีสิทธิ์รีเซ็ตรหัสผ่าน");
        var user = await db.Set<IdentityUser>().FromSqlInterpolated($"SELECT * FROM users WHERE id={targetId} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (user is null || !user.IsActive) return Results.Problem(statusCode: 404, title: "ไม่พบบัญชีที่ใช้งานได้");
        var credential = await db.Set<LocalCredential>().SingleOrDefaultAsync(x => x.UserId == targetId, ct);
        if (credential is null) return Invalid();
        var temporary = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var now = clock.GetUtcNow();
        credential.PasswordHash = await hasher.HashAsync(temporary, ct);
        credential.MustChangePassword = true;
        credential.PasswordChangedAtUtc = now;
        credential.TemporaryExpiresAtUtc = now.AddMinutes(15);
        credential.TemporaryConsumedAtUtc = null;
        await InvalidateAsync(targetId, ct);
        await AuditAsync("identity.password-reset.admin", actorId, targetId, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        // Only this authenticated MFA-protected response contains the temporary credential. No email or log.
        return Results.Ok(new { temporaryPassword = temporary, expiresAtUtc = credential.TemporaryExpiresAtUtc });
    }

    private Task AuditAsync(string action, Guid? actor, Guid target, CancellationToken ct) => audit.WriteAsync(
        new SecurityAuditRequest(actor, actor is null ? null : permission.ActingRoleId, null, null, null, action, "user", target, "success", new Dictionary<string, string>()), ct);
}
