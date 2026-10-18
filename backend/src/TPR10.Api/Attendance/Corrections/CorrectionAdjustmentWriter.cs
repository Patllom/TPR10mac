using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Attendance.Recording;
using TPR10.Api.Data;

namespace TPR10.Api.Attendance.Corrections;

// Stages the effective time and its ledger entry in the caller's transaction; never commits.
public sealed class CorrectionAdjustmentWriter(Tpr10DbContext db, TimeProvider clock)
{
    public async Task<int?> ApplyAsync(Guid requestId, int revision, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Correction requires caller transaction");
        var request = await db.Set<CorrectionRequest>().SingleAsync(x => x.Id == requestId, ct);
        await AttendanceEmployeeLock.AcquireAsync(db, request.EmployeeId, ct);
        if (request.CurrentRevision != revision || request.State != CorrectionState.Approved) return 409;
        var proposal = await db.Set<CorrectionRevision>().AsNoTracking().SingleAsync(x => x.RequestId == requestId && x.Revision == revision, ct);
        var now = DirectoryQueries.Now(clock);
        if (proposal.InUtc > now || proposal.WorkDate != AttendancePairing.WorkDate(proposal.InUtc)
            || (proposal.OutUtc is { } end && (end <= proposal.InUtc || end > now))) return 409;
        var history = new CorrectionSiteHistory(db);
        if (await history.ValidateAsync(request.EmployeeId, CorrectionApproval.Proposal(proposal), ct) is { } invalid) return invalid;
        if (!await history.Memberships(request.EmployeeId, proposal.InUtc).AnyAsync(x => x.Id == request.MembershipId, ct)) return 409;
        var targetDayId = request.DayId ?? proposal.RecoveryDayId;
        var day = targetDayId is { } dayId ? await db.Set<AttendanceDay>().SingleOrDefaultAsync(x => x.Id == dayId, ct) : null;
        if (targetDayId is not null && (day is null || day.EmployeeId != request.EmployeeId
            || day.Version != proposal.BaselineDayVersion || day.MembershipId != request.MembershipId
            || day.WorkspaceId != request.WorkspaceId || day.DepartmentId != request.DepartmentId
            || (request.DayId is not null ? day.SnapshotAtUtc != request.SnapshotAtUtc : day.WorkDate != request.OriginalWorkDate))) return 409;
        if (day is null && proposal.BaselineDayVersion is not null) return 409;
        if (await new CorrectionTargetGuard(db, clock).ValidateAsync(request.EmployeeId, request.Id,
            request.OriginalWorkDate, proposal.WorkDate, false, ct) is { } targetError) return targetError;
        if (proposal.OutUtc is null && day is not null && await db.Set<AttendanceEvent>()
            .AnyAsync(x => x.DayId == day.Id && x.Action == EvidenceAction.CheckOut, ct)) return 409;
        if (await db.Set<AttendanceDay>().AnyAsync(x => x.EmployeeId == request.EmployeeId && x.Id != targetDayId
            && (x.WorkDate == proposal.WorkDate || (proposal.OutUtc == null && x.EffectiveOutUtc == null)), ct)) return 409;
        var adjustment = new AttendanceAdjustment
        {
            Id = Guid.NewGuid(),
            RequestId = requestId,
            Revision = revision,
            DayId = day?.Id ?? Guid.NewGuid(),
            BeforeVersion = day?.Version,
            BeforeWorkDate = day?.WorkDate,
            BeforeInUtc = day?.EffectiveInUtc,
            BeforeOutUtc = day?.EffectiveOutUtc,
            AfterVersion = day is null ? 1 : day.Version + 1,
            AfterWorkDate = proposal.WorkDate,
            AfterInUtc = proposal.InUtc,
            AfterOutUtc = proposal.OutUtc,
            AppliedAtUtc = now
        };
        if (day is null)
        {
            day = new AttendanceDay
            {
                Id = adjustment.DayId,
                EmployeeId = request.EmployeeId,
                MembershipId = request.MembershipId,
                WorkspaceId = request.WorkspaceId,
                DepartmentId = request.DepartmentId,
                SnapshotAtUtc = request.SnapshotAtUtc
            };
            db.Add(day);
        }
        day.WorkDate = proposal.WorkDate; day.EffectiveInUtc = proposal.InUtc;
        day.EffectiveOutUtc = proposal.OutUtc; day.Version = adjustment.AfterVersion;
        db.Add(adjustment);
        return null;
    }
}
