using Microsoft.EntityFrameworkCore;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.Identity.Data;

namespace TPR10.Api.Identity.Mfa;

// Trusted use-case only. Task 6 must bind actorSessionId from the authenticated request, never from JSON.
public sealed class OperatorMfaRecovery(Tpr10DbContext db, ISessionService sessions, IAuditEventWriter audit, TimeProvider clock)
{
    public async Task<IResult> RecoverAsync(Guid actorSessionId, Guid targetId, string reason, string evidenceReference, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct);
        var candidate = await db.Set<IdentitySession>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == actorSessionId, ct);
        if (candidate is null || candidate.UserId == targetId) return Denied();
        var actorId = candidate.UserId;
        var actor = await db.Set<IdentityUser>().FromSqlInterpolated($"SELECT * FROM users WHERE id={actorId} FOR UPDATE").SingleAsync(ct);
        var session = await db.Set<IdentitySession>().FromSqlInterpolated($"SELECT * FROM sessions WHERE id={actorSessionId} FOR UPDATE").SingleAsync(ct);
        var now = clock.GetUtcNow();
        if (!actor.IsActive || session.SecurityVersion != actor.SecurityVersion || session.RevokedAtUtc != null
            || session.Stage != SessionStage.Active || session.ExpiresAtUtc <= now || session.LastSeenAtUtc <= now.AddMinutes(-30)
            || session.MfaVerifiedAtUtc is not { } verified || verified > now || verified <= now.AddMinutes(-15)
            || await db.Set<LocalCredential>().AnyAsync(x => x.UserId == actorId && x.MustChangePassword, ct)
            || !await db.Set<MfaFactor>().AnyAsync(x => x.UserId == actorId && x.ConfirmedAtUtc != null && x.RevokedAtUtc == null, ct)
            || !await (from ur in db.Set<UserRole>()
                       join rp in db.Set<RolePermission>() on ur.RoleId equals rp.RoleId
                       join p in db.Set<IdentityPermission>() on rp.PermissionId equals p.Id
                       where ur.UserId == actorId && p.Capability == "users:recover-mfa"
                       select p.Id).AnyAsync(ct)) return Denied();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500 || reason.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(evidenceReference) || evidenceReference.Length > 120 || evidenceReference.Any(char.IsControl))
            return Results.Problem(statusCode: 400, title: "ต้องระบุเหตุผลและเลขอ้างอิงหลักฐานยืนยันตัวบุคคล");
        var target = await db.Set<IdentityUser>().FromSqlInterpolated($"SELECT * FROM users WHERE id={targetId} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (target is null) return Results.Problem(statusCode: 404, title: "ไม่พบบัญชีผู้ใช้");
        await db.Set<MfaFactor>().Where(x => x.UserId == targetId && x.RevokedAtUtc == null).ExecuteUpdateAsync(x => x.SetProperty(f => f.RevokedAtUtc, now), ct);
        await db.Set<MfaRecoveryCode>().Where(x => x.UserId == targetId && x.RevokedAtUtc == null).ExecuteUpdateAsync(x => x.SetProperty(r => r.RevokedAtUtc, now), ct);
        await sessions.RevokeUserAsync(targetId, "operator-mfa-recovery", ct);
        await audit.WriteAsync("identity.mfa.operator-recovery", targetId, new Dictionary<string, string>
        {
            ["outcome"] = "success",
            ["scope"] = "system",
            ["target-type"] = "user",
            ["reason"] = reason.Trim(),
            ["evidence-reference"] = evidenceReference.Trim()
        }, ct, actorId);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }
    private static IResult Denied() => Results.Problem(statusCode: 403, title: "ไม่มีสิทธิ์กู้ MFA หรือการยืนยันตัวตนไม่เพียงพอ");
}
