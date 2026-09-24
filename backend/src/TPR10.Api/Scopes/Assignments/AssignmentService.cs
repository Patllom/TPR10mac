using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.Identity;
using TPR10.Api.Identity.Authorization;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Organization;
using TPR10.Api.Organization.Data;
using TPR10.Api.Scopes.Data;

namespace TPR10.Api.Scopes.Assignments;

public sealed class AssignmentService(Tpr10DbContext db, RequestSession current, PermissionMutationGuard guard,
    PermissionContext permission, ISessionService sessions, IAuditEventWriter audit, TimeProvider clock)
{
    private static readonly string[] BusinessClasses = ["staff", "approval", "accounting", "finance-data-access"];
    private Guid Actor => current.Entity?.UserId ?? Guid.Empty;

    public async Task<IResult> GrantAsync(Guid actorId, GrantAssignment request, CancellationToken ct)
    {
        await using var tx = await BeginAsync(ct);
        if (await AuthorizeAsync(actorId, tx, ct) is { } denied) return denied;
        if (request.UserId == actorId) return await DenyAsync(403, "self-assignment", request.Scope, null, tx, ct);
        if (request.UserId == Guid.Empty || !Valid(request.Scope, request.RoleId, request.Reason)) return await DenyAsync(400, "invalid-request", request.Scope, null, tx, ct);
        if (await ValidateTargetAsync(request.UserId, request.Scope, request.RoleId, ct) is { } status) return await DenyAsync(status, "target-unavailable", request.Scope, null, tx, ct);
        if (await DuplicateAsync(request.UserId, request.Scope, request.RoleId, ct)) return await DenyAsync(409, "duplicate-assignment", request.Scope, null, tx, ct);
        var row = New(request.UserId, request.Scope, request.RoleId, request.Reason, actorId);
        db.Add(row);
        await WriteAsync("scope.assignment.granted", row, request.Reason, ct);
        await sessions.RevokeUserAsync(row.UserId, "assignment-granted", ct);
        await CommitAsync(tx, ct);
        return Results.Created($"/api/v1/scope-assignments?userId={row.UserId}", View(row));
    }

    public async Task<IResult> ReplaceAsync(Guid actorId, Guid assignmentId, ReplaceAssignment request, CancellationToken ct)
    {
        await using var tx = await BeginAsync(ct);
        if (await AuthorizeAsync(actorId, tx, ct) is { } denied) return denied;
        if (assignmentId == Guid.Empty || request.ExpectedVersion < 1 || !Valid(request.Scope, request.RoleId, request.Reason))
            return await DenyAsync(400, "invalid-request", request.Scope, assignmentId, tx, ct);
        var old = await db.Set<ScopeAssignment>().SingleOrDefaultAsync(x => x.Id == assignmentId, ct);
        if (old is null) return await DenyAsync(404, "assignment-unavailable", request.Scope, assignmentId, tx, ct);
        if (old.UserId == actorId) return await DenyAsync(403, "self-assignment", Key(old), assignmentId, tx, ct);
        if (old.RevokedAtUtc != null || old.Version != request.ExpectedVersion || old.Version == long.MaxValue)
            return await DenyAsync(409, "version-conflict", Key(old), assignmentId, tx, ct);
        if (await ValidateTargetAsync(old.UserId, request.Scope, request.RoleId, ct) is { } status) return await DenyAsync(status, "target-unavailable", request.Scope, assignmentId, tx, ct);
        if (Key(old) == request.Scope && old.RoleId == request.RoleId) { await tx.CommitAsync(ct); return Results.Ok(View(old)); }
        if (await DuplicateAsync(old.UserId, request.Scope, request.RoleId, ct)) return await DenyAsync(409, "duplicate-assignment", request.Scope, assignmentId, tx, ct);
        await RevokeRowAsync(old, actorId, request.Reason, ct);
        var row = New(old.UserId, request.Scope, request.RoleId, request.Reason, actorId);
        db.Add(row);
        await WriteAsync("scope.assignment.granted", row, request.Reason, ct);
        await WriteAsync("scope.assignment.replaced", old, request.Reason, ct, row.Id);
        await sessions.RevokeUserAsync(old.UserId, "assignment-replaced", ct);
        await CommitAsync(tx, ct);
        return Results.Ok(View(row));
    }

    public async Task<IResult> RevokeAsync(Guid actorId, Guid assignmentId, ExpectedChange request, CancellationToken ct)
    {
        await using var tx = await BeginAsync(ct);
        if (await AuthorizeAsync(actorId, tx, ct) is { } denied) return denied;
        if (assignmentId == Guid.Empty || request.ExpectedVersion < 1 || !OrganizationValidation.Text(request.Reason, 500))
            return await DenyAsync(400, "invalid-request", null, assignmentId, tx, ct);
        var row = await db.Set<ScopeAssignment>().SingleOrDefaultAsync(x => x.Id == assignmentId, ct);
        if (row is null) return await DenyAsync(404, "assignment-unavailable", null, assignmentId, tx, ct);
        if (row.UserId == actorId) return await DenyAsync(403, "self-assignment", Key(row), assignmentId, tx, ct);
        if (row.RevokedAtUtc != null || row.Version != request.ExpectedVersion || row.Version == long.MaxValue)
            return await DenyAsync(409, "version-conflict", Key(row), assignmentId, tx, ct);
        await RevokeRowAsync(row, actorId, request.Reason, ct);
        await sessions.RevokeUserAsync(row.UserId, "assignment-revoked", ct);
        await CommitAsync(tx, ct);
        return Results.Ok(View(row));
    }

    public async Task<IResult> ListAsync(Guid? userId, ScopeKey? scope, bool? revoked, int page, int pageSize, CancellationToken ct)
    {
        await using var tx = await BeginAsync(ct);
        if (await AuthorizeAsync(Actor, tx, ct) is { } denied) return denied;
        if (userId == Guid.Empty || scope is { IsValid: false } || !Pagination(page, ref pageSize, out var offset))
            return await DenyAsync(400, "invalid-request", scope, null, tx, ct);
        var query = db.Set<ScopeAssignment>().AsNoTracking().AsQueryable();
        if (userId is { } user) query = query.Where(x => x.UserId == user);
        if (scope is not null) query = query.Where(x => x.WorkspaceId == scope.WorkspaceId && x.ProjectId == scope.ProjectId && x.SiteId == scope.SiteId);
        if (revoked is { } history) query = query.Where(x => (x.RevokedAtUtc != null) == history);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).Skip(offset).Take(pageSize).ToArrayAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new Page<AssignmentView>(rows.Select(View).ToArray(), total, page, pageSize));
    }

    public async Task<IResult> UsersAsync(string? prefix, int page, int pageSize, CancellationToken ct)
    {
        await using var tx = await BeginAsync(ct);
        if (await AuthorizeAsync(Actor, tx, ct) is { } denied) return denied;
        if (!Prefix(prefix) || !Pagination(page, ref pageSize, out var offset)) return await DenyAsync(400, "invalid-request", null, null, tx, ct);
        var search = (prefix ?? "").Trim().ToUpperInvariant();
        var actor = Actor;
        var query = db.Set<IdentityUser>().AsNoTracking().Where(x => x.Id != actor && x.NormalizedUsername.StartsWith(search));
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(x => x.NormalizedUsername).ThenBy(x => x.Id).Skip(offset).Take(pageSize)
            .Select(x => new AssignmentUserOption(x.Id, x.Username, x.IsActive)).ToArrayAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new Page<AssignmentUserOption>(rows, total, page, pageSize));
    }

    public async Task<IResult> RolesAsync(string? prefix, int page, int pageSize, CancellationToken ct)
    {
        await using var tx = await BeginAsync(ct);
        if (await AuthorizeAsync(Actor, tx, ct) is { } denied) return denied;
        if (!Prefix(prefix) || !Pagination(page, ref pageSize, out var offset)) return await DenyAsync(400, "invalid-request", null, null, tx, ct);
        var search = (prefix ?? "").Trim();
        var query = db.Set<IdentityRole>().AsNoTracking().Where(x => BusinessClasses.Contains(x.RoleClass) && x.Name.StartsWith(search));
        var total = await query.CountAsync(ct);
        var roles = await query.OrderBy(x => x.Name).ThenBy(x => x.Id).Skip(offset).Take(pageSize).ToArrayAsync(ct);
        var ids = roles.Select(x => x.Id).ToArray();
        var grants = await (from rp in db.Set<RolePermission>()
                            join p in db.Set<IdentityPermission>() on rp.PermissionId equals p.Id
                            where ids.Contains(rp.RoleId) && p.Domain == "scoped-business"
                            select new { rp.RoleId, p.Capability }).ToArrayAsync(ct);
        var rows = roles.Select(r => new AssignmentRoleOption(r.Id, r.Name, r.RoleClass,
            grants.Where(g => g.RoleId == r.Id).Select(g => g.Capability).Distinct().OrderBy(x => x).ToArray())).ToArray();
        await tx.CommitAsync(ct);
        return Results.Ok(new Page<AssignmentRoleOption>(rows, total, page, pageSize));
    }

    public async Task<IResult> RejectBindingAsync(Guid? id, CancellationToken ct)
    {
        await using var tx = await BeginAsync(ct);
        return await AuthorizeAsync(Actor, tx, ct) ?? await DenyAsync(400, "invalid-request", null, id, tx, ct);
    }

    private async Task<IDbContextTransaction> BeginAsync(CancellationToken ct)
    {
        var tx = await db.Database.BeginTransactionAsync(ct);
        try { await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct); permission.ActingRoleId = null; return tx; }
        catch { await tx.DisposeAsync(); throw; }
    }
    private async Task<IResult?> AuthorizeAsync(Guid actorId, IDbContextTransaction tx, CancellationToken ct)
    {
        if (await guard.AllowsAsync(actorId, "scope-assignments:manage", ct)) return null;
        var now = clock.GetUtcNow();
        var valid = current.Entity is { } candidate && await db.Set<IdentitySession>().AsNoTracking().AnyAsync(s => s.Id == candidate.Id
            && s.RevokedAtUtc == null && s.ExpiresAtUtc > now && s.LastSeenAtUtc > now.AddMinutes(-30)
            && db.Set<IdentityUser>().Any(u => u.Id == s.UserId && u.IsActive && u.SecurityVersion == s.SecurityVersion), ct);
        return await DenyAsync(valid ? 403 : 401, "permission-denied", null, null, tx, ct);
    }
    private async Task<int?> ValidateTargetAsync(Guid userId, ScopeKey scope, Guid roleId, CancellationToken ct)
    {
        var user = await db.Set<IdentityUser>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, ct);
        if (user is null) return 404;
        if (!user.IsActive) return 409;
        var role = await db.Set<IdentityRole>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == roleId, ct);
        if (role is null) return 404;
        if (!BusinessClasses.Contains(role.RoleClass)) return 400;
        var workspace = await db.Set<Workspace>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == scope.WorkspaceId, ct);
        if (workspace is null) return 404;
        Project? project = null; Site? site = null;
        if (scope.ProjectId is { } p)
        {
            project = await db.Set<Project>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == p && x.WorkspaceId == scope.WorkspaceId, ct);
            if (project is null) return 404;
        }
        if (scope.SiteId is { } s)
        {
            site = await db.Set<Site>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == s && x.WorkspaceId == scope.WorkspaceId && x.ProjectId == scope.ProjectId, ct);
            if (site is null) return 404;
        }
        return !workspace.IsActive || project is { IsActive: false } || site is { IsActive: false } ? 409 : null;
    }
    private Task<bool> DuplicateAsync(Guid user, ScopeKey scope, Guid role, CancellationToken ct) => db.Set<ScopeAssignment>().AnyAsync(x => x.UserId == user
        && x.WorkspaceId == scope.WorkspaceId && x.ProjectId == scope.ProjectId && x.SiteId == scope.SiteId && x.RoleId == role && x.RevokedAtUtc == null, ct);
    private ScopeAssignment New(Guid user, ScopeKey scope, Guid role, string reason, Guid actor) => new()
    {
        Id = Guid.NewGuid(),
        UserId = user,
        WorkspaceId = scope.WorkspaceId,
        ProjectId = scope.ProjectId,
        SiteId = scope.SiteId,
        RoleId = role,
        CreatedAtUtc = clock.GetUtcNow(),
        CreatedBy = actor,
        Reason = reason.Trim()
    };
    private async Task RevokeRowAsync(ScopeAssignment row, Guid actor, string reason, CancellationToken ct)
    {
        row.RevokedAtUtc = clock.GetUtcNow(); row.RevokedBy = actor; row.RevocationReason = reason.Trim(); row.Version = checked(row.Version + 1);
        await WriteAsync("scope.assignment.revoked", row, reason, ct);
    }
    private Task WriteAsync(string action, ScopeAssignment row, string reason, CancellationToken ct, Guid? replacement = null)
    {
        var metadata = new Dictionary<string, string> { ["reason"] = reason.Trim(), ["scope-validation"] = "authorized" };
        if (replacement is { } id) metadata["assignment-id"] = id.ToString();
        return audit.WriteAsync(new SecurityAuditRequest(Actor, permission.ActingRoleId, row.WorkspaceId, row.ProjectId, row.SiteId,
            action, "scope-assignment", row.Id, "success", metadata), ct);
    }
    private async Task<IResult> DenyAsync(int status, string reason, ScopeKey? scope, Guid? id, IDbContextTransaction tx, CancellationToken ct)
    {
        // Denials occur before any assignment/user/session mutation.
        await audit.WriteAsync(new SecurityAuditRequest(Actor == Guid.Empty ? null : Actor, permission.ActingRoleId, scope?.WorkspaceId, scope?.ProjectId, scope?.SiteId,
            "scope.assignment.denied", "scope-assignment", id, "denied", new Dictionary<string, string>
            { ["reason"] = reason, ["capability"] = "scope-assignments:manage", ["scope-validation"] = "requested-unverified" }), ct);
        await CommitAsync(tx, ct);
        return Results.Problem(statusCode: status, title: status switch
        {
            400 => "ข้อมูลการมอบหมายไม่ถูกต้อง",
            401 => "กรุณาเข้าสู่ระบบใหม่",
            403 => "ไม่มีสิทธิ์หรือไม่สามารถจัดการสิทธิ์ของตนเอง",
            404 => "ไม่พบข้อมูลการมอบหมายที่ระบุ",
            _ => "ข้อมูลซ้ำ รุ่นข้อมูลเปลี่ยนแปลง หรือผู้ใช้และพื้นที่ไม่พร้อมใช้งาน"
        });
    }
    private async Task CommitAsync(IDbContextTransaction tx, CancellationToken ct) { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
    private static bool Valid(ScopeKey? scope, Guid role, string? reason) => scope is { IsValid: true } && role != Guid.Empty && OrganizationValidation.Text(reason, 500);
    private static bool Prefix(string? prefix) => prefix is null || prefix.Length <= 100 && !prefix.Any(char.IsControl);
    private static bool Pagination(int page, ref int size, out int offset)
    {
        size = Math.Min(size, 100); var value = ((long)page - 1) * size; offset = value is >= 0 and <= int.MaxValue ? (int)value : 0;
        return page >= 1 && size >= 1 && value <= int.MaxValue;
    }
    private static ScopeKey Key(ScopeAssignment row) => new(row.WorkspaceId, row.ProjectId, row.SiteId);
    private static AssignmentView View(ScopeAssignment row) => new(row.Id, row.UserId, Key(row), row.RoleId, row.Version, row.RevokedAtUtc);
}
