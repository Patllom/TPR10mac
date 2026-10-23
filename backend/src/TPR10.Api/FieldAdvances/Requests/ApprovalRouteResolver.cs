using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Policies;
using TPR10.Api.Organization.Data;

namespace TPR10.Api.FieldAdvances.Requests;

public sealed class ApprovalRouteResolver(Tpr10DbContext db, FinancePersonResolver people, TimeProvider clock)
{
    // Caller owns transaction and advisory lock 7241002 through assignment/decision commit.
    // Caller persists the current revision's total and frozen snapshot before resolving.
    // This helper does not mutate requests, steps, history, or transaction ownership.
    public Task<RouteChoice> ResolveAsync(Guid requestId, Guid revisionId, string levelCode, CancellationToken ct)
        => ResolveCoreAsync(requestId, revisionId, levelCode, null, ct);

    // ตรวจตำแหน่ง/สิทธิ์ซ้ำหลัง audit ภายใน transaction ที่ลงผลอยู่เท่านั้น
    // ไม่นับผลของขั้นที่เพิ่งลงเองเป็นข้อห้าม แต่ยังคงผลทุกขั้นก่อนหน้าและประวัติทั้งคำขอ
    internal Task<RouteChoice> RecheckDecisionAsync(Guid requestId, Guid revisionId, string levelCode, Guid decidingStepId, CancellationToken ct)
        => ResolveCoreAsync(requestId, revisionId, levelCode, decidingStepId, ct);

    private async Task<RouteChoice> ResolveCoreAsync(Guid requestId, Guid revisionId, string levelCode, Guid? decidingStepId, CancellationToken ct)
    {
        var traversed = new List<Guid>();
        RouteChoice Hold(string code) => new(null, null, code, traversed.ToArray());
        if (db.Database.CurrentTransaction is null || requestId == Guid.Empty || revisionId == Guid.Empty
            || string.IsNullOrWhiteSpace(levelCode)) return Hold("route-invalid-context");
        var request = await db.Set<AdvanceRequest>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == requestId, ct);
        if (request is null) return Hold("route-invalid-context");
        var revision = await db.Set<AdvanceRequestRevision>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == revisionId
            && x.RequestId == requestId && x.WorkspaceId == request.WorkspaceId && x.ProjectId == request.ProjectId && x.IsCurrent, ct);
        if (revision is null || !await db.Set<Project>().AnyAsync(x => x.Id == request.ProjectId
            && x.WorkspaceId == request.WorkspaceId && x.IsActive, ct)) return Hold("route-invalid-context");

        PolicySnapshot? snapshot;
        try { snapshot = JsonSerializer.Deserialize<PolicySnapshot>(revision.PolicySnapshotJson ?? "null"); }
        catch (JsonException) { return Hold("route-invalid-policy"); }
        var levels = ApprovalLevelEvaluator.Evaluate(revision.Total, snapshot!);
        if (levels.Value is null || !levels.Value.Contains(levelCode, StringComparer.Ordinal)) return Hold("route-invalid-level");

        var participants = await db.Set<AdvanceRequestParticipant>().AsNoTracking().Where(x => x.RequestId == requestId).ToArrayAsync(ct);
        var decisions = await db.Set<AdvanceApprovalStep>().AsNoTracking()
            .Where(x => x.RequestId == requestId && x.Outcome == "Approved" && (decidingStepId == null || x.Id != decidingStepId))
            .Select(x => new { x.RevisionId, PersonId = x.DecidedByPersonId ?? Guid.Empty }).ToArrayAsync(ct);
        Guid[] Role(string role) => participants.Where(x => x.Role == role).Select(x => x.PersonId).Distinct().ToArray();
        RequestParticipationHistory history = new(
            Role("Owner").Append(request.OwnerPersonId).Distinct().ToArray(),
            Role("Delegate").Concat(request.DelegatePersonId is { } d ? [d] : Array.Empty<Guid>()).Distinct().ToArray(),
            Role("Approver").Concat(decisions.Select(x => x.PersonId)).Distinct().ToArray(), Role("Checker"), Role("Payer"));
        var approved = decisions.Where(x => x.RevisionId == revisionId).Select(x => x.PersonId).ToArray();
        var scope = new FinanceScope(request.WorkspaceId, request.ProjectId);
        var candidates = new ApprovalCandidateReader(db, people, clock);
        var next = levelCode == "supervisor"
            ? await candidates.SupervisorAsync(request.OwnerPersonId, request.WorkspaceId, ct)
            : await candidates.PrimaryAsync(scope, levelCode, ct);
        var visited = new HashSet<Guid>();
        while (next.PersonId is { } person)
        {
            if (!visited.Add(person)) return Hold("route-cycle");
            traversed.Add(person);
            if (RequestSeparation.Check(person, "request.approve", history, approved).Allowed)
            {
                var account = await candidates.EligibleAccountAsync(scope, person, levelCode, revision.Total, ct);
                if (account is not null) return new(person, account, null, traversed.ToArray());
            }
            next = await candidates.SupervisorAsync(person, request.WorkspaceId, ct);
        }
        return Hold(next.HoldCode ?? "route-no-candidate");
    }
}
