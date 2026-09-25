using Microsoft.EntityFrameworkCore;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.Identity.Authorization;

namespace TPR10.Api.Attendance.Directory;

// Caller owns the identity lock, transaction, SaveChanges and session revocations.
public sealed class DirectoryLifecycle(Tpr10DbContext db, IAuditEventWriter audit, TimeProvider clock, PermissionContext permission)
{
    public Task<Guid[]> EndForUserAsync(Guid userId, Guid actorId, string reason, CancellationToken ct)
    {
        Validate(actorId, reason);
        if (userId == Guid.Empty) throw new ArgumentException("A user is required.", nameof(userId));
        return EndAsync(db.Set<EmployeeMembership>().Where(x => x.UserId == userId && x.ValidToUtc == null),
            db.Set<HrAssignment>().Where(x => x.UserId == userId && x.ValidToUtc == null),
            db.Set<ReportingLine>().Where(x => (x.EmployeeUserId == userId || x.SupervisorUserId == userId) && x.ValidToUtc == null), actorId, reason, ct);
    }

    public Task<Guid[]> EndForUnitAsync(Guid workspaceId, Guid? departmentId, Guid actorId, string reason, CancellationToken ct)
    {
        Validate(actorId, reason);
        if (workspaceId == Guid.Empty || departmentId == Guid.Empty) throw new ArgumentException("A valid unit is required.");
        var memberships = db.Set<EmployeeMembership>().Where(x => x.WorkspaceId == workspaceId
            && (departmentId == null || x.DepartmentId == departmentId) && x.ValidToUtc == null);
        var users = memberships.Select(x => x.UserId);
        return EndAsync(memberships, db.Set<HrAssignment>().Where(x => x.WorkspaceId == workspaceId
                && (departmentId == null || x.DepartmentId == departmentId) && x.ValidToUtc == null),
            db.Set<ReportingLine>().Where(x => x.ValidToUtc == null && (users.Contains(x.EmployeeUserId) || users.Contains(x.SupervisorUserId))), actorId, reason, ct);
    }

    private async Task<Guid[]> EndAsync(IQueryable<EmployeeMembership> membershipQuery, IQueryable<HrAssignment> hrQuery,
        IQueryable<ReportingLine> lineQuery, Guid actor, string reason, CancellationToken ct)
    {
        var memberships = (await membershipQuery.OrderBy(x => x.Id).ToArrayAsync(ct)).Where(x => x.ValidToUtc == null).ToArray();
        var hrs = (await hrQuery.OrderBy(x => x.Id).ToArrayAsync(ct)).Where(x => x.ValidToUtc == null).ToArray();
        var lines = (await lineQuery.OrderBy(x => x.Id).ToArrayAsync(ct)).Where(x => x.ValidToUtc == null).ToArray();
        var instant = clock.GetUtcNow();
        var now = new DateTimeOffset(instant.UtcTicks - instant.UtcTicks % 10, TimeSpan.Zero);
        var rows = memberships.Cast<object>().Concat(hrs).Concat(lines).ToArray();
        // Validate the whole batch before touching any tracked state.
        foreach (var row in rows)
        {
            var entry = db.Entry(row);
            if ((DateTimeOffset)entry.Property("ValidFromUtc").CurrentValue! >= now || (long)entry.Property("Version").CurrentValue! == long.MaxValue)
                throw new DirectoryConflictException();
        }
        foreach (var row in rows)
        {
            var entry = db.Entry(row);
            entry.Property("ValidToUtc").CurrentValue = now;
            entry.Property("EndedBy").CurrentValue = actor;
            entry.Property("Version").CurrentValue = checked((long)entry.Property("Version").CurrentValue! + 1);
            await audit.WriteAsync(new SecurityAuditRequest(actor, permission.ActingRoleId,
                row is EmployeeMembership m ? m.WorkspaceId : row is HrAssignment h ? h.WorkspaceId : null,
                null, null, "attendance.directory.ended", row is ReportingLine ? "reporting-line" : row is HrAssignment ? "hr-assignment" : "employee-membership",
                (Guid)entry.Property("Id").CurrentValue!, "success",
                new Dictionary<string, string> { ["reason"] = reason.Trim(), ["changed-fields"] = "valid-to,ended-by,version" }), ct);
        }
        return memberships.Select(x => x.UserId).Concat(hrs.Select(x => x.UserId))
            .Concat(lines.SelectMany(x => new[] { x.EmployeeUserId, x.SupervisorUserId })).Distinct().OrderBy(x => x).ToArray();
    }

    private void Validate(Guid actor, string reason)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Directory lifecycle requires a caller transaction and identity lock.");
        if (actor == Guid.Empty || !DirectoryRules.ValidReason(reason)) throw new ArgumentException("An actor and bounded reason are required.");
    }
}

public sealed class DirectoryConflictException : Exception;
