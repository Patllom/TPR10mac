using System.Globalization;

namespace TPR10.Api.Attendance.Evidence;

public static class StampFormatter
{
    private static readonly TimeZoneInfo Bangkok = TimeZoneInfo.FindSystemTimeZoneById("Asia/Bangkok");

    public static string Format(StampRequest stamp)
    {
        var action = stamp.Action switch
        {
            EvidenceAction.CheckIn => "เข้า",
            EvidenceAction.CheckOut => "ออก",
            _ => throw new ArgumentOutOfRangeException(nameof(stamp))
        };
        var thai = TimeZoneInfo.ConvertTime(stamp.OccurredAtUtc, Bangkok);
        return thai.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " (UTC+7) — " + action;
    }
}
