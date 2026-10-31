using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Evidence;
using static TPR10.Api.FieldAdvances.Payments.InstallmentService;

namespace TPR10.Api.FieldAdvances.Payments;

public sealed class PaymentMetadataService(Tpr10DbContext db, PaymentCommandExecutor executor, FinancePersonResolver people,
    FinanceEvidenceService evidence, FinanceEvidenceOptions options, TimeProvider clock)
{
    public async Task<FinanceResult<PaymentView>> ProposeAsync(Guid actor, Guid installmentId, PaymentMetadataInput input,
        PaymentCommand meta, CancellationToken ct)
    {
        var request = await RequestIdAsync(db, installmentId, ct);
        if (request is null) return Denied();
        var recovery = new PaymentRecovery(db, people, evidence, options, clock);
        var ids = input?.EvidenceIds ?? [];
        var prepared = await recovery.PrepareAsync(actor, request.Value, ids, meta, false, ct);
        return await executor.ExecuteAsync(actor, request.Value, installmentId, "record", meta,
            PaymentCommandExecutor.Hash(new { Operation = "metadata.propose", Input = input }), async token =>
            {
                var installment = await db.Set<AdvanceInstallment>().SingleAsync(x => x.Id == installmentId, token);
                var current = await CurrentAsync(installment, token);
                if (current is null) return Conflict();
                if (input is null || input.PaidOn == default || input.PaidOn == DateOnly.MaxValue
                    || !await recovery.CheckEvidenceAsync(prepared, ids, installment, token)) return Invalid();
                var person = await people.ResolveAsync(actor, installment.WorkspaceId, token);
                if (person is null) return Denied();
                var next = new MetadataSnapshot(current.ResultId, current.ResultHash, input, prepared.Rows);
                db.Add(AmendmentProposal.Create(installment, actor, person.Value, "PaymentMetadata", current, next,
                    ids, meta, clock.GetUtcNow()));
                // Wrong amount/person is reported FIRST through ReportMismatchAsync, with explicit observed data.
                // This correction neither infers receipt contents nor changes money, destination, result or holds.
                return Success(request.Value, installmentId);
            }, ct);
    }

    public async Task<FinanceResult<PaymentView>> ConfirmAsync(Guid actor, Guid proposalId, PaymentCommand meta, CancellationToken ct)
    {
        var found = await db.Set<AdvancePaymentProposal>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == proposalId && x.Kind == "PaymentMetadata", ct);
        if (found?.InstallmentId is not { } id) return Denied();
        var snapshot = AmendmentProposal.Read<MetadataSnapshot>(found, true);
        var ids = snapshot?.Input.EvidenceIds ?? [];
        var recovery = new PaymentRecovery(db, people, evidence, options, clock);
        var prepared = await recovery.PrepareAsync(actor, found.RequestId, ids, meta, true, ct);
        return await executor.ExecuteAsync(actor, found.RequestId, id, "reconcile", meta,
            PaymentCommandExecutor.Hash(new { Operation = "metadata.confirm", ProposalId = proposalId }), async token =>
            {
                var proposal = await db.Set<AdvancePaymentProposal>().SingleAsync(x => x.Id == proposalId, token);
                var before = AmendmentProposal.Read<MetadataSnapshot>(proposal);
                var after = AmendmentProposal.Read<MetadataSnapshot>(proposal, true);
                var installment = await db.Set<AdvanceInstallment>().SingleAsync(x => x.Id == id, token);
                var current = await CurrentAsync(installment, token);
                if (before is null || after is null || current is null || !PaymentRecovery.CurrentProposal(proposal, meta)
                    || PaymentCommandExecutor.Hash(current) != PaymentCommandExecutor.Hash(before)
                    || PaymentCommandExecutor.Hash(snapshot) != PaymentCommandExecutor.Hash(after)) return Conflict();
                var person = await people.ResolveAsync(actor, installment.WorkspaceId, token);
                if (person is null || person == proposal.ProposedByPersonId || !await recovery.CurrentPayerAsync(proposal, token)) return Denied();
                PaymentCommandExecutor.RegisterFinalAuthorization(db, found.RequestId, id, meta.CommandId,
                    finalToken => recovery.CurrentPayerAsync(proposal, finalToken));
                if (!await recovery.CheckEvidenceAsync(prepared, ids, installment, token)
                    || PaymentCommandExecutor.Hash(prepared.Rows) != PaymentCommandExecutor.Hash(after.Evidence)) return Conflict();
                recovery.Approve(proposal, actor, person.Value);
                return Success(found.RequestId, id);
            }, ct);
    }

    private async Task<MetadataSnapshot?> CurrentAsync(AdvanceInstallment installment, CancellationToken ct)
    {
        if (installment.State != "Paid") return null;
        var result = await db.Set<AdvancePaymentResult>().SingleOrDefaultAsync(x => x.InstallmentId == installment.Id, ct);
        if (result is null) return null;
        var hash = PaymentCommandExecutor.Hash(result);
        var latest = await db.Set<AdvancePaymentProposal>().Where(x => x.InstallmentId == installment.Id && x.Kind == "PaymentMetadata" && x.State == "Approved")
            .OrderByDescending(x => x.InstallmentVersion).FirstOrDefaultAsync(ct);
        if (latest is not null)
        {
            var snapshot = AmendmentProposal.Read<MetadataSnapshot>(latest, true);
            return latest.ReviewedBy is not null && latest.ReviewedByPersonId is not null && latest.ReviewedAtUtc is not null
                && snapshot?.ResultId == result.Id && snapshot.ResultHash == hash ? snapshot : null;
        }
        // Original payment is immutable; subsequent corrections form a separate before/after chain.
        return new(result.Id, hash, new(result.PaidOn, result.Reference, JsonSerializer.Deserialize<Guid[]>(result.EvidenceIdsJson)!), []);
    }

    private sealed record MetadataSnapshot(Guid ResultId, string ResultHash, PaymentMetadataInput Input, RecoveryEvidence[] Evidence);
}
