using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Scopes;

namespace TPR10.Api.Attendance.Recording;

public sealed record CaptureChallengeRequest(EvidenceAction Action, ScopeKey Site, Guid IdempotencyKey);
public sealed record CaptureChallengeView(Guid Id, DateTimeOffset ServerNowUtc, DateTimeOffset ExpiresAtUtc);
public sealed record GpsSample(double Latitude, double Longitude, double AccuracyMeters, DateTimeOffset SampledAtUtc);
public sealed record CaptureMetadata(Guid ChallengeId, Guid IdempotencyKey, ScopeKey Site,
    DateTimeOffset DeviceCapturedAtUtc, GpsSample Gps, bool ConfirmContinuousWork, long? ExpectedDayVersion);
public sealed record CaptureReceipt(Guid OperationId, Guid DayId, Guid EventId, DateOnly WorkDate,
    DateTimeOffset OccurredAtUtc, Guid EvidenceId, long DayVersion);
public sealed record RecordingResult<T>(T? Value, int Status, string? Code, int? RetryAfterSeconds);
public sealed record CaptureOperationView(string State, CaptureReceipt? Receipt);
