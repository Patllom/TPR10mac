using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Evidence;
using static TPR10.Api.FieldAdvances.Payments.InstallmentService;

namespace TPR10.Api.FieldAdvances.Payments;

public sealed class PaymentHoldService(Tpr10DbContext db, PaymentCommandExecutor executor, FinancePersonResolver people,
    FinanceEvidenceService evidence, FinanceEvidenceOptions options, TimeProvider clock)
{
    public async Task<FinanceResult<PaymentView>> ReportMismatchAsync(Guid actor, Guid installmentId, ActualPaymentInput observed,
        PaymentCommand meta, CancellationToken ct)
    {
        var requestId = await RequestIdAsync(db, installmentId, ct);
        if (requestId is null) return Denied();
        var recovery = new PaymentRecovery(db, people, evidence, options, clock);
        var ids = observed?.EvidenceIds ?? [];
        var prepared = await recovery.PrepareAsync(actor, requestId.Value, ids, meta, false, ct);
        return await executor.ExecuteAsync(actor, requestId.Value, installmentId, "record", meta,
            PaymentCommandExecutor.Hash(new { Operation = "mismatch.report", Observed = observed }), async token =>
            {
                var installment = await db.Set<AdvanceInstallment>().SingleAsync(x => x.Id == installmentId, token);
                var attempt = installment.State == "Paid"
                    ? await db.Set<AdvancePaymentAttempt>().SingleOrDefaultAsync(x => x.InstallmentId == installmentId && x.State == "Matched", token)
                    : await recovery.PendingAttemptAsync(installment, token);
                if (attempt is null) return Conflict();
                var revision = await recovery.RevisionAsync(attempt, token);
                if (revision is null) return Conflict();
                if (observed is null || !PaymentRecovery.ValidActual(observed, out _) || !recovery.ValidIds(ids)) return Invalid();
                if (!PaymentRecovery.Mismatch(observed, revision)) return Conflict();
                if (!await recovery.CheckEvidenceAsync(prepared, ids, installment, token)) return Invalid();
                var person = await people.ResolveAsync(actor, installment.WorkspaceId, token);
                if (person is null) return Denied();
                var owner = await PaymentRecovery.OwnerAsync(db, attempt, token);
                var snapshot = new RecoverySnapshot(attempt.Id, revision.Id, revision.ContentHash, owner,
                    new("Unknown", observed, ids), prepared.Rows, null);
                db.Add(recovery.Proposal(installment, actor, person.Value, "PaymentMismatch", snapshot, meta));
                recovery.Hold(installment, actor, person.Value, meta.Reason);
                if (installment.State != "Paid") { installment.State = "OutcomePending"; attempt.State = "Mismatch"; }
                // The incident is separate evidence, never a correction to recorded money or recipient.
                return Success(requestId.Value, installmentId);
            }, ct);
    }
}
