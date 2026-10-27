using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using static TPR10.Api.FieldAdvances.Payments.InstallmentService;

namespace TPR10.Api.FieldAdvances.Payments;

public sealed class InstallmentReviewService(Tpr10DbContext db, PaymentCommandExecutor executor, FinancePersonResolver people, TimeProvider clock)
{
    public async Task<FinanceResult<PaymentView>> ReviewAsync(Guid actor, Guid installmentId, string outcome, PaymentCommand meta, CancellationToken ct)
    {
        var requestId = await RequestIdAsync(db, installmentId, ct);
        if (requestId is null) return Denied();
        return await executor.ExecuteAsync(actor, requestId.Value, installmentId, "check", meta,
            PaymentCommandExecutor.Hash(new { Operation = "installment.review", Outcome = outcome }), async token =>
            {
                if (outcome is not ("Approve" or "Return")) return Invalid();
                var installment = await db.Set<AdvanceInstallment>().SingleAsync(x => x.Id == installmentId, token);
                if (installment.State != "PendingReview" || !await EligibleAsync(db, requestId.Value, installmentId, token)) return Conflict();
                var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == requestId, token);
                var current = await db.Set<AdvanceInstallmentRevision>().SingleOrDefaultAsync(x => x.InstallmentId == installmentId && x.IsCurrent, token);
                if (current is null || current.ReviewedAtUtc is not null || !ValidRevision(current, request.OwnerPersonId)
                    || !await ReservationMatchesAsync(db, installment, current.Amount, token)) return Conflict();
                var person = await people.ResolveAsync(actor, request.WorkspaceId, token);
                if (person is null) return Denied();
                var submitted = await db.Set<AdvancePaymentEvent>().AsNoTracking().SingleOrDefaultAsync(x =>
                    x.RequestId == requestId && x.InstallmentId == installmentId
                    && x.InstallmentVersion == meta.InstallmentVersion && x.Kind == "prepare", token);
                var recovery = submitted is null
                    ? await PaymentOutcomeService.UnpaidProvenanceAsync(db, current, meta.InstallmentVersion, token) : null;
                var proposedBy = submitted?.ActorId ?? recovery?.ProposedBy;
                var proposedPerson = submitted?.ActorPersonId ?? recovery?.ProposedByPersonId;
                if (proposedBy is null || proposedPerson is null || proposedPerson == person) return Conflict();
                var at = clock.GetUtcNow();
                // Retain every decision, including repeated Returns on the same unchanged revision.
                // Snapshot references immutable content; account details stay in the protected revision table.
                db.Add(new AdvancePaymentProposal
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = installment.WorkspaceId,
                    ProjectId = installment.ProjectId,
                    FamilyId = installment.FamilyId,
                    RequestId = requestId.Value,
                    InstallmentId = installmentId,
                    CommandId = meta.CommandId,
                    Kind = "InstallmentReview",
                    State = outcome == "Approve" ? "Approved" : "Returned",
                    FamilyVersion = meta.FamilyVersion,
                    RequestVersion = meta.RequestVersion,
                    InstallmentVersion = meta.InstallmentVersion,
                    ContentHash = current.ContentHash,
                    BeforeJson = JsonSerializer.Serialize(new { RevisionId = current.Id, current.RevisionNumber, current.ContentHash }),
                    AfterJson = JsonSerializer.Serialize(new { RevisionId = current.Id, current.ContentHash, Outcome = outcome }),
                    ProposedBy = proposedBy.Value,
                    ProposedByPersonId = proposedPerson.Value,
                    ReviewedBy = actor,
                    ReviewedByPersonId = person.Value,
                    ReviewedAtUtc = at,
                    Reason = meta.Reason,
                    CreatedAtUtc = at
                });
                if (outcome == "Approve")
                {
                    current.ReviewedBy = actor; current.ReviewedByPersonId = person; current.ReviewedAtUtc = at;
                    installment.State = "Reviewed";
                }
                else installment.State = "Returned";
                return Success(requestId.Value, installmentId);
            }, ct);
    }
}
