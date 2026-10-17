using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Data;

namespace TPR10.Api.Attendance.Recording;

public static class AttendancePairing
{
    private static readonly TimeZoneInfo Bangkok = TimeZoneInfo.FindSystemTimeZoneById("Asia/Bangkok");
    public static DateOnly WorkDate(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Bangkok).DateTime);

    // Caller holds the identity lock followed by the employee lock.
    internal static async Task<RecordingResult<AttendanceDay>> ValidateAsync(Tpr10DbContext db, Guid actor,
        EvidenceAction action, DateTimeOffset instant, CaptureMetadata metadata, Guid? target, CancellationToken ct)
    {
        var day = await db.Set<AttendanceDay>().SingleOrDefaultAsync(x => x.EmployeeId == actor && x.EffectiveOutUtc == null, ct);
        if (action == EvidenceAction.CheckIn)
        {
            if (metadata.ExpectedDayVersion is not null) return new(null, 400, "unexpected-day-version", null);
            var date = WorkDate(instant);
            return day is not null || await db.Set<AttendanceDay>().AnyAsync(x => x.EmployeeId == actor && x.WorkDate == date, ct)
                ? new(null, 409, "attendance-pair-conflict", null) : new(null, 200, null, null);
        }
        if (day is null || target is { } id && day.Id != id || metadata.ExpectedDayVersion != day.Version
            || instant <= day.EffectiveInUtc || WorkDate(instant) != day.WorkDate && !metadata.ConfirmContinuousWork)
            return new(null, 409, "attendance-pair-conflict", null);
        return new(day, 200, null, null);
    }
}
