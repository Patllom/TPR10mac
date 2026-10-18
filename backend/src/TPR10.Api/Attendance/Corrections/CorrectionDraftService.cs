using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Access;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Attendance.Recording;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.Scopes;

namespace TPR10.Api.Attendance.Corrections;

public sealed class CorrectionDraftService(Tpr10DbContext db, ScopeAccess access, IAttendanceAccess attendance,
    IAuditEventWriter audit, TimeProvider clock)
{
    public Task<CorrectionResult<CorrectionView>> CreateAsync(SaveCorrection input, Guid commandId, CancellationToken ct) =>
        WriteAsync(null, input, new(commandId, 0, 0, null), ct);
    public Task<CorrectionResult<CorrectionView>> SaveAsync(Guid id, SaveCorrection input, CorrectionCommand command, CancellationToken ct) =>
        WriteAsync(id, input, command, ct);

    private async Task<CorrectionResult<CorrectionView>> WriteAsync(Guid? id, SaveCorrection input, CorrectionCommand command, CancellationToken ct)
    {
        try
        {
            await using var tx = await ScopeOperation.BeginAsync(db, ct);
            var identity = await access.ValidateSessionAsync(false, ct);
            if (identity.Status is { } denied) return Failure(denied);
            var actor = identity.ActorId;
            await AttendanceEmployeeLock.AcquireAsync(db, actor, ct);
            if (!(await attendance.DescribeAsync(ct)).CanRecord) return Failure(403);
            if (command is null || command.CommandId == Guid.Empty || input is null || input.Proposal is null
                || id == Guid.Empty || (id is not null && (command.ExpectedVersion < 1 || command.Revision < 1))) return Failure(400);
            var action = id is null ? "create" : "save";
            var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { id, input, command, action })));
            var receipt = await db.Set<CorrectionCommandReceipt>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.ActorId == actor && x.CommandId == command.CommandId, ct);
            if (receipt is not null)
            {
                if (receipt.Action != action || receipt.PayloadSha256 != hash) return Failure(409);
                if (!await db.Set<CorrectionRequest>().AnyAsync(x => x.Id == receipt.RequestId && x.EmployeeId == actor, ct)) return Failure(404);
                return new(JsonSerializer.Deserialize<CorrectionView>(receipt.ResultJson), receipt.Status, null);
            }
            var now = clock.GetUtcNow();
            CorrectionRequest? row = null;
            if (id is not null)
            {
                row = await db.Set<CorrectionRequest>().SingleOrDefaultAsync(x => x.Id == id && x.EmployeeId == actor, ct);
                if (row is null) return Failure(404);
                if (row.Version != command.ExpectedVersion || row.CurrentRevision != command.Revision) return Failure(409);
                if (row.State is not (CorrectionState.Draft or CorrectionState.Returned)) return Failure(409);
                if (row.OriginalWorkDate != input.OriginalWorkDate || row.DayId != input.DayId) return Failure(409);
                if (now >= row.DeadlineUtc)
                {
                    row.State = CorrectionState.Expired; row.Version++;
                    await Audit(actor, row, "expired", ct);
                    await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
                    return Failure(400);
                }
            }
            if (!Valid(input, now)) return Failure(400);
            if (id is null && input.DayId is null && input.OriginalWorkDate != input.Proposal.WorkDate) return Failure(400);
            var deadline = CorrectionDeadline.First(input.OriginalWorkDate);
            if (row is null && now >= deadline) return Failure(400);
            if (row is null && await db.Set<CorrectionRequest>().AnyAsync(x => x.EmployeeId == actor
                && x.OriginalWorkDate == input.OriginalWorkDate && x.State != CorrectionState.Approved, ct)) return Failure(409);
            if (await new CorrectionTargetGuard(db, clock).ValidateAsync(actor, id, input.OriginalWorkDate,
                input.Proposal.WorkDate, true, ct) is { } targetError) return Failure(targetError);
            var history = new CorrectionSiteHistory(db);
            if (await history.ValidateAsync(actor, input.Proposal, ct) is { } invalid) return Failure(invalid);
            var membership = await history.Memberships(actor, input.Proposal.InUtc).SingleAsync(ct);
            var day = input.DayId is { } dayId
                ? await db.Set<AttendanceDay>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == dayId && x.EmployeeId == actor, ct) : null;
            if (input.DayId is not null && day is null) return Failure(404);
            if (day is not null && (day.WorkDate != input.OriginalWorkDate || day.Version != input.ExpectedDayVersion
                || day.MembershipId != membership.Id)) return Failure(409);
            if (day is null && (input.ExpectedDayVersion is not null || await db.Set<AttendanceDay>().AnyAsync(x => x.EmployeeId == actor
                && x.WorkDate == input.OriginalWorkDate, ct))) return Failure(409);
            if (await db.Set<AttendanceDay>().AnyAsync(x => x.EmployeeId == actor && x.WorkDate == input.Proposal.WorkDate
                && x.Id != input.DayId, ct)) return Failure(409);
            if (row is not null && row.MembershipId != membership.Id) return Failure(409);
            if (row is null)
            {
                row = new()
                {
                    Id = Guid.NewGuid(),
                    EmployeeId = actor,
                    OriginalWorkDate = input.OriginalWorkDate,
                    DayId = input.DayId,
                    MembershipId = membership.Id,
                    WorkspaceId = membership.WorkspaceId,
                    DepartmentId = membership.DepartmentId,
                    SnapshotAtUtc = day?.SnapshotAtUtc ?? input.Proposal.InUtc.ToUniversalTime(),
                    DeadlineAnchorUtc = deadline,
                    DeadlineUtc = deadline
                };
                db.Add(row); await db.SaveChangesAsync(ct);
            }
            var proposal = input.Proposal with { Reason = input.Proposal.Reason.Trim(), InUtc = input.Proposal.InUtc.ToUniversalTime(), OutUtc = input.Proposal.OutUtc?.ToUniversalTime() };
            var revision = new CorrectionRevision
            {
                RequestId = row.Id,
                Revision = row.CurrentRevision + 1,
                BaselineDayVersion = input.ExpectedDayVersion,
                WorkDate = proposal.WorkDate,
                InUtc = proposal.InUtc,
                OutUtc = proposal.OutUtc,
                InWorkspaceId = proposal.InSite.WorkspaceId,
                InProjectId = proposal.InSite.ProjectId,
                InSiteId = proposal.InSite.SiteId,
                OutWorkspaceId = proposal.OutSite?.WorkspaceId,
                OutProjectId = proposal.OutSite?.ProjectId,
                OutSiteId = proposal.OutSite?.SiteId,
                Reason = proposal.Reason,
                CreatedAtUtc = DirectoryQueries.Now(clock)
            };
            db.Add(revision); await db.SaveChangesAsync(ct);
            row.CurrentRevision = revision.Revision; row.Version++;
            var view = new CorrectionView(row.Id, actor, row.OriginalWorkDate, row.Version, row.State, row.DeadlineUtc,
                row.CurrentRevision, proposal, revision.BaselineDayVersion, ["save", "submit"], "Own");
            var status = id is null ? 201 : 200;
            db.Add(new CorrectionCommandReceipt
            {
                ActorId = actor,
                CommandId = command.CommandId,
                RequestId = row.Id,
                Action = action,
                PayloadSha256 = hash,
                Status = status,
                ResultJson = JsonSerializer.Serialize(view),
                CreatedAtUtc = DirectoryQueries.Now(clock)
            });
            await Audit(actor, row, action, ct);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return new(view, status, null);
        }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return Failure(409); }
        catch (Exception error) when (ScopeOperation.IsDatabaseFault(error)) { db.ChangeTracker.Clear(); return Failure(503); }
    }

    private Task Audit(Guid actor, CorrectionRequest row, string action, CancellationToken ct) =>
        audit.WriteAsync(new SecurityAuditRequest(actor, null, row.WorkspaceId, null, null,
            "attendance.correction." + action, "correction-request", row.Id, "success",
            new Dictionary<string, string> { ["evidence-reference"] = $"revision:{row.CurrentRevision}" }), ct);

    private static bool Valid(SaveCorrection input, DateTimeOffset now)
    {
        var p = input.Proposal;
        return input.OriginalWorkDate > DateOnly.MinValue && input.OriginalWorkDate <= DateOnly.MaxValue.AddDays(-8)
            && input.OriginalWorkDate <= AttendancePairing.WorkDate(now) && p.InUtc > DateTimeOffset.MinValue && p.InUtc <= now
            && p.WorkDate == AttendancePairing.WorkDate(p.InUtc) && Exact(p.InSite)
            && p.Reason?.Trim().Length is >= 1 and <= 2000
            && (p.OutUtc is { } end ? end > p.InUtc && end <= now && Exact(p.OutSite) : p.OutSite is null);
    }
    private static bool Exact(CorrectionSite? s) => s is not null && s.WorkspaceId != Guid.Empty && s.ProjectId != Guid.Empty && s.SiteId != Guid.Empty;
    private static CorrectionResult<CorrectionView> Failure(int status) => new(null, status, ScopeOperation.ProblemType(status));
}
