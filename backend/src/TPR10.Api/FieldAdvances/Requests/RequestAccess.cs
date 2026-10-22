using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;

namespace TPR10.Api.FieldAdvances.Requests;

public sealed class RequestAccess(Tpr10DbContext db, FinanceAccess access, FinancePersonResolver people, TimeProvider clock)
{
    // Caller holds transaction and identity advisory lock 7241002 until its protected operation ends.
    // Creation has no request ID: DraftService must check its scope and owner eligibility separately.
    public async Task<FinanceDecision> CheckAsync(Guid actorId, Guid requestId, string action, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null || actorId == Guid.Empty || requestId == Guid.Empty
            || action is not ("read" or "edit" or "send-to-owner" or "confirm" or "return-to-author"
                or "review" or "revise" or "withdraw" or "copy")) return Denied();
        var request = await db.Set<AdvanceRequest>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == requestId, ct);
        if (request is null) return Denied();
        var scope = new FinanceScope(request.WorkspaceId, request.ProjectId);
        var person = await people.ResolveAsync(actorId, request.WorkspaceId, ct);
        if (person is null) return Denied();
        var owner = person == request.OwnerPersonId && (await access.CheckAsync(actorId, scope, "request.create", ct)).Allowed;
        var delegated = person == request.DelegatePersonId && (await access.CheckAsync(actorId, scope, "request.delegate", ct)).Allowed;
        var author = request.DelegatePersonId is null ? owner : delegated;
        var allowed = action switch
        {
            "read" => owner || delegated || (await access.CheckAsync(actorId, scope, "report.read", ct)).Allowed
                || await CanReviewAsync(actorId, person.Value, request, ct),
            "edit" => author && request.State == "Draft",
            "send-to-owner" => delegated && request.State == "Draft",
            "confirm" => owner && (request.State == "AwaitingOwner" || request.State == "Draft" && request.DelegatePersonId is null),
            "return-to-author" => owner && request.DelegatePersonId is not null && request.State == "AwaitingOwner",
            "review" => await CanReviewAsync(actorId, person.Value, request, ct),
            "revise" => author && request.State == "Returned",
            "withdraw" => owner && request.State is "Draft" or "AwaitingOwner" or "PendingApproval" or "Held" or "Returned",
            // This authorizes reading the terminal source only. The copy command must separately
            // check create/delegate and owner eligibility for the NEW request in DraftService.
            "copy" => request.State is "Rejected" or "Withdrawn"
                && (owner || delegated || (await access.CheckAsync(actorId, scope, "report.read", ct)).Allowed),
            _ => false
        };
        return allowed ? new(true, "allowed") : Denied();
    }

    private async Task<bool> CanReviewAsync(Guid actor, Guid person, AdvanceRequest request, CancellationToken ct)
    {
        if (request.State != "PendingApproval" || !(await access.CheckAsync(actor,
            new(request.WorkspaceId, request.ProjectId), "request.approve", ct)).Allowed) return false;
        var revision = await db.Set<AdvanceRequestRevision>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.RequestId == request.Id && x.IsCurrent, ct);
        if (revision is null || revision.SubmittedAtUtc is null || revision.Total <= 0) return false;
        var steps = await db.Set<AdvanceApprovalStep>().AsNoTracking().Where(x => x.RevisionId == revision.Id)
            .OrderBy(x => x.StepOrder).ToArrayAsync(ct);
        var next = steps.FirstOrDefault(x => x.Outcome != "Approved");
        if (next is null || next.Outcome is not null || next.AssigneePersonId != person || next.HoldCode is not null) return false;
        var participants = await db.Set<AdvanceRequestParticipant>().AsNoTracking().Where(x => x.RequestId == request.Id).ToArrayAsync(ct);
        Guid[] Role(string role) => participants.Where(x => x.Role == role).Select(x => x.PersonId).Distinct().ToArray();
        var approved = steps.Where(x => x.Outcome == "Approved").Select(x => x.DecidedByPersonId ?? Guid.Empty).ToArray();
        var allApprovers = await db.Set<AdvanceApprovalStep>().AsNoTracking()
            .Where(x => x.RequestId == request.Id && x.Outcome == "Approved")
            .Select(x => x.DecidedByPersonId ?? Guid.Empty).ToArrayAsync(ct);
        RequestParticipationHistory history = new(
            Role("Owner").Append(request.OwnerPersonId).Distinct().ToArray(),
            Role("Delegate").Concat(request.DelegatePersonId is { } d ? [d] : Array.Empty<Guid>()).Distinct().ToArray(),
            Role("Approver").Concat(allApprovers).Distinct().ToArray(), Role("Checker"), Role("Payer"));
        if (!RequestSeparation.Check(person, "request.approve", history, approved).Allowed) return false;
        var now = clock.GetUtcNow();
        // One current source supplies capability, exact level and the entire amount together.
        return await (from assignment in db.Set<GrantAssignment>().AsNoTracking()
                      join proposal in db.Set<GrantProposal>() on assignment.ProposalId equals proposal.Id
                      join review in db.Set<GrantReview>() on assignment.ReviewId equals review.Id
                      where assignment.WorkspaceId == request.WorkspaceId && assignment.ProjectId == request.ProjectId
                          && proposal.WorkspaceId == assignment.WorkspaceId && proposal.ProjectId == assignment.ProjectId
                          && review.WorkspaceId == assignment.WorkspaceId && review.ProposalId == proposal.Id
                          && assignment.RevokedAtUtc == null && assignment.CreatedAtUtc <= now
                          && proposal.State == "Approved" && review.Outcome == "Approved"
                          && proposal.CreatedAtUtc <= now && review.CreatedAtUtc <= now
                          && proposal.RecipientId == actor && proposal.RecipientPersonId == person
                          && proposal.ProposedByPersonId != person && review.ReviewerPersonId != person
                          && review.ReviewerPersonId != proposal.ProposedByPersonId
                          && proposal.StartsAtUtc <= now && (proposal.EndsAtUtc == null || now < proposal.EndsAtUtc)
                          && proposal.Capabilities.Contains("request.approve") && proposal.ApprovalLevels.Contains(next.LevelCode)
                          && proposal.ApprovalLimit != null && proposal.ApprovalLimit >= revision.Total
                      select assignment.Id).AnyAsync(ct);
    }

    private static FinanceDecision Denied() => new(false, "finance-access-denied");
}
