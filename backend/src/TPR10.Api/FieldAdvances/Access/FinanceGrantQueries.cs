using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;

namespace TPR10.Api.FieldAdvances.Access;

internal static class FinanceGrantQueries
{
    // Grant evidence only. Callers retain session/MFA, current account/person resolution,
    // active scope checks and the transaction/identity lock protecting their operation.
    internal static IQueryable<GrantProposal> Current(Tpr10DbContext db, FinanceScope scope,
        string capability, DateTimeOffset now) =>
        from assignment in db.Set<GrantAssignment>().AsNoTracking()
        join proposal in db.Set<GrantProposal>().AsNoTracking() on assignment.ProposalId equals proposal.Id
        join review in db.Set<GrantReview>().AsNoTracking() on assignment.ReviewId equals review.Id
        where assignment.WorkspaceId == scope.WorkspaceId && assignment.ProjectId == scope.ProjectId
            && proposal.WorkspaceId == assignment.WorkspaceId && proposal.ProjectId == assignment.ProjectId
            && review.WorkspaceId == assignment.WorkspaceId && review.ProposalId == proposal.Id
            && assignment.RevokedAtUtc == null && assignment.CreatedAtUtc <= now
            && proposal.State == "Approved" && review.Outcome == "Approved"
            && proposal.CreatedAtUtc <= now && review.CreatedAtUtc <= now
            && proposal.ProposedByPersonId != proposal.RecipientPersonId
            && review.ReviewerPersonId != proposal.RecipientPersonId
            && review.ReviewerPersonId != proposal.ProposedByPersonId
            && proposal.StartsAtUtc <= now && (proposal.EndsAtUtc == null || now < proposal.EndsAtUtc)
            && proposal.Capabilities.Contains(capability)
        select proposal;
}
