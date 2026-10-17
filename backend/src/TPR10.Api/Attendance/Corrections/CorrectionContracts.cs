namespace TPR10.Api.Attendance.Corrections;

public enum CorrectionState { Draft, PendingSupervisor, PendingHr, Returned, Approved, Expired }
public sealed record CorrectionSite(Guid WorkspaceId, Guid ProjectId, Guid SiteId);
public sealed record CorrectionProposal(DateOnly WorkDate, DateTimeOffset InUtc, DateTimeOffset? OutUtc,
    CorrectionSite InSite, CorrectionSite? OutSite, string Reason);
public sealed record SaveCorrection(Guid? DayId, DateOnly OriginalWorkDate, long? ExpectedDayVersion, CorrectionProposal Proposal);
public sealed record CorrectionCommand(Guid CommandId, long ExpectedVersion, int Revision, string? Reason);
public sealed record CorrectionResult<T>(T? Value, int Status, string? Problem);
public sealed record CorrectionView(Guid Id, Guid EmployeeId, DateOnly OriginalWorkDate, long Version,
    CorrectionState State, DateTimeOffset DeadlineUtc, int CurrentRevision, CorrectionProposal Proposal,
    long? BaselineDayVersion, string[] Actions, string ReadBasis);
