using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.Identity;
using TPR10.Api.Identity.Authorization;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Organization.Data;
using TPR10.Api.Scopes;
using TPR10.Api.Scopes.Assignments;

namespace TPR10.Api.Organization;

// Control plane only. Every result is materialized under the shared identity lock.
public sealed class OrganizationService(Tpr10DbContext db, RequestSession current, PermissionMutationGuard guard,
    PermissionContext permission, AssignmentLifecycle lifecycle, ISessionService sessions, IAuditEventWriter audit, TimeProvider clock)
{
    public async Task<IResult> CreateAsync(Guid actorId, OrganizationKind kind, ScopeKey? parent, CreateOrganization request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(ct);
        if (!await guard.AllowsAsync(actorId, "organization:manage", ct)) return await DenyAsync(403, "permission-denied", actorId, kind, parent, null, tx, ct);
        var code = OrganizationValidation.Code(request.Code);
        if (!OrganizationValidation.Parent(kind, parent) || code is null || !OrganizationValidation.Text(request.Name, 200))
            return await DenyAsync(400, "invalid-request", actorId, kind, parent, null, tx, ct);
        var parentStatus = await ParentStatusAsync(parent, requireActive: true, ct);
        if (parentStatus is { } status) return await DenyAsync(status, "parent-unavailable", actorId, kind, parent, null, tx, ct);
        if (await Query(kind, parent).AnyAsync(x => x.Code == code, ct)) return await DenyAsync(409, "duplicate-code", actorId, kind, parent, null, tx, ct);
        var id = Guid.NewGuid();
        var entity = NewEntity(kind, id, parent, code, request.Name.Trim(), actorId);
        db.Add(entity);
        var view = new OrganizationView(id, parent?.WorkspaceId ?? id, kind == OrganizationKind.Site ? parent!.ProjectId : null, code, request.Name.Trim(), true, 1);
        await SuccessAsync("organization.created", actorId, kind, ScopeFor(kind, parent, id), id, "code,name,is-active,version", null, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Created(ResourcePath(kind, parent, id), view);
    }

    public async Task<IResult> UpdateAsync(Guid actorId, OrganizationKind kind, ScopeKey? expectedParent, Guid id, UpdateOrganization request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(ct);
        if (!await guard.AllowsAsync(actorId, "organization:manage", ct)) return await DenyAsync(403, "permission-denied", actorId, kind, expectedParent, id, tx, ct);
        if (id == Guid.Empty || !OrganizationValidation.Parent(kind, expectedParent) || !OrganizationValidation.Text(request.Name, 200)
            || !OrganizationValidation.Text(request.Reason, 500) || request.ExpectedVersion < 1)
            return await DenyAsync(400, "invalid-request", actorId, kind, expectedParent, id, tx, ct);
        // Filter by the complete route ancestry BEFORE looking up a tracked entity.
        var row = await Query(kind, expectedParent).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return await DenyAsync(404, "resource-unavailable", actorId, kind, expectedParent, id, tx, ct);
        var parentStatus = await ParentStatusAsync(expectedParent, request.IsActive, ct);
        if (parentStatus is { } status) return await DenyAsync(status, "parent-unavailable", actorId, kind, expectedParent, id, tx, ct);
        if (row.Version != request.ExpectedVersion || row.Version == long.MaxValue)
            return await DenyAsync(409, "version-conflict", actorId, kind, expectedParent, id, tx, ct);
        var name = request.Name.Trim();
        if (row.Name == name && row.IsActive == request.IsActive) { await tx.CommitAsync(ct); return Results.Ok(View(row)); }
        var entity = await db.FindAsync(EntityType(kind), [id], ct) ?? throw new InvalidOperationException("Organization disappeared under lock.");
        var entry = db.Entry(entity);
        entry.Property("Name").CurrentValue = name;
        entry.Property("IsActive").CurrentValue = request.IsActive;
        entry.Property("Version").CurrentValue = checked(row.Version + 1);
        entry.Property("UpdatedAtUtc").CurrentValue = clock.GetUtcNow();
        entry.Property("UpdatedBy").CurrentValue = actorId;
        var scope = ScopeFor(kind, expectedParent, id)!;
        if (row.IsActive && !request.IsActive)
        {
            var affected = kind == OrganizationKind.Department ? [] : await lifecycle.RevokeForScopeAsync(scope, actorId, request.Reason.Trim(), ct);
            var directoryAffected = kind is OrganizationKind.Workspace or OrganizationKind.Department
                ? await new Attendance.Directory.DirectoryLifecycle(db, audit, clock, permission)
                    .EndForUnitAsync(row.WorkspaceId, kind == OrganizationKind.Department ? id : null, actorId, request.Reason.Trim(), ct)
                : [];
            foreach (var userId in affected.Concat(directoryAffected).Distinct().OrderBy(x => x))
                await sessions.RevokeUserAsync(userId, "organization-deactivated", ct);
        }
        var action = row.IsActive != request.IsActive ? request.IsActive ? "organization.reactivated" : "organization.deactivated" : "organization.updated";
        await SuccessAsync(action, actorId, kind, scope, id, "name,is-active,version", request.Reason.Trim(), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new OrganizationView(row.Id, row.WorkspaceId, row.ProjectId, row.Code, name, request.IsActive, row.Version + 1));
    }

    public async Task<IResult> ListAsync(OrganizationKind kind, ScopeKey? parent, int page, int pageSize, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(ct);
        var actor = current.Entity?.UserId ?? Guid.Empty;
        if (!await guard.AllowsAsync(actor, "organization:manage", ct)) return await DenyAsync(403, "permission-denied", actor, kind, parent, null, tx, ct);
        pageSize = Math.Min(pageSize, 100);
        var offset = ((long)page - 1) * pageSize;
        if (!OrganizationValidation.Parent(kind, parent) || page < 1 || pageSize < 1 || offset > int.MaxValue)
            return await DenyAsync(400, "invalid-request", actor, kind, parent, null, tx, ct);
        var parentStatus = await ParentStatusAsync(parent, requireActive: false, ct);
        if (parentStatus is { } status) return await DenyAsync(status, "parent-unavailable", actor, kind, parent, null, tx, ct);
        var query = Query(kind, parent);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(x => x.Code).ThenBy(x => x.Id).Skip((int)offset).Take(pageSize).ToArrayAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new OrganizationPage(rows.Select(View).ToArray(), total, page, pageSize));
    }

    public async Task<IResult> RejectBindingAsync(OrganizationKind kind, ScopeKey? parent, Guid? id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(ct);
        var actor = current.Entity?.UserId ?? Guid.Empty;
        var allowed = await guard.AllowsAsync(actor, "organization:manage", ct);
        return await DenyAsync(allowed ? 400 : 403, allowed ? "invalid-request" : "permission-denied", actor, kind, parent, id, tx, ct);
    }

    private async Task LockAsync(CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct);
        permission.ActingRoleId = null;
    }

    private async Task<int?> ParentStatusAsync(ScopeKey? parent, bool requireActive, CancellationToken ct)
    {
        if (parent is null) return null;
        var workspace = await db.Set<Workspace>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == parent.WorkspaceId, ct);
        if (workspace is null) return 404;
        Project? project = null;
        if (parent.ProjectId is { } projectId)
        {
            project = await db.Set<Project>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == projectId && x.WorkspaceId == parent.WorkspaceId, ct);
            if (project is null) return 404;
        }
        return requireActive && (!workspace.IsActive || project is { IsActive: false }) ? 409 : null;
    }

    // A private member-initialized projection keeps predicates translated to SQL.
    private IQueryable<Row> Query(OrganizationKind kind, ScopeKey? parent) => kind switch
    {
        OrganizationKind.Workspace => db.Set<Workspace>().AsNoTracking().Select(x => new Row { Id = x.Id, WorkspaceId = x.Id, Code = x.Code, Name = x.Name, IsActive = x.IsActive, Version = x.Version }),
        OrganizationKind.Department => db.Set<Department>().AsNoTracking().Where(x => x.WorkspaceId == parent!.WorkspaceId).Select(x => new Row { Id = x.Id, WorkspaceId = x.WorkspaceId, Code = x.Code, Name = x.Name, IsActive = x.IsActive, Version = x.Version }),
        OrganizationKind.Project => db.Set<Project>().AsNoTracking().Where(x => x.WorkspaceId == parent!.WorkspaceId).Select(x => new Row { Id = x.Id, WorkspaceId = x.WorkspaceId, Code = x.Code, Name = x.Name, IsActive = x.IsActive, Version = x.Version }),
        OrganizationKind.Site => db.Set<Site>().AsNoTracking().Where(x => x.WorkspaceId == parent!.WorkspaceId && x.ProjectId == parent.ProjectId).Select(x => new Row { Id = x.Id, WorkspaceId = x.WorkspaceId, ProjectId = x.ProjectId, Code = x.Code, Name = x.Name, IsActive = x.IsActive, Version = x.Version }),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    private sealed class Row
    {
        public Guid Id { get; init; }
        public Guid WorkspaceId { get; init; }
        public Guid? ProjectId { get; init; }
        public required string Code { get; init; }
        public required string Name { get; init; }
        public bool IsActive { get; init; }
        public long Version { get; init; }
    }
    private static OrganizationView View(Row row) => new(row.Id, row.WorkspaceId, row.ProjectId, row.Code, row.Name, row.IsActive, row.Version);
    private object NewEntity(OrganizationKind kind, Guid id, ScopeKey? parent, string code, string name, Guid actor) => kind switch
    {
        OrganizationKind.Workspace => new Workspace { Id = id, Code = code, Name = name, CreatedBy = actor, CreatedAtUtc = clock.GetUtcNow() },
        OrganizationKind.Department => new Department { Id = id, WorkspaceId = parent!.WorkspaceId, Code = code, Name = name, CreatedBy = actor, CreatedAtUtc = clock.GetUtcNow() },
        OrganizationKind.Project => new Project { Id = id, WorkspaceId = parent!.WorkspaceId, Code = code, Name = name, CreatedBy = actor, CreatedAtUtc = clock.GetUtcNow() },
        OrganizationKind.Site => new Site { Id = id, WorkspaceId = parent!.WorkspaceId, ProjectId = parent.ProjectId!.Value, Code = code, Name = name, CreatedBy = actor, CreatedAtUtc = clock.GetUtcNow() },
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    private static Type EntityType(OrganizationKind kind) => kind switch
    {
        OrganizationKind.Workspace => typeof(Workspace),
        OrganizationKind.Department => typeof(Department),
        OrganizationKind.Project => typeof(Project),
        OrganizationKind.Site => typeof(Site),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    private static ScopeKey? ScopeFor(OrganizationKind kind, ScopeKey? parent, Guid? id) => kind switch
    {
        OrganizationKind.Workspace => id is { } workspace ? new(workspace) : null,
        OrganizationKind.Project => parent is null ? null : new(parent.WorkspaceId, id),
        OrganizationKind.Site => parent is null ? null : new(parent.WorkspaceId, parent.ProjectId, id),
        _ => parent
    };
    private Task SuccessAsync(string action, Guid actor, OrganizationKind kind, ScopeKey? scope, Guid id, string changes, string? reason, CancellationToken ct)
    {
        var metadata = new Dictionary<string, string> { ["changed-fields"] = changes, ["scope-validation"] = "authorized" };
        if (reason is not null) metadata["reason"] = reason;
        return audit.WriteAsync(new SecurityAuditRequest(actor, permission.ActingRoleId, scope?.WorkspaceId, scope?.ProjectId, scope?.SiteId,
            action, kind.ToString().ToLowerInvariant(), id, "success", metadata), ct);
    }
    private async Task<IResult> DenyAsync(int status, string reason, Guid actor, OrganizationKind kind, ScopeKey? parent, Guid? id, IDbContextTransaction tx, CancellationToken ct)
    {
        // All callers deny BEFORE modifying business entities. Only this audit commits.
        var scope = ScopeFor(kind, parent, id);
        await audit.WriteAsync(new SecurityAuditRequest(actor == Guid.Empty ? null : actor, permission.ActingRoleId,
            scope?.WorkspaceId, scope?.ProjectId, scope?.SiteId, "organization.access.denied", kind.ToString().ToLowerInvariant(), id, "denied",
            new Dictionary<string, string> { ["reason"] = reason, ["capability"] = "organization:manage", ["scope-validation"] = "requested-unverified" }), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Problem(statusCode: status, title: status switch
        {
            400 => "ข้อมูลโครงสร้างองค์กรไม่ถูกต้อง",
            403 => "ไม่มีสิทธิ์หรือการยืนยันตัวตนไม่เพียงพอ",
            404 => "ไม่พบโครงสร้างองค์กรที่ระบุ",
            _ => "ข้อมูลซ้ำ รุ่นข้อมูลเปลี่ยนแปลง หรือพื้นที่แม่ไม่พร้อมใช้งาน"
        });
    }
    private static string ResourcePath(OrganizationKind kind, ScopeKey? parent, Guid id) => kind switch
    {
        OrganizationKind.Workspace => $"/api/v1/organization/workspaces/{id}",
        OrganizationKind.Department => $"/api/v1/organization/workspaces/{parent!.WorkspaceId}/departments/{id}",
        OrganizationKind.Project => $"/api/v1/organization/workspaces/{parent!.WorkspaceId}/projects/{id}",
        _ => $"/api/v1/organization/workspaces/{parent!.WorkspaceId}/projects/{parent.ProjectId}/sites/{id}"
    };
}
