namespace TPR10.Api.Attendance.Corrections;

public static class CorrectionDeadline
{
    private static readonly TimeZoneInfo Bangkok = TimeZoneInfo.FindSystemTimeZoneById("Asia/Bangkok");
    public static DateTimeOffset First(DateOnly date) => new(TimeZoneInfo.ConvertTimeToUtc(date.AddDays(8).ToDateTime(TimeOnly.MinValue), Bangkok));
    public static DateTimeOffset Returned(DateTimeOffset at) => First(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, Bangkok).DateTime));
}
