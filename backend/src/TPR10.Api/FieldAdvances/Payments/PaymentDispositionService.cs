using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Requests;
using static TPR10.Api.FieldAdvances.Payments.InstallmentService;

namespace TPR10.Api.FieldAdvances.Payments;

public sealed class PaymentDispositionService(Tpr10DbContext db, PaymentCommandExecutor executor,
    FinancePersonResolver people, TimeProvider clock)
{
    public Task<FinanceResult<PaymentView>> ProposeAsync(Guid actor, Guid requestId, string outcome, PaymentCommand meta, CancellationToken ct)
        => executor.ExecuteAsync(actor, requestId, null, "terminate-propose", meta,
            PaymentCommandExecutor.Hash(new { Operation = "disposition.propose", Outcome = outcome }), async token =>
            {
                if (outcome is not ("ResumeOriginal" or "TerminateRemaining")) return Invalid();
                var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == requestId, token);
                var account = await db.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == requestId, token);
                var revision = await db.Set<AdvanceRequestRevision>().SingleAsync(x => x.RequestId == requestId && x.IsCurrent, token);
                var hold = await CeilingAmendmentService.ActiveHoldAsync(db, requestId, token);
                if (!Eligible(request, account, revision, hold) || !await CeilingAmendmentService.UnpaidAsync(db, account, token)) return Conflict();
                var person = await people.ResolveAsync(actor, request.WorkspaceId, token);
                if (person is null) return Denied();
                var snapshot = new DispositionSnapshot(outcome, account.ApprovedRevisionId!.Value, revision.Id, hold!.Id, account.Version);
                var proposal = new AdvancePaymentProposal
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = request.WorkspaceId,
                    ProjectId = request.ProjectId,
                    FamilyId = request.FamilyId,
                    RequestId = requestId,
                    CommandId = meta.CommandId,
                    Kind = "PaymentDisposition",
                    FamilyVersion = checked(meta.FamilyVersion + 1),
                    RequestVersion = checked(meta.RequestVersion + 1),
                    BeforeJson = JsonSerializer.Serialize(snapshot),
                    AfterJson = JsonSerializer.Serialize(new DispositionOutcome(outcome)),
                    ContentHash = "",
                    ProposedBy = actor,
                    ProposedByPersonId = person.Value,
                    Reason = meta.Reason,
                    CreatedAtUtc = clock.GetUtcNow()
                };
                proposal.ContentHash = Hash(proposal, snapshot); db.Add(proposal);
                return CeilingAmendmentService.Success(requestId);
            }, ct);

    public async Task<FinanceResult<PaymentView>> ConfirmAsync(Guid actor, Guid proposalId, PaymentCommand meta, CancellationToken ct)
    {
        var found = await db.Set<AdvancePaymentProposal>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == proposalId && x.Kind == "PaymentDisposition", ct);
        if (found is null || found.InstallmentId is not null) return Denied();
        return await executor.ExecuteAsync(actor, found.RequestId, null, "terminate-confirm", meta,
            PaymentCommandExecutor.Hash(new { Operation = "disposition.confirm", ProposalId = proposalId }), async token =>
            {
                var proposal = await db.Set<AdvancePaymentProposal>().SingleAsync(x => x.Id == proposalId, token);
                DispositionSnapshot? snapshot;
                try { snapshot = JsonSerializer.Deserialize<DispositionSnapshot>(proposal.BeforeJson); }
                catch (JsonException) { return Conflict(); }
                if (snapshot is null || proposal.ContentHash != Hash(proposal, snapshot) || proposal.State != "Pending"
                    || proposal.FamilyVersion != meta.FamilyVersion || proposal.RequestVersion != meta.RequestVersion
                    || snapshot.Outcome is not ("ResumeOriginal" or "TerminateRemaining")) return Conflict();
                var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == found.RequestId, token);
                var account = await db.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == request.Id, token);
                var revision = await db.Set<AdvanceRequestRevision>().SingleAsync(x => x.RequestId == request.Id && x.IsCurrent, token);
                var hold = await CeilingAmendmentService.ActiveHoldAsync(db, request.Id, token);
                if (!Eligible(request, account, revision, hold) || snapshot.AmendmentRevision != revision.Id
                    || snapshot.ApprovedRevision != account.ApprovedRevisionId || snapshot.HoldId != hold!.Id
                    || snapshot.AccountVersion != account.Version || !await CeilingAmendmentService.UnpaidAsync(db, account, token)
                    || snapshot.Outcome == "TerminateRemaining" && !await CeilingAmendmentService.ClearedAsync(db, account, token)) return Conflict();
                var person = await people.ResolveAsync(actor, request.WorkspaceId, token);
                if (person is null || person == proposal.ProposedByPersonId || !await ProposerCurrentAsync(proposal, token)) return Denied();
                PaymentCommandExecutor.RegisterFinalAuthorization(db, request.Id, null, meta.CommandId,
                    finalToken => ProposerCurrentAsync(proposal, finalToken));
                // Retain the rejected/withdrawn revision and its decisions; restore only the active pointer.
                revision.IsCurrent = false; await db.SaveChangesAsync(token);
                var original = await db.Set<AdvanceRequestRevision>().SingleAsync(x => x.Id == account.ApprovedRevisionId, token);
                original.IsCurrent = true; request.State = "Approved"; request.HoldCode = null;
                if (snapshot.Outcome == "TerminateRemaining")
                { account.Spendable = 0; account.Version = checked(account.Version + 1); }
                hold.ReleasedAtUtc = clock.GetUtcNow(); hold.ReleasedByPersonId = person;
                proposal.State = "Approved"; proposal.ReviewedBy = actor; proposal.ReviewedByPersonId = person; proposal.ReviewedAtUtc = clock.GetUtcNow();
                return CeilingAmendmentService.Success(request.Id);
            }, ct);
    }

    private async Task<bool> ProposerCurrentAsync(AdvancePaymentProposal proposal, CancellationToken ct)
    {
        return await people.ResolveAsync(proposal.ProposedBy, proposal.WorkspaceId, ct) == proposal.ProposedByPersonId
            && RequestSeparation.Check(proposal.ProposedByPersonId, "finance.check", await FamilyParticipation.ReadHistoryAsync(db, proposal.FamilyId, ct), []).Allowed
            && await FinanceGrantQueries.Current(db, new(proposal.WorkspaceId, proposal.ProjectId), "finance.check", clock.GetUtcNow())
                .AnyAsync(x => x.RecipientId == proposal.ProposedBy && x.RecipientPersonId == proposal.ProposedByPersonId, ct);
    }

    private static bool Eligible(AdvanceRequest request, AdvancePaymentAccount account, AdvanceRequestRevision revision, AdvancePaymentHold? hold)
        => request.State is "Rejected" or "Withdrawn" && account.ApprovedRevisionId is not null
            && revision.Id != account.ApprovedRevisionId && hold is not null;

    private static string Hash(AdvancePaymentProposal p, DispositionSnapshot snapshot) => PaymentCommandExecutor.Hash(new
    {
        p.Id,
        p.WorkspaceId,
        p.ProjectId,
        p.FamilyId,
        p.RequestId,
        p.InstallmentId,
        p.CommandId,
        p.Kind,
        p.FamilyVersion,
        p.RequestVersion,
        p.InstallmentVersion,
        p.ProposedBy,
        p.ProposedByPersonId,
        p.Reason,
        Snapshot = snapshot,
        Outcome = JsonSerializer.Deserialize<DispositionOutcome>(p.AfterJson)
    });
    private sealed record DispositionOutcome(string Outcome);
    private sealed record DispositionSnapshot(string Outcome, Guid ApprovedRevision, Guid AmendmentRevision, Guid HoldId, long AccountVersion);
}
