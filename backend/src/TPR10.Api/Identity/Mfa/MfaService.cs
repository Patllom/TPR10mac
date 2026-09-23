using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using OtpNet;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.Identity.Csrf;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Sessions;

namespace TPR10.Api.Identity.Mfa;

public sealed class MfaService(Tpr10DbContext db, ISessionService sessions, RequestSession current,
    IDataProtectionProvider protection, IOptions<CsrfOptions> options, IAuditEventWriter audit, TimeProvider clock)
{
    private IDataProtector Protector(Guid userId, Guid factorId) => protection.CreateProtector("TPR10.Mfa.Secret.v1", userId.ToString("D"), factorId.ToString("D"));

    public async Task<IResult> EnrollAsync(HttpContext context, CancellationToken ct)
    {
        if (options.Value.KeyRingPath is null) return Unavailable();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var session = await LockSessionAsync(context, ct);
        if (session is null) return Unauthorized();
        if (session.Stage is not (SessionStage.MfaEnrollmentRequired or SessionStage.Active)) return Forbidden();
        if (clock.GetUtcNow() >= session.CreatedAtUtc.AddMinutes(10)) return Unauthorized();
        var pendingCutoff = clock.GetUtcNow().AddMinutes(-10);
        await db.Set<MfaFactor>().Where(x => x.UserId == session.UserId && x.ConfirmedAtUtc == null
            && x.RevokedAtUtc == null && x.CreatedAtUtc <= pendingCutoff)
            .ExecuteUpdateAsync(x => x.SetProperty(f => f.RevokedAtUtc, clock.GetUtcNow()), ct);
        if (await db.Set<MfaFactor>().AnyAsync(x => x.UserId == session.UserId && x.RevokedAtUtc == null, ct))
            return Results.Problem(statusCode: 409, title: "มีการตั้งค่า MFA อยู่แล้ว");
        var now = clock.GetUtcNow();
        var secret = Base32Encoding.ToString(RandomNumberGenerator.GetBytes(20));
        var factorId = Guid.NewGuid();
        db.Add(new MfaFactor
        {
            Id = factorId,
            UserId = session.UserId,
            EnrollmentSessionId = session.Id,
            ProtectedSecret = Protector(session.UserId, factorId).Protect(secret),
            CreatedAtUtc = now
        });
        await AuditAsync("identity.mfa.enroll", session.UserId, "success", ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new { provisioningUri = $"otpauth://totp/TPR10:{session.UserId:D}?secret={secret}&issuer=TPR10&algorithm=SHA1&digits=6&period=30" });
    }

    public async Task<IResult> ConfirmAsync(string code, HttpContext context, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var session = await LockSessionAsync(context, ct);
        if (session is null) return Unauthorized();
        if (session.Stage is not (SessionStage.MfaEnrollmentRequired or SessionStage.Active)) return Forbidden();
        if (clock.GetUtcNow() >= session.CreatedAtUtc.AddMinutes(10)) return Unauthorized();
        var attempts = await AttemptsAsync(session.UserId, ct);
        if (Locked(attempts, context)) return Limited();
        var factor = await db.Set<MfaFactor>().AsNoTracking().SingleOrDefaultAsync(x => x.UserId == session.UserId
            && x.RevokedAtUtc == null && x.ConfirmedAtUtc == null && x.EnrollmentSessionId == session.Id, ct);
        if (factor is null || clock.GetUtcNow() >= factor.CreatedAtUtc.AddMinutes(10)) return Forbidden();
        try { if (!await ConsumeTotpAsync(factor, code, ct)) return await FailedAsync(attempts, tx, ct); }
        catch (CryptographicException) { return Unavailable(); }
        var now = clock.GetUtcNow();
        // Rotate while the old optional-MFA Active session is still valid, then activate the factor atomically.
        var issued = await sessions.RotateAsync(context.Request.Cookies[CsrfService.SessionCookieName]!, SessionStage.Active, now, ct);
        await db.Set<MfaFactor>().Where(x => x.Id == factor.Id).ExecuteUpdateAsync(x => x.SetProperty(f => f.ConfirmedAtUtc, now), ct);
        var codes = Enumerable.Range(0, 10).Select(_ => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(16))).ToArray();
        foreach (var recovery in codes) db.Add(new MfaRecoveryCode
        {
            Id = Guid.NewGuid(),
            UserId = session.UserId,
            CodeHash = SHA256.HashData(WebEncoders.Base64UrlDecode(recovery)),
            CreatedAtUtc = now
        });
        Reset(attempts);
        await AuditAsync("identity.mfa.confirm", session.UserId, "success", ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        SetCookie(context, issued);
        return Results.Ok(new { session = issued.View, recoveryCodes = codes });
    }

    public async Task<IResult> ChallengeAsync(string code, HttpContext context, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var session = await LockSessionAsync(context, ct);
        if (session is null || ExpiredChallenge(session)) return Unauthorized();
        if (session.Stage is not (SessionStage.MfaChallengeRequired or SessionStage.Active)) return Forbidden();
        var attempts = await AttemptsAsync(session.UserId, ct);
        if (Locked(attempts, context)) return Limited();
        try { if (!await VerifyTotpAsync(session.UserId, code, ct)) return await FailedAsync(attempts, tx, ct); }
        catch (CryptographicException) { return Unavailable(); }
        var issued = await sessions.RotateAsync(context.Request.Cookies[CsrfService.SessionCookieName]!, SessionStage.Active, clock.GetUtcNow(), ct);
        Reset(attempts);
        await AuditAsync("identity.mfa.challenge", session.UserId, "success", ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        SetCookie(context, issued);
        return Results.Ok(new { session = issued.View });
    }

    public async Task<IResult> RecoverAsync(string code, HttpContext context, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var session = await LockSessionAsync(context, ct);
        if (session is null || ExpiredChallenge(session)) return Unauthorized();
        if (session.Stage is not (SessionStage.MfaChallengeRequired or SessionStage.Active)) return Forbidden();
        var attempts = await AttemptsAsync(session.UserId, ct);
        if (Locked(attempts, context)) return Limited();
        var hash = RecoveryHash(code);
        var now = clock.GetUtcNow();
        if (hash is null || await db.Set<MfaRecoveryCode>().Where(x => x.UserId == session.UserId && x.CodeHash == hash
                && x.ConsumedAtUtc == null && x.RevokedAtUtc == null)
            .ExecuteUpdateAsync(x => x.SetProperty(r => r.ConsumedAtUtc, now), ct) != 1)
            return await FailedAsync(attempts, tx, ct);
        await RevokeFactorsAsync(session.UserId, ct);
        await sessions.RevokeUserAsync(session.UserId, "mfa-recovery", ct);
        Reset(attempts);
        await db.SaveChangesAsync(ct);
        var issued = await sessions.IssueAsync(session.UserId, SessionStage.MfaEnrollmentRequired, ct);
        // Recovery starts a new enrollment deadline, never extends the original absolute login expiry.
        db.Set<IdentitySession>().Local.Single(x => db.Entry(x).State == EntityState.Added).ExpiresAtUtc = session.ExpiresAtUtc;
        await AuditAsync("identity.mfa.recovery", session.UserId, "success", ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        SetCookie(context, issued);
        return Results.Ok(new { session = issued.View });
    }

    private async Task<int> RevokeFactorsAsync(Guid userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await db.Set<MfaFactor>().Where(x => x.UserId == userId && x.RevokedAtUtc == null).ExecuteUpdateAsync(x => x.SetProperty(f => f.RevokedAtUtc, now), ct);
        return await db.Set<MfaRecoveryCode>().Where(x => x.UserId == userId && x.RevokedAtUtc == null).ExecuteUpdateAsync(x => x.SetProperty(r => r.RevokedAtUtc, now), ct);
    }
    private bool ExpiredChallenge(IdentitySession session) => session.Stage != SessionStage.Active && clock.GetUtcNow() >= session.CreatedAtUtc.AddMinutes(10);
    private static byte[]? RecoveryHash(string? code)
    {
        if (code is not { Length: 22 }) return null;
        try
        {
            var raw = WebEncoders.Base64UrlDecode(code);
            return raw.Length == 16 && WebEncoders.Base64UrlEncode(raw) == code ? SHA256.HashData(raw) : null;
        }
        catch (FormatException) { return null; }
    }
    private async Task<MfaAttemptState> AttemptsAsync(Guid userId, CancellationToken ct)
    {
        var state = await db.Set<MfaAttemptState>().SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if (state is null) { state = new MfaAttemptState { UserId = userId, WindowStartedAtUtc = clock.GetUtcNow() }; db.Add(state); }
        if (state.LockedUntilUtc <= clock.GetUtcNow() || state.LockedUntilUtc is null && state.WindowStartedAtUtc.AddMinutes(15) <= clock.GetUtcNow()) Reset(state);
        return state;
    }
    private void Reset(MfaAttemptState state) { state.FailedAttempts = 0; state.LockedUntilUtc = null; state.WindowStartedAtUtc = clock.GetUtcNow(); }
    private bool Locked(MfaAttemptState state, HttpContext context)
    {
        if (state.LockedUntilUtc is not { } until || until <= clock.GetUtcNow()) return false;
        context.Response.Headers.RetryAfter = Math.Ceiling((until - clock.GetUtcNow()).TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }
    private async Task<IResult> FailedAsync(MfaAttemptState state, IDbContextTransaction tx, CancellationToken ct)
    {
        state.FailedAttempts++;
        if (state.FailedAttempts >= 5) state.LockedUntilUtc = clock.GetUtcNow().AddMinutes(15);
        await AuditAsync("identity.mfa.denied", state.UserId, "denied", ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Forbidden();
    }

    // Caller owns the transaction and stage/proof guard; consuming a step alone is not assurance.
    public async Task<bool> VerifyTotpAsync(Guid userId, string code, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("TOTP consumption requires a caller transaction.");
        var factor = await db.Set<MfaFactor>().AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId && x.RevokedAtUtc == null && x.ConfirmedAtUtc != null, ct);
        return factor is not null && await ConsumeTotpAsync(factor, code, ct);
    }

    private async Task<bool> ConsumeTotpAsync(MfaFactor factor, string code, CancellationToken ct)
    {
        if (code is not { Length: 6 } || code.Any(x => x is < '0' or > '9')) return false;
        var secret = Base32Encoding.ToBytes(Protector(factor.UserId, factor.Id).Unprotect(factor.ProtectedSecret));
        try
        {
            var valid = new Totp(secret, step: 30, mode: OtpHashMode.Sha1, totpSize: 6)
                .VerifyTotp(clock.GetUtcNow().UtcDateTime, code, out var step, new VerificationWindow(1, 1));
            return valid && await db.Set<MfaFactor>().Where(x => x.Id == factor.Id && x.RevokedAtUtc == null
                && (x.LastUsedStep == null || x.LastUsedStep < step)).ExecuteUpdateAsync(x => x.SetProperty(f => f.LastUsedStep, step), ct) == 1;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    private async Task<IdentitySession?> LockSessionAsync(HttpContext context, CancellationToken ct)
    {
        var token = context.Request.Cookies[CsrfService.SessionCookieName];
        var hash = SessionService.HashToken(token);
        if (hash is null || current.Entity is null) return null;
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct);
        var userId = current.Entity.UserId;
        await db.Set<IdentityUser>().FromSqlInterpolated($"SELECT * FROM users WHERE id={userId} FOR UPDATE").SingleAsync(ct);
        var session = await db.Set<IdentitySession>().FromSqlInterpolated($"SELECT * FROM sessions WHERE token_hash={hash} FOR UPDATE").SingleOrDefaultAsync(ct);
        return session is not null && await sessions.ValidateAsync(token!, ct) is not null ? session : null;
    }
    private Task AuditAsync(string action, Guid userId, string outcome, CancellationToken ct) => audit.WriteAsync(action, userId,
        new Dictionary<string, string> { ["outcome"] = outcome, ["scope"] = "system", ["target-type"] = "user" }, ct, userId);
    private static void SetCookie(HttpContext context, IssuedSession issued)
    {
        context.Response.Cookies.Append(CsrfService.SessionCookieName, issued.Token, SessionAuthenticationHandler.CookieOptions());
        context.Response.Cookies.Delete(CsrfService.CookieName, SessionAuthenticationHandler.CookieOptions());
    }
    private static IResult Forbidden() => Results.Problem(statusCode: 403, title: "ไม่สามารถทำรายการ MFA นี้ได้");
    private static IResult Unauthorized() => Results.Problem(statusCode: 401, title: "กรุณาเข้าสู่ระบบใหม่");
    private static IResult Unavailable() => Results.Problem(statusCode: 503, title: "ระบบ MFA ไม่พร้อมใช้งาน กรุณาติดต่อผู้ดูแล");
    private static IResult Limited() => Results.Problem(statusCode: 429, title: "รหัส MFA ไม่ถูกต้องหลายครั้ง กรุณาลองใหม่ภายหลัง");
}
