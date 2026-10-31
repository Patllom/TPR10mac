using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Evidence;
using static TPR10.Api.FieldAdvances.Payments.InstallmentService;

namespace TPR10.Api.FieldAdvances.Payments;

public sealed class PaymentTakeoverService(Tpr10DbContext db, PaymentCommandExecutor executor, FinancePersonResolver people,
    FinanceEvidenceService evidence, FinanceEvidenceOptions options, TimeProvider clock)
{
    public async Task<FinanceResult<PaymentView>> ProposeAsync(Guid actor, Guid installmentId, Guid[] evidenceIds, PaymentCommand meta, CancellationToken ct)
    {
        var requestId = await RequestIdAsync(db, installmentId, ct);
        if (requestId is null) return Denied();
        var recovery = new PaymentRecovery(db, people, evidence, options, clock);
        var prepared = await recovery.PrepareAsync(actor, requestId.Value, evidenceIds, meta, false, ct);
        return await executor.ExecuteAsync(actor, requestId.Value, installmentId, "takeover", meta,
            PaymentCommandExecutor.Hash(new { Operation = "takeover.propose", EvidenceIds = evidenceIds }), async token =>
            {
                var installment = await db.Set<AdvanceInstallment>().SingleAsync(x => x.Id == installmentId, token);
                var attempt = await recovery.PendingAttemptAsync(installment, token);
                if (attempt is null) return Conflict();
                var owner = await PaymentRecovery.OwnerAsync(db, attempt, token);
                var person = await people.ResolveAsync(actor, installment.WorkspaceId, token);
                if (person is null) return Denied();
                if (owner.Actor == Guid.Empty || person == owner.Person) return Conflict();
                var revision = await recovery.RevisionAsync(attempt, token);
                if (revision is null || !await ReservationMatchesAsync(db, installment, revision.Amount, token)) return Conflict();
                if (!await recovery.CheckEvidenceAsync(prepared, evidenceIds, installment, token)) return Invalid();
                var snapshot = new RecoverySnapshot(attempt.Id, revision.Id, revision.ContentHash, owner,
                    new("Unknown", null, evidenceIds), prepared.Rows, null);
                db.Add(recovery.Proposal(installment, actor, person.Value, "PaymentTakeover", snapshot, meta));
                return Success(requestId.Value, installmentId);
            }, ct);
    }

    public async Task<FinanceResult<PaymentView>> ConfirmAsync(Guid actor, Guid proposalId, PaymentCommand meta, CancellationToken ct)
    {
        var found = await db.Set<AdvancePaymentProposal>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == proposalId && x.Kind == "PaymentTakeover", ct);
        if (found?.InstallmentId is not { } installmentId) return Denied();
        var recovery = new PaymentRecovery(db, people, evidence, options, clock);
        var snapshot = PaymentRecovery.Snapshot(found);
        var ids = snapshot is null ? [] : PaymentRecovery.EvidenceIds(snapshot.Input);
        var prepared = await recovery.PrepareAsync(actor, found.RequestId, ids, meta, true, ct);
        return await executor.ExecuteAsync(actor, found.RequestId, installmentId, "reconcile", meta,
            PaymentCommandExecutor.Hash(new { Operation = "takeover.confirm", ProposalId = proposalId }), async token =>
            {
                var proposal = await db.Set<AdvancePaymentProposal>().SingleAsync(x => x.Id == proposalId, token);
                var installment = await db.Set<AdvanceInstallment>().SingleAsync(x => x.Id == installmentId, token);
                var current = PaymentRecovery.Snapshot(proposal);
                var attempt = await recovery.PendingAttemptAsync(installment, token);
                if (current is null || attempt is null || attempt.Id != current.AttemptId || !PaymentRecovery.CurrentProposal(proposal, meta)
                    || PaymentCommandExecutor.Hash(snapshot) != PaymentCommandExecutor.Hash(current)
                    || await PaymentRecovery.OwnerAsync(db, attempt, token) != current.Owner) return Conflict();
                var person = await people.ResolveAsync(actor, installment.WorkspaceId, token);
                if (person is null || person == proposal.ProposedByPersonId || !await recovery.CurrentPayerAsync(proposal, token)) return Denied();
                PaymentCommandExecutor.RegisterFinalAuthorization(db, found.RequestId, installmentId, meta.CommandId,
                    finalToken => recovery.CurrentPayerAsync(proposal, finalToken));
                var revision = await recovery.RevisionAsync(attempt, token);
                if (revision is null || revision.Id != current.RevisionId || revision.ContentHash != current.RevisionHash
                    || !await ReservationMatchesAsync(db, installment, revision.Amount, token)) return Conflict();
                if (!await recovery.CheckEvidenceAsync(prepared, ids, installment, token)
                    || PaymentCommandExecutor.Hash(prepared.Rows) != PaymentCommandExecutor.Hash(current.Evidence)) return Conflict();
                recovery.Approve(proposal, actor, person.Value);
                // No changes to attempt operator, reservations, holds or review. This owner can only reconcile.
                return Success(found.RequestId, installmentId);
            }, ct);
    }
}
