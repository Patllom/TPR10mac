using TPR10.Api.Attendance.Access;

namespace TPR10.Api.Attendance.Evidence;

public enum EvidenceAction { CheckIn, CheckOut }
public enum EvidenceState { Reserved, Prepared, Published, Orphan }
public enum CopyState { Pending, Verified, Active, Fallback, Quarantined }
public sealed record StampRequest(DateTimeOffset OccurredAtUtc, EvidenceAction Action);
public sealed record StampedImage(byte[] Jpeg, string Sha256, int Width, int Height, string StampText);
public sealed record EvidenceReservation(Guid EvidenceId, Guid OperationId, Guid StorageId, long StorageVersion, string ObjectKey);
public sealed record PreparedEvidence(Guid EvidenceId, Guid OperationId, Guid LocationId, string Sha256, long Length, int Width, int Height);
public sealed record EvidencePublication(Guid EvidenceId, Guid OperationId, Guid EventId, EmploymentSnapshot Subject, StampRequest Stamp);
