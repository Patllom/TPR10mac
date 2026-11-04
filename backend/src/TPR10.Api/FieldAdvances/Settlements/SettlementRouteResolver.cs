using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Payments;
using TPR10.Api.FieldAdvances.Requests;

namespace TPR10.Api.FieldAdvances.Settlements;

// The caller retains the identity/family transaction through assignment and decision commit.
public sealed class SettlementRouteResolver(Tpr10DbContext db, FinancePersonResolver people, TimeProvider clock)
{
    public async Task<SettlementRoute> ResolveAsync(Guid requestId, Guid roundId, CancellationToken ct)
    {
        var supervisor = await ChooseAsync(requestId, roundId, "Supervisor", [], ct);
        var excluded = supervisor.PersonId is { } s ? new[] { s } : [];
        var manager = await ChooseAsync(requestId, roundId, "ProjectManager", excluded, ct);
        if (manager.PersonId is { } m) excluded = [.. excluded, m];
        var finance = await ChooseAsync(requestId, roundId, "Finance", excluded, ct);
        return new(supervisor, manager, finance);
    }

    internal async Task<RouteChoice> CurrentStepAsync(SettlementApprovalStep step, CancellationToken ct)
    {
        var revision = await db.Set<SettlementRevision>().AsNoTracking().SingleAsync(x => x.Id == step.RevisionId, ct);
        // Earlier decisions are facts, not routes to recalculate after somebody changes position.
        var previous = await db.Set<SettlementApprovalStep>().AsNoTracking()
            .Where(x => x.RevisionId == revision.Id && x.StepOrder < step.StepOrder && x.State == "Accepted")
            .Select(x => x.ReviewedByPersonId ?? Guid.Empty).ToArrayAsync(ct);
        return await ChooseAsync(step.RequestId, revision.RoundId, step.LevelCode, previous, ct, revision.Id);
    }

    private async Task<RouteChoice> ChooseAsync(Guid requestId, Guid roundId, string level, Guid[] excluded, CancellationToken ct, Guid? revisionId = null)
    {
        var traversed = new List<Guid>();
        RouteChoice Hold(string code) => new(null, null, code, traversed.ToArray());
        if (db.Database.CurrentTransaction is null) return Hold("route-invalid-context");
        var request = await db.Set<AdvanceRequest>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == requestId, ct);
        var revision = await db.Set<SettlementRevision>().AsNoTracking().Where(x => x.RoundId == roundId && x.RequestId == requestId && (revisionId == null || x.Id == revisionId))
            .OrderByDescending(x => x.RevisionNumber).FirstOrDefaultAsync(ct);
        if (request is null || revision is null || request.State == "Closed" && !await ExpenseAdjustmentService.ClosedRoundAsync(db, requestId, roundId, ct)) return Hold("route-invalid-context");
        var scope = new FinanceScope(request.WorkspaceId, request.ProjectId);
        var history = await FamilyParticipation.ReadHistoryAsync(db, request.FamilyId, ct);
        if (level == "Finance")
        {
            var grants = await FinanceGrantQueries.Current(db, scope, "finance.check", clock.GetUtcNow())
                .OrderBy(x => x.RecipientPersonId).ThenBy(x => x.RecipientId)
                .Select(x => new { x.RecipientPersonId, x.RecipientId }).Distinct().ToArrayAsync(ct);
            foreach (var grant in grants.OrderBy(x => x.RecipientPersonId).ThenBy(x => x.RecipientId))
            {
                traversed.Add(grant.RecipientPersonId);
                if (RequestSeparation.Check(grant.RecipientPersonId, "finance.check", history, excluded).Allowed
                    && await people.ResolveAsync(grant.RecipientId, request.WorkspaceId, ct) == grant.RecipientPersonId)
                    return new(grant.RecipientPersonId, grant.RecipientId, null, traversed.Distinct().ToArray());
            }
            return Hold("route-no-candidate");
        }
        var code = level == "Supervisor" ? "supervisor" : "project-manager";
        var candidates = new ApprovalCandidateReader(db, people, clock);
        var next = level == "Supervisor" ? await candidates.SupervisorAsync(request.OwnerPersonId, request.WorkspaceId, ct)
            : await candidates.PrimaryAsync(scope, code, ct);
        var seen = new HashSet<Guid>();
        while (next.PersonId is { } person)
        {
            if (!seen.Add(person)) return Hold("route-cycle");
            traversed.Add(person);
            if (RequestSeparation.Check(person, "request.approve", history, excluded).Allowed)
            {
                var account = await candidates.EligibleAccountAsync(scope, person, code, revision.Total, ct);
                if (account is not null) return new(person, account, null, traversed.ToArray());
            }
            next = await candidates.SupervisorAsync(person, request.WorkspaceId, ct);
        }
        return Hold(next.HoldCode ?? "route-no-candidate");
    }
}
