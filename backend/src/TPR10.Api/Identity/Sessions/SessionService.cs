using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.Identity.Data;

namespace TPR10.Api.Identity.Sessions;

public sealed class RequestSession
{
    public IdentitySession? Entity { get; set; }
    public SessionView? View { get; set; }
}

public sealed class SessionService(Tpr10DbContext db, TimeProvider clock, RequestSession current) : ISessionService
{
    public static byte[]? HashToken(string? token)
    {
        if (token is not { Length: 43 }) return null;
        try
        {
            var raw = WebEncoders.Base64UrlDecode(token);
            return raw.Length == 32 && WebEncoders.Base64UrlEncode(raw) == token ? SHA256.HashData(raw) : null;
        }
        catch (FormatException) { return null; }
    }

    public async Task<IssuedSession> IssueAsync(Guid userId, SessionStage stage, CancellationToken ct)
    {
        var user = await db.Set<IdentityUser>().SingleAsync(x => x.Id == userId && x.IsActive, ct);
        var raw = RandomNumberGenerator.GetBytes(32);
        var now = clock.GetUtcNow();
        var session = new IdentitySession
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = SHA256.HashData(raw),
            Stage = stage,
            SecurityVersion = user.SecurityVersion,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(8)
        };
        db.Add(session);
        return new IssuedSession(WebEncoders.Base64UrlEncode(raw), await ViewAsync(session, ct));
    }

    public async Task<SessionView?> ValidateAsync(string token, CancellationToken ct)
    {
        var hash = HashToken(token);
        if (hash is null) return null;
        var now = clock.GetUtcNow();
        var idle = now.AddMinutes(-30);
        var session = await db.Set<IdentitySession>().AsNoTracking().SingleOrDefaultAsync(x => x.TokenHash == hash
            && x.RevokedAtUtc == null && x.ExpiresAtUtc > now && x.LastSeenAtUtc > idle
            && db.Set<IdentityUser>().Any(u => u.Id == x.UserId && u.IsActive && u.SecurityVersion == x.SecurityVersion), ct);
        if (session is null) return null;
        if (session.Stage != SessionStage.PasswordChangeRequired && await db.Set<LocalCredential>()
            .AnyAsync(x => x.UserId == session.UserId && x.MustChangePassword, ct)) return null;
        if (session.Stage == SessionStage.Active && session.MfaVerifiedAtUtc is null)
        {
            var confirmed = await db.Set<MfaFactor>().AnyAsync(x => x.UserId == session.UserId && x.ConfirmedAtUtc != null && x.RevokedAtUtc == null, ct);
            var mandatory = await (from ur in db.Set<UserRole>()
                                   join r in db.Set<IdentityRole>() on ur.RoleId equals r.Id
                                   where ur.UserId == session.UserId && r.RoleClass != "staff"
                                   select r.Id).AnyAsync(ct);
            if (confirmed || mandatory) return null;
        }
        current.Entity = session;
        current.View = await ViewAsync(session, ct);
        return current.View;
    }

    private async Task<SessionView> ViewAsync(IdentitySession session, CancellationToken ct)
    {
        var permissions = session.Stage != SessionStage.Active ? [] : await (
            from ur in db.Set<UserRole>()
            join rp in db.Set<RolePermission>() on ur.RoleId equals rp.RoleId
            join p in db.Set<IdentityPermission>() on rp.PermissionId equals p.Id
            where ur.UserId == session.UserId
            select p.Capability).Distinct().OrderBy(x => x).ToArrayAsync(ct);
        return new SessionView(session.UserId, session.Stage, permissions, session.MfaVerifiedAtUtc);
    }

    public async Task RevokeUserAsync(Guid userId, string reason, CancellationToken ct)
    {
        // Invalidate even a concurrent issuance missing from the session row snapshot.
        var user = await db.Set<IdentityUser>().SingleAsync(x => x.Id == userId, ct);
        user.SecurityVersion = checked(user.SecurityVersion + 1);
        foreach (var session in await db.Set<IdentitySession>().Where(x => x.UserId == userId && x.RevokedAtUtc == null).ToListAsync(ct))
            session.RevokedAtUtc = clock.GetUtcNow();
    }

    // Internal use-case API: caller verifies the transition/proof, owns audit and commits atomically.
    public async Task<IssuedSession> RotateAsync(string token, SessionStage stage, DateTimeOffset? mfaVerifiedAtUtc, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Rotation requires a caller transaction.");
        var hash = HashToken(token) ?? throw new InvalidOperationException("Invalid session.");
        var old = await db.Set<IdentitySession>().FromSqlInterpolated($"SELECT * FROM sessions WHERE token_hash={hash} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (old is null || await ValidateAsync(token, ct) is null) throw new InvalidOperationException("Invalid session.");
        var now = clock.GetUtcNow();
        if (mfaVerifiedAtUtc > now) throw new ArgumentOutOfRangeException(nameof(mfaVerifiedAtUtc));
        var raw = RandomNumberGenerator.GetBytes(32);
        var next = new IdentitySession
        {
            Id = Guid.NewGuid(),
            UserId = old.UserId,
            TokenHash = SHA256.HashData(raw),
            Stage = stage,
            SecurityVersion = old.SecurityVersion,
            CreatedAtUtc = old.CreatedAtUtc,
            LastSeenAtUtc = now,
            ExpiresAtUtc = old.ExpiresAtUtc,
            MfaVerifiedAtUtc = mfaVerifiedAtUtc
        };
        old.RevokedAtUtc = now;
        db.Add(next);
        return new IssuedSession(WebEncoders.Base64UrlEncode(raw), await ViewAsync(next, ct));
    }
}
