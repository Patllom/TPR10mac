using Microsoft.EntityFrameworkCore;
using Npgsql;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.Identity;
using TPR10.Api.Identity.Authorization;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Organization.Data;
using TPR10.Api.Scopes;

namespace TPR10.Api.Attendance.Directory;

public sealed partial class DirectoryService(Tpr10DbContext db, RequestSession current, PermissionMutationGuard guard,
    PermissionContext permission, ISessionService sessions, IAuditEventWriter audit, TimeProvider clock)
{
    public const string Capability = "attendance:directory-manage";
    private Guid Actor => current.Entity?.UserId ?? Guid.Empty;
    private sealed record Change(IResult Result, Guid? Target = null, Guid[]? Affected = null, string Fields = "none");

    public Task<IResult> SetMembershipAsync(SetMembership r, CancellationToken ct) => RunAsync("membership", r.Reason, async () =>
    {
        if (!Valid(r.Reason, r.UserId, r.WorkspaceId, r.DepartmentId) || r.ExpectedVersion is < 1) return Reject(400);
        if (!await UserActive(r.UserId, ct) || !await UnitActive(r.WorkspaceId, r.DepartmentId, ct)) return Reject(404);
        var old = await db.Set<EmployeeMembership>().SingleOrDefaultAsync(x => x.UserId == r.UserId && x.ValidToUtc == null, ct);
        if (old?.Version != r.ExpectedVersion) return Reject(409);
        if (old is not null && old.WorkspaceId == r.WorkspaceId && old.DepartmentId == r.DepartmentId) return new(Results.Ok(View(old)), old.Id);
        if (r.UserId == Actor) return Reject(403);
        var now = DirectoryQueries.Now(clock);
        var affected = new HashSet<Guid> { r.UserId };
        if (old is not null)
        {
            Close(old, now);
            await CloseLines(r.UserId, now, affected, ct);
            await db.SaveChangesAsync(ct); // End before insert to satisfy temporal overlap trigger.
        }
        var nextVersion = await NextVersionAsync(db.Set<EmployeeMembership>().Where(x => x.UserId == r.UserId).Select(x => x.Version), ct);
        var row = new EmployeeMembership
        {
            Id = Guid.NewGuid(),
            UserId = r.UserId,
            WorkspaceId = r.WorkspaceId,
            DepartmentId = r.DepartmentId,
            Version = nextVersion,
            ValidFromUtc = now,
            CreatedAtUtc = now,
            CreatedBy = Actor,
            Reason = r.Reason.Trim()
        };
        db.Add(row);
        return new(old is null ? Results.Created($"/api/v1/attendance/directory/memberships/{row.Id}", View(row)) : Results.Ok(View(row)), row.Id, affected.ToArray(), "membership,reporting,version");
    }, ct);

    public Task<IResult> SetReportingAsync(SetReportingLine r, CancellationToken ct) => RunAsync("reporting-line", r.Reason, async () =>
    {
        if (!Valid(r.Reason, r.EmployeeUserId, r.SupervisorUserId) || r.ExpectedVersion is < 1 || r.EmployeeUserId == r.SupervisorUserId) return Reject(400);
        var now = DirectoryQueries.Now(clock);
        var employee = await DirectoryQueries.Employment(db, now).SingleOrDefaultAsync(x => x.UserId == r.EmployeeUserId, ct);
        var manager = await DirectoryQueries.Employment(db, now).SingleOrDefaultAsync(x => x.UserId == r.SupervisorUserId, ct);
        if (employee is null || manager is null || employee.WorkspaceId != manager.WorkspaceId) return Reject(404);
        var old = await db.Set<ReportingLine>().SingleOrDefaultAsync(x => x.EmployeeUserId == r.EmployeeUserId && x.ValidToUtc == null, ct);
        if (old?.Version != r.ExpectedVersion) return Reject(409);
        if (old is not null && old.SupervisorUserId == r.SupervisorUserId && old.EmployeeMembershipId == employee.Id) return new(Results.Ok(View(old)), old.Id);
        var graph = await DirectoryQueries.Reporting(db, now).ToDictionaryAsync(x => x.EmployeeUserId, x => x.SupervisorUserId, ct);
        if (DirectoryRules.CreatesCycle(r.EmployeeUserId, r.SupervisorUserId, graph)) return Reject(400);
        if (old is not null) { Close(old, now); await db.SaveChangesAsync(ct); }
        var nextVersion = await NextVersionAsync(db.Set<ReportingLine>().Where(x => x.EmployeeUserId == r.EmployeeUserId).Select(x => x.Version), ct);
        var row = new ReportingLine
        {
            Id = Guid.NewGuid(),
            EmployeeMembershipId = employee.Id,
            EmployeeUserId = r.EmployeeUserId,
            SupervisorUserId = r.SupervisorUserId,
            Version = nextVersion,
            ValidFromUtc = now,
            CreatedAtUtc = now,
            CreatedBy = Actor,
            Reason = r.Reason.Trim()
        };
        db.Add(row);
        return new(old is null ? Results.Created($"/api/v1/attendance/directory/reporting-lines/{row.Id}", View(row)) : Results.Ok(View(row)), row.Id,
            new[] { r.EmployeeUserId, r.SupervisorUserId, old?.SupervisorUserId ?? r.SupervisorUserId }, "reporting,version");
    }, ct);

    public Task<IResult> GrantHrAsync(GrantHrAssignment r, CancellationToken ct) => RunAsync("hr-assignment", r.Reason, async () =>
    {
        if (!Valid(r.Reason, r.UserId, r.WorkspaceId, r.DepartmentId)) return Reject(400);
        if (!await UserActive(r.UserId, ct) || !await UnitActive(r.WorkspaceId, r.DepartmentId, ct)) return Reject(404);
        if (await db.Set<HrAssignment>().AnyAsync(x => x.UserId == r.UserId && x.WorkspaceId == r.WorkspaceId && x.DepartmentId == r.DepartmentId && x.ValidToUtc == null, ct)) return Reject(409);
        var now = DirectoryQueries.Now(clock);
        var row = new HrAssignment
        {
            Id = Guid.NewGuid(),
            UserId = r.UserId,
            WorkspaceId = r.WorkspaceId,
            DepartmentId = r.DepartmentId,
            ValidFromUtc = now,
            CreatedAtUtc = now,
            CreatedBy = Actor,
            Reason = r.Reason.Trim()
        };
        db.Add(row);
        return new(Results.Created($"/api/v1/attendance/directory/hr-assignments/{row.Id}", View(row)), row.Id, [row.UserId], "hr-assignment,version");
    }, ct);

    public Task<IResult> EndMembershipAsync(Guid id, EndDirectoryRow r, CancellationToken ct) => EndAsync<EmployeeMembership>(id, r, "membership", ct);
    public Task<IResult> EndReportingAsync(Guid id, EndDirectoryRow r, CancellationToken ct) => EndAsync<ReportingLine>(id, r, "reporting-line", ct);
    public Task<IResult> EndHrAsync(Guid id, EndDirectoryRow r, CancellationToken ct) => EndAsync<HrAssignment>(id, r, "hr-assignment", ct);

    private Task<IResult> EndAsync<T>(Guid id, EndDirectoryRow r, string kind, CancellationToken ct) where T : class => RunAsync(kind, r.Reason, async () =>
    {
        if (!Valid(r.Reason, id) || r.ExpectedVersion < 1) return Reject(400);
        var row = await db.Set<T>().FindAsync([id], ct);
        if (row is null) return Reject(404);
        var entry = db.Entry(row);
        if (entry.Property<long>("Version").CurrentValue != r.ExpectedVersion || entry.Property<DateTimeOffset?>("ValidToUtc").CurrentValue is not null) return Reject(409);
        if (row is EmployeeMembership own && own.UserId == Actor) return Reject(403);
        var affected = new HashSet<Guid>();
        var now = DirectoryQueries.Now(clock);
        Close(row, now);
        switch (row)
        {
            case EmployeeMembership m: affected.Add(m.UserId); await CloseLines(m.UserId, now, affected, ct); break;
            case ReportingLine l: affected.Add(l.EmployeeUserId); affected.Add(l.SupervisorUserId); break;
            case HrAssignment h: affected.Add(h.UserId); break;
        }
        return new(Results.NoContent(), id, affected.ToArray(), "valid-to,ended-by,version");
    }, ct);

    private async Task CloseLines(Guid user, DateTimeOffset now, HashSet<Guid> affected, CancellationToken ct)
    {
        var lines = await db.Set<ReportingLine>().Where(x => x.ValidToUtc == null && (x.EmployeeUserId == user || x.SupervisorUserId == user)).ToArrayAsync(ct);
        foreach (var line in lines)
        {
            Close(line, now);
            affected.Add(line.EmployeeUserId); affected.Add(line.SupervisorUserId);
        }
    }

    private void Close(object row, DateTimeOffset now)
    {
        var e = db.Entry(row);
        var version = (long)e.Property("Version").CurrentValue!;
        if ((DateTimeOffset)e.Property("ValidFromUtc").CurrentValue! >= now || version == long.MaxValue) throw new DirectoryConflictException();
        e.Property("ValidToUtc").CurrentValue = now;
        e.Property("EndedBy").CurrentValue = Actor;
        e.Property("Version").CurrentValue = version + 1;
    }

    public Task<IResult> RejectBindingAsync(CancellationToken ct) => RunAsync("binding", null, () => Task.FromResult(Reject(400)), ct);

    private async Task<IResult> RunAsync(string kind, string? reason, Func<Task<Change>> action, CancellationToken ct)
    {
        var status = 403;
        try
        {
            await using (var tx = await ScopeOperation.BeginAsync(db, ct))
            {
                permission.ActingRoleId = null;
                if (await guard.AllowsAsync(Actor, Capability, ct))
                {
                    var before = await AuthorityAsync(ct);
                    Change change;
                    try
                    {
                        change = await action();
                        status = ((IStatusCodeHttpResult)change.Result).StatusCode ?? 500;
                        if (status < 400)
                        {
                            await db.SaveChangesAsync(ct);
                            if ((await AuthorityAsync(ct)).Except(before).Any()) status = 403;
                            else
                            {
                                foreach (var user in (change.Affected ?? []).Distinct().OrderBy(x => x))
                                    await sessions.RevokeUserAsync(user, "attendance-directory-changed", ct);
                                await WriteAuditAsync("attendance.directory.completed", kind, change.Target, "success", change.Fields, reason, ct);
                                await db.SaveChangesAsync(ct);
                                await tx.CommitAsync(ct);
                                return change.Result;
                            }
                        }
                    }
                    catch (DirectoryConflictException) { status = 409; }
                    catch (Exception error) when (IsConflict(error)) { status = 409; }
                }
                await tx.RollbackAsync(ct);
            }
            db.ChangeTracker.Clear();
            await using var denied = await ScopeOperation.BeginAsync(db, ct);
            await WriteAuditAsync("attendance.directory.denied", kind, null, "denied", "none", "request-denied-" + status, ct);
            await db.SaveChangesAsync(ct);
            await denied.CommitAsync(ct);
            return Problem(status);
        }
        catch (Exception error) when (ScopeOperation.IsDatabaseFault(error)) { db.ChangeTracker.Clear(); return Problem(503); }
    }

    private async Task<HashSet<string>> AuthorityAsync(CancellationToken ct)
    {
        var now = DirectoryQueries.Now(clock);
        var teams = await DirectoryQueries.Reporting(db, now).AsNoTracking().Where(x => x.SupervisorUserId == Actor)
            .Select(x => new { x.EmployeeUserId, x.EmployeeMembershipId }).ToArrayAsync(ct);
        var units = await DirectoryQueries.Hr(db, now).AsNoTracking().Where(x => x.UserId == Actor)
            .Select(x => new { x.WorkspaceId, x.DepartmentId }).ToArrayAsync(ct);
        return teams.Select(x => $"team:{x.EmployeeUserId}:{x.EmployeeMembershipId}")
            .Concat(units.Select(x => $"hr:{x.WorkspaceId}:{x.DepartmentId}")).ToHashSet(StringComparer.Ordinal);
    }

    private Task WriteAuditAsync(string action, string kind, Guid? target, string outcome, string fields, string? reason, CancellationToken ct)
    {
        var metadata = new Dictionary<string, string> { ["capability"] = Capability, ["changed-fields"] = fields };
        if (reason is not null && DirectoryRules.ValidReason(reason)) metadata["reason"] = reason.Trim();
        return audit.WriteAsync(new SecurityAuditRequest(Actor == Guid.Empty ? null : Actor, permission.ActingRoleId, null, null, null, action, kind, target, outcome, metadata), ct);
    }

    private Task<bool> UserActive(Guid id, CancellationToken ct) => db.Set<IdentityUser>().AnyAsync(x => x.Id == id && x.IsActive, ct);
    private static async Task<long> NextVersionAsync(IQueryable<long> versions, CancellationToken ct)
    {
        var latest = await versions.Select(x => (long?)x).MaxAsync(ct) ?? 0;
        if (latest == long.MaxValue) throw new DirectoryConflictException();
        return latest + 1;
    }
    private Task<bool> UnitActive(Guid workspace, Guid department, CancellationToken ct) => db.Set<Department>().AnyAsync(x => x.Id == department && x.WorkspaceId == workspace && x.IsActive
        && db.Set<Workspace>().Any(w => w.Id == workspace && w.IsActive), ct);
    private static bool Valid(string? reason, params Guid[] ids) => ids.All(x => x != Guid.Empty) && DirectoryRules.ValidReason(reason);
    private static Change Reject(int status) => new(Problem(status));
    private static bool IsConflict(Exception error) => error is DbUpdateConcurrencyException
        || error is PostgresException { SqlState: "23P01" or "23505" } || error is DbUpdateException { InnerException: PostgresException { SqlState: "23P01" or "23505" } };
    internal static IResult Problem(int status) => Results.Problem(statusCode: status, title: status switch
    {
        400 => "ข้อมูลบุคลากรหรือคำขอไม่ถูกต้อง",
        403 => "ไม่มีสิทธิ์ ยืนยันตัวตนไม่เพียงพอ หรือเป็นการเพิ่มสิทธิ์ให้ตนเอง",
        404 => "ไม่พบบุคลากรหรือหน่วยงานที่พร้อมใช้งาน",
        409 => "ข้อมูลเปลี่ยนแปลง กรุณาโหลดใหม่",
        _ => "บริการไม่พร้อมใช้งาน กรุณาลองใหม่ภายหลัง"
    });
    private static MembershipView View(EmployeeMembership x) => new(x.Id, x.UserId, x.WorkspaceId, x.DepartmentId, x.ValidFromUtc, x.ValidToUtc, x.Version);
    private static ReportingView View(ReportingLine x) => new(x.Id, x.EmployeeUserId, x.SupervisorUserId, x.ValidFromUtc, x.ValidToUtc, x.Version);
    private static HrView View(HrAssignment x) => new(x.Id, x.UserId, x.WorkspaceId, x.DepartmentId, x.ValidFromUtc, x.ValidToUtc, x.Version);
}
