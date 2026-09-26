using System.Text.Json.Serialization;
using TPR10.Api.Attendance.Access;
using TPR10.Api.Attendance.Recording;
using TPR10.Api.Scopes;

namespace TPR10.Api.Attendance.History;

public enum AttendanceAudience { Mine, Team, Hr }
public sealed record AttendanceListQuery(DateOnly From, DateOnly To, int PageSize = 20, string? Cursor = null);
public sealed record AttendancePage(AttendanceDayView[] Items, string? NextCursor);
public sealed record CurrentAttendanceView(AttendanceDayView? OpenDay, bool TodayCompleted, DateTimeOffset ServerNowUtc);
public sealed record AttendancePhotoView(Guid EvidenceId);
public sealed record AttendanceEventView(Guid Id, DateTimeOffset OccurredAtUtc, ScopeKey Site, GpsSample Gps,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AttendancePhotoView? Photo);
public sealed record AttendanceDayView(Guid Id, DateOnly WorkDate, long Version, Guid EmployeeId,
    AttendanceEventView In, AttendanceEventView? Out, AttendanceReadBasis ReadBasis,
    DateTimeOffset EffectiveInUtc, DateTimeOffset? EffectiveOutUtc);
