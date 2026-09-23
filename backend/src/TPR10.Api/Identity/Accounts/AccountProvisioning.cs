using TPR10.Api.Auditing;
using TPR10.Api.Data;
using Microsoft.EntityFrameworkCore;
using System.Net.Mail;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Passwords;
using TPR10.Api.Identity.Authorization;

namespace TPR10.Api.Identity.Accounts;

public sealed record CreateAccountRequest(string Username, string Password, string? Email = null, Guid[]? RoleIds = null);
public sealed record UpdateAccountRequest(bool? IsActive = null, Guid[]? RoleIds = null);
public sealed record AccountView(Guid Id, string Username, string? Email, bool IsActive, Guid[] RoleIds);
public sealed record AccountPage(AccountView[] Items, int Total, int Page, int PageSize);

// Use-case boundary: owns transaction/audit/Save. HTTP callers must also require the Task 6 MFA policy.
public sealed class AccountProvisioning(Tpr10DbContext db, IPasswordHasher passwords, ISessionService sessions, IAuditEventWriter audit, TimeProvider clock,
    Scopes.Assignments.AssignmentLifecycle assignments, PermissionMutationGuard? guard = null)
{
    public async Task<IResult> CreateAsync(Guid actorId, CreateAccountRequest request, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct);
        if (guard is not null && !await guard.AllowsAsync(actorId, "users:manage", ct)) return Denied();
        if (!await CanManageAsync(actorId, ct)) return Denied();
        if (request.RoleIds is not null && !await CanManageAsync(actorId, ct, "roles:manage"))
            return await DenyRoleAssignmentAsync(actorId, null, transaction, ct);
        var normalized = UsernameNormalizer.Normalize(request.Username);
        var roles = request.RoleIds ?? [IdentityCatalog.StaffRoleId];
        if (normalized is null || normalized.Any(char.IsControl) || !ValidEmail(request.Email) || !await ValidRolesAsync(roles, ct)) return Invalid();
        if (await db.Set<IdentityUser>().AnyAsync(x => x.NormalizedUsername == normalized, ct))
            return Results.Problem(statusCode: 409, title: "ชื่อผู้ใช้นี้ถูกใช้งานแล้ว");
        string hash;
        try { hash = await passwords.HashAsync(request.Password, ct); }
        catch (ArgumentException) { return Invalid(); }
        var now = clock.GetUtcNow();
        var user = new IdentityUser
        {
            Id = Guid.NewGuid(),
            Username = request.Username.Trim(),
            NormalizedUsername = normalized,
            Email = request.Email,
            CreatedAtUtc = now
        };
        db.Add(user);
        db.Add(new LocalCredential { UserId = user.Id, PasswordHash = hash, MustChangePassword = true, PasswordChangedAtUtc = now });
        foreach (var role in roles) db.Add(new UserRole { UserId = user.Id, RoleId = role, CreatedAtUtc = now });
        await WriteAuditAsync("identity.user.created", actorId, user.Id, "account,credential,roles", ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Created($"/api/v1/users/{user.Id}", View(user, roles));
    }

    public async Task<IResult> UpdateAsync(Guid actorId, Guid userId, UpdateAccountRequest request, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct);
        if (guard is not null && !await guard.AllowsAsync(actorId, "users:manage", ct)) return Denied();
        if (!await CanManageAsync(actorId, ct)) return Denied();
        if (request.RoleIds is not null && !await CanManageAsync(actorId, ct, "roles:manage"))
            return await DenyRoleAssignmentAsync(actorId, userId, transaction, ct);
        if ((request.IsActive is null && request.RoleIds is null) || (request.RoleIds is not null && !await ValidRolesAsync(request.RoleIds, ct))) return Invalid();
        var user = await db.Set<IdentityUser>().FromSqlInterpolated($"SELECT * FROM users WHERE id={userId} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (user is null) return Results.Problem(statusCode: 404, title: "ไม่พบบัญชีผู้ใช้");
        var mappings = await db.Set<UserRole>().Where(x => x.UserId == userId).ToListAsync(ct);
        var previous = mappings.Select(x => x.RoleId).ToArray();
        var roles = request.RoleIds ?? previous;
        var active = request.IsActive ?? user.IsActive;
        var wasAdmin = user.IsActive && await db.Set<IdentityRole>().AnyAsync(x => previous.Contains(x.Id) && x.RoleClass == "system-administration", ct);
        var remainsAdmin = active && await db.Set<IdentityRole>().AnyAsync(x => roles.Contains(x.Id) && x.RoleClass == "system-administration", ct);
        if (wasAdmin && !remainsAdmin && !await (from u in db.Set<IdentityUser>()
                                                 join ur in db.Set<UserRole>() on u.Id equals ur.UserId
                                                 join r in db.Set<IdentityRole>() on ur.RoleId equals r.Id
                                                 where u.IsActive && u.Id != userId && r.RoleClass == "system-administration"
                                                 select u.Id).AnyAsync(ct))
            return Results.Problem(statusCode: 409, title: "ไม่สามารถปิดหรือถอดผู้ดูแลที่ใช้งานอยู่คนสุดท้ายได้");
        var rolesChanged = !previous.ToHashSet().SetEquals(roles);
        if (active == user.IsActive && !rolesChanged) return Results.Ok(View(user, roles));
        var hadManagingAdmin = await PermissionMutationGuard.HasManagingAdminAsync(db, ct);
        user.IsActive = active;
        user.UpdatedAtUtc = clock.GetUtcNow();
        if (rolesChanged)
        {
            db.RemoveRange(mappings.Where(x => !roles.Contains(x.RoleId)));
            foreach (var role in roles.Except(previous)) db.Add(new UserRole { UserId = userId, RoleId = role, CreatedAtUtc = clock.GetUtcNow() });
        }
        await db.SaveChangesAsync(ct);
        if (hadManagingAdmin && !await PermissionMutationGuard.HasManagingAdminAsync(db, ct))
            return Results.Problem(statusCode: 409, title: "ไม่สามารถถอดสิทธิ์จัดการของผู้ดูแลคนสุดท้ายได้");
        if (!active) await assignments.RevokeForUserAsync(userId, actorId, "account-disabled", ct);
        await sessions.RevokeUserAsync(userId, "account-or-role-change", ct);
        await WriteAuditAsync("identity.user.updated", actorId, userId, rolesChanged ? "active,roles" : "active", ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(View(user, roles));
    }

    public async Task<IResult> ListAsync(Guid actorId, int page, int pageSize, CancellationToken ct)
    {
        if (guard is not null && !await guard.AllowsAsync(actorId, "users:manage", ct)) return Denied();
        if (!await CanManageAsync(actorId, ct)) return Denied();
        if (page < 1 || pageSize < 1) return Invalid();
        pageSize = Math.Min(pageSize, 100);
        var offset = ((long)page - 1) * pageSize;
        if (offset > int.MaxValue) return Invalid();
        var total = await db.Set<IdentityUser>().CountAsync(ct);
        var rows = await db.Set<IdentityUser>().AsNoTracking().OrderBy(x => x.NormalizedUsername).ThenBy(x => x.Id)
            .Skip((int)offset).Take(pageSize).Select(x => new { x.Id, x.Username, x.Email, x.IsActive }).ToArrayAsync(ct);
        var ids = rows.Select(x => x.Id).ToArray();
        var mappings = await db.Set<UserRole>().AsNoTracking().Where(x => ids.Contains(x.UserId)).ToArrayAsync(ct);
        var items = rows.Select(x => new AccountView(x.Id, x.Username, x.Email, x.IsActive,
            mappings.Where(r => r.UserId == x.Id).Select(r => r.RoleId).OrderBy(id => id).ToArray())).ToArray();
        return Results.Ok(new AccountPage(items, total, page, pageSize));
    }

    private Task<bool> CanManageAsync(Guid actorId, CancellationToken ct, string capability = "users:manage") => (from user in db.Set<IdentityUser>()
                                                                                                                  join ur in db.Set<UserRole>() on user.Id equals ur.UserId
                                                                                                                  join rp in db.Set<RolePermission>() on ur.RoleId equals rp.RoleId
                                                                                                                  join p in db.Set<IdentityPermission>() on rp.PermissionId equals p.Id
                                                                                                                  where user.Id == actorId && user.IsActive && p.Capability == capability && p.Domain == "system"
                                                                                                                  select user.Id).AnyAsync(ct);

    private async Task<bool> ValidRolesAsync(Guid[] roles, CancellationToken ct) => roles.Length <= 20
        && roles.Distinct().Count() == roles.Length && await db.Set<IdentityRole>().CountAsync(x => roles.Contains(x.Id), ct) == roles.Length;
    private static bool ValidEmail(string? email) => email is null || email.Length <= 320
        && MailAddress.TryCreate(email, out var parsed) && parsed.Address == email;
    private static AccountView View(IdentityUser user, Guid[] roles) => new(user.Id, user.Username, user.Email, user.IsActive, roles.OrderBy(x => x).ToArray());
    private static IResult Denied() => Results.Problem(statusCode: 403, title: "ไม่มีสิทธิ์จัดการบัญชีผู้ใช้");
    private async Task<IResult> DenyRoleAssignmentAsync(Guid actorId, Guid? targetId,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction, CancellationToken ct)
    {
        // Both callers invoke this before any business mutation; commit only the denial audit.
        await audit.WriteAsync(new SecurityAuditRequest(actorId, null, null, null, null,
            "identity.authorization.denied", "user", targetId, "denied", new Dictionary<string, string>
            { ["reason"] = "permission-denied", ["capability"] = "roles:manage" }), ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Denied();
    }
    private static IResult Invalid() => Results.Problem(statusCode: 400, title: "ข้อมูลบัญชีผู้ใช้ไม่ถูกต้อง");
    private async Task WriteAuditAsync(string action, Guid actorId, Guid targetId, string changes, CancellationToken ct)
    {
        var actorRoles = await db.Set<UserRole>().Where(x => x.UserId == actorId).Select(x => x.RoleId).OrderBy(x => x).ToArrayAsync(ct);
        await audit.WriteAsync(action, targetId, new Dictionary<string, string>
        {
            ["outcome"] = "success",
            ["scope"] = "system",
            ["target-type"] = "user",
            ["changed-fields"] = changes,
            ["acting-roles"] = string.Join(",", actorRoles)
        }, ct, actorId);
    }
}
