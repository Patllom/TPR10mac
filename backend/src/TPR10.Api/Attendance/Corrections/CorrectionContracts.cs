using System.Text.Json.Serialization;

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
    long? BaselineDayVersion, string[] Actions, string ReadBasis)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CorrectionHistoryView? History { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CorrectionComparison? Comparison { get; init; }
}
public sealed record CorrectionTimePair(DateTimeOffset? InUtc, DateTimeOffset? OutUtc);
public sealed record CorrectionEffective(long Version, DateOnly WorkDate, DateTimeOffset InUtc, DateTimeOffset? OutUtc,
    CorrectionSite? InSite, CorrectionSite? OutSite);
public sealed record CorrectionComparison(Guid? DayId, bool BaselineMatches, CorrectionTimePair? Original, CorrectionEffective? Current);
public sealed record CorrectionPage(CorrectionView[] Items, int Total, int PageNumber, int PageSize);
public sealed record CorrectionRevisionView(int Revision, long? BaselineDayVersion, CorrectionProposal Proposal, DateTimeOffset CreatedAtUtc);
public sealed record CorrectionRouteView(int Revision, Guid SupervisorId, Guid[] HrCandidateIds, DateTimeOffset CreatedAtUtc);
public sealed record CorrectionApprovalView(int Revision, string Step, Guid ActorId, string Outcome, string? Reason, DateTimeOffset OccurredAtUtc);
public sealed record CorrectionRerouteView(int Revision, Guid ActorId, string Reason, DateTimeOffset OccurredAtUtc);
public sealed record CorrectionHistoryView(CorrectionRevisionView[] Revisions, CorrectionRouteView[] Routes,
    CorrectionApprovalView[] Approvals, CorrectionRerouteView[] Reroutes);
