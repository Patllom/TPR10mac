using TPR10.Api.Data;
using TPR10.Api.Identity.Data;
using Microsoft.EntityFrameworkCore;

namespace TPR10.Api.Identity.Passwords;

public sealed class LocalIdentityProvider(Tpr10DbContext db, IPasswordHasher hasher, TimeProvider clock) : IIdentityProvider
{
    // ค่าทดสอบสาธารณะเพื่อให้ unknown user ยังผ่านงาน Argon2 เท่ากัน ไม่ใช่ credential ของบัญชีใด
    private const string DummyHash = "$argon2id$v=19$m=65536,t=3,p=1$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    public async Task<PasswordCheck?> VerifyAsync(string username, string password, CancellationToken ct)
    {
        var normalized = UsernameNormalizer.Normalize(username);
        var account = await (from user in db.Set<IdentityUser>().AsNoTracking()
                             join credential in db.Set<LocalCredential>().AsNoTracking() on user.Id equals credential.UserId
                             where user.NormalizedUsername == normalized
                             select new
                             {
                                 user.Id,
                                 user.IsActive,
                                 credential.PasswordHash,
                                 credential.MustChangePassword,
                                 credential.LockedUntilUtc,
                                 credential.TemporaryExpiresAtUtc,
                                 credential.TemporaryConsumedAtUtc
                             }).SingleOrDefaultAsync(ct);
        var matches = await hasher.VerifyAsync(password, account?.PasswordHash ?? DummyHash, ct);
        if (!matches || account is null || !account.IsActive || account.LockedUntilUtc > clock.GetUtcNow()
            || account.TemporaryConsumedAtUtc is not null || account.TemporaryExpiresAtUtc <= clock.GetUtcNow()) return null;
        return new PasswordCheck(account.Id, account.MustChangePassword);
    }
}
