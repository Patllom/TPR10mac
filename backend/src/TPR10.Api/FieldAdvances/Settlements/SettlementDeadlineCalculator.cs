namespace TPR10.Api.FieldAdvances.Settlements;

public static class SettlementDeadlineCalculator
{
    private static readonly TimeZoneInfo Bangkok = TimeZoneInfo.FindSystemTimeZoneById("Asia/Bangkok");

    public static DateOnly DueOn(DateOnly end, int days)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(days);
        return end.AddDays(days);
    }

    public static DateOnly LocalDate(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, Bangkok).DateTime);
    public static bool OnTime(DateTimeOffset submittedAt, DateOnly due) => LocalDate(submittedAt) <= due;
}
