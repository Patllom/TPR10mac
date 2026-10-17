using TPR10.Api.Scopes;

namespace TPR10.Api.Attendance.Recording;

public static class CaptureValidation
{
    internal static bool ExactSite(ScopeKey? site) => site is { IsValid: true, ProjectId: not null, SiteId: not null };

    public static string? Validate(CaptureMetadata metadata, DateTimeOffset now)
    {
        if (metadata is null || metadata.ChallengeId == Guid.Empty || metadata.IdempotencyKey == Guid.Empty
            || !ExactSite(metadata.Site) || metadata.ExpectedDayVersion is <= 0) return "invalid-capture";
        if (metadata.DeviceCapturedAtUtc == DateTimeOffset.MinValue || metadata.DeviceCapturedAtUtc == DateTimeOffset.MaxValue
            || metadata.DeviceCapturedAtUtc > now) return "invalid-capture-time";
        if (metadata.Gps is not { } gps || !double.IsFinite(gps.Latitude) || gps.Latitude is < -90 or > 90
            || !double.IsFinite(gps.Longitude) || gps.Longitude is < -180 or > 180
            || !double.IsFinite(gps.AccuracyMeters) || gps.AccuracyMeters <= 0
            || gps.SampledAtUtc > now || now - gps.SampledAtUtc > TimeSpan.FromSeconds(60)) return "invalid-gps";
        return null;
    }
}
