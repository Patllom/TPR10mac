using Microsoft.EntityFrameworkCore;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Passwords;

namespace TPR10.Api.Identity.Accounts;

public sealed class BootstrapService(Tpr10DbContext db, IPasswordHasher passwords, IAuditEventWriter audit, TimeProvider clock)
{
    public async Task<int> CreateAsync(string username, string password, CancellationToken ct)
    {
        var normalized = UsernameNormalizer.Normalize(username);
        if (normalized is null || normalized.Any(char.IsControl)) return 1;
        string hash;
        try { hash = await passwords.HashAsync(password, ct); }
        catch (ArgumentException) { return 1; }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct);
        if (await db.Set<IdentityUser>().AnyAsync(ct)) return 2;
        var now = clock.GetUtcNow();
        await IdentityCatalog.SeedAsync(db, now, ct);
        var id = Guid.NewGuid();
        db.Add(new IdentityUser { Id = id, Username = username.Trim(), NormalizedUsername = normalized, CreatedAtUtc = now });
        db.Add(new LocalCredential { UserId = id, PasswordHash = hash, MustChangePassword = false, PasswordChangedAtUtc = now });
        db.Add(new UserRole { UserId = id, RoleId = IdentityCatalog.AdministratorRoleId, CreatedAtUtc = now });
        await audit.WriteAsync("identity.bootstrap", id, new Dictionary<string, string>
        { ["outcome"] = "success", ["actor-type"] = "local-operator", ["scope"] = "system", ["target-type"] = "user" }, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return 0;
    }
}
