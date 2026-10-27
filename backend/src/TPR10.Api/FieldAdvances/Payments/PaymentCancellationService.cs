using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Evidence;
using static TPR10.Api.FieldAdvances.Payments.InstallmentService;

namespace TPR10.Api.FieldAdvances.Payments;

public sealed class PaymentCancellationService(Tpr10DbContext db, PaymentCommandExecutor executor, ReservationLedger ledger,
    FinancePersonResolver people, FinanceEvidenceService evidence, FinanceEvidenceOptions options, TimeProvider clock)
{
    public async Task<FinanceResult<PaymentView>> ProposeAsync(Guid actor, Guid installmentId, PaymentCommand meta, CancellationToken ct)
    {
        var request = await RequestIdAsync(db, installmentId, ct);
        if (request is null) return Denied();
        return await executor.ExecuteAsync(actor, request.Value, installmentId, "record", meta,
            PaymentCommandExecutor.Hash(new { Operation = "cancellation.propose" }), async token =>
            {
                var installment = await db.Set<AdvanceInstallment>().SingleAsync(x => x.Id == installmentId, token);
                var revision = await db.Set<AdvanceInstallmentRevision>().SingleOrDefaultAsync(x => x.InstallmentId == installmentId && x.IsCurrent, token);
                var priorState = installment.State;
                // A new family command can stale a pending proposal. Re-proposal retains the original reservation basis.
                if (priorState == "CancelPending")
                {
                    var previous = await db.Set<AdvancePaymentProposal>().Where(x => x.InstallmentId == installmentId && x.Kind == "PaymentCancellation")
                        .OrderByDescending(x => x.InstallmentVersion).FirstOrDefaultAsync(token);
                    var old = previous is null ? null : AmendmentProposal.Read<CancellationSnapshot>(previous);
                    if (old is null || previous!.State != "Pending" || revision is null || old.RevisionId != revision.Id || old.RevisionHash != revision.ContentHash) return Conflict();
                    priorState = old.PriorState;
                }
                if (revision is null || priorState is not ("Draft" or "PendingReview" or "Returned" or "Reviewed")) return Conflict();
                var snapshot = new CancellationSnapshot(revision.Id, revision.ContentHash, priorState, priorState == "Draft" ? 0 : revision.Amount);
                if (!await ValidUnpaidAsync(installment, snapshot, token)) return Conflict();
                var person = await people.ResolveAsync(actor, installment.WorkspaceId, token);
                if (person is null) return Denied();
                db.Add(AmendmentProposal.Create(installment, actor, person.Value, "PaymentCancellation", snapshot,
                    new { State = "Cancelled", Snapshot = snapshot }, [], meta, clock.GetUtcNow()));
                installment.State = "CancelPending";
                return Success(request.Value, installmentId);
            }, ct);
    }

    public async Task<FinanceResult<PaymentView>> ConfirmAsync(Guid actor, Guid proposalId, PaymentCommand meta, CancellationToken ct)
    {
        var found = await db.Set<AdvancePaymentProposal>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == proposalId && x.Kind == "PaymentCancellation", ct);
        if (found?.InstallmentId is not { } id) return Denied();
        var recovery = new PaymentRecovery(db, people, evidence, options, clock);
        return await executor.ExecuteAsync(actor, found.RequestId, id, "reconcile", meta,
            PaymentCommandExecutor.Hash(new { Operation = "cancellation.confirm", ProposalId = proposalId }), async token =>
            {
                var proposal = await db.Set<AdvancePaymentProposal>().SingleAsync(x => x.Id == proposalId, token);
                var snapshot = AmendmentProposal.Read<CancellationSnapshot>(proposal);
                var installment = await db.Set<AdvanceInstallment>().SingleAsync(x => x.Id == id, token);
                if (snapshot is null || !PaymentRecovery.CurrentProposal(proposal, meta) || installment.State != "CancelPending"
                    || !await ValidUnpaidAsync(installment, snapshot, token)) return Conflict();
                var person = await people.ResolveAsync(actor, installment.WorkspaceId, token);
                if (person is null || person == proposal.ProposedByPersonId || !await recovery.CurrentPayerAsync(proposal, token)) return Denied();
                PaymentCommandExecutor.RegisterFinalAuthorization(db, found.RequestId, id, meta.CommandId,
                    finalToken => recovery.CurrentPayerAsync(proposal, finalToken));
                if (snapshot.Reserved > 0) await ledger.ReleaseAsync(found.RequestId, id, snapshot.Reserved, meta.CommandId, token);
                installment.State = "Cancelled";
                recovery.Approve(proposal, actor, person.Value);
                return Success(found.RequestId, id);
            }, ct);
    }

    private async Task<bool> ValidUnpaidAsync(AdvanceInstallment installment, CancellationSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.PriorState is not ("Draft" or "PendingReview" or "Returned" or "Reviewed")) return false;
        var revision = await db.Set<AdvanceInstallmentRevision>().SingleOrDefaultAsync(x => x.Id == snapshot.RevisionId && x.InstallmentId == installment.Id && x.IsCurrent, ct);
        var owner = await db.Set<AdvanceRequest>().Where(x => x.Id == installment.RequestId).Select(x => x.OwnerPersonId).SingleAsync(ct);
        if (revision is null || !ValidRevision(revision, owner) || revision.ContentHash != snapshot.RevisionHash
            || snapshot.Reserved != (snapshot.PriorState == "Draft" ? 0 : revision.Amount)
            || await db.Set<AdvancePaymentResult>().AnyAsync(x => x.InstallmentId == installment.Id, ct)
            || await db.Set<AdvancePaymentAttempt>().AnyAsync(x => x.InstallmentId == installment.Id && x.State != "Unpaid", ct)) return false;
        var entries = await db.Set<AdvanceReservationEntry>().Where(x => x.InstallmentId == installment.Id).ToArrayAsync(ct);
        return entries.All(x => x.Action is "Reserve" or "Release")
            && entries.Sum(x => x.Action == "Reserve" ? x.Amount : -x.Amount) == snapshot.Reserved;
    }

    private sealed record CancellationSnapshot(Guid RevisionId, string RevisionHash, string PriorState, decimal Reserved);
}

// Task8-only proposal envelope: both before and after, scope and actors are bound by its digest.
internal static class AmendmentProposal
{
    internal static AdvancePaymentProposal Create<TBefore, TAfter>(AdvanceInstallment installment, Guid actor, Guid person,
        string kind, TBefore before, TAfter after, Guid[] ids, PaymentCommand meta, DateTimeOffset now)
    {
        var proposal = new AdvancePaymentProposal
        {
            Id = Guid.NewGuid(),
            WorkspaceId = installment.WorkspaceId,
            ProjectId = installment.ProjectId,
            FamilyId = installment.FamilyId,
            RequestId = installment.RequestId,
            InstallmentId = installment.Id,
            CommandId = meta.CommandId,
            Kind = kind,
            FamilyVersion = checked(meta.FamilyVersion + 1),
            RequestVersion = checked(meta.RequestVersion + 1),
            InstallmentVersion = checked(meta.InstallmentVersion!.Value + 1),
            BeforeJson = JsonSerializer.Serialize(before),
            AfterJson = JsonSerializer.Serialize(after),
            EvidenceIdsJson = JsonSerializer.Serialize(ids),
            ProposedBy = actor,
            ProposedByPersonId = person,
            Reason = meta.Reason,
            CreatedAtUtc = now,
            ContentHash = ""
        };
        proposal.ContentHash = Hash(proposal);
        return proposal;
    }
    private static string Hash(AdvancePaymentProposal proposal) => PaymentCommandExecutor.Hash(new
    {
        proposal.Id,
        proposal.WorkspaceId,
        proposal.ProjectId,
        proposal.FamilyId,
        proposal.RequestId,
        proposal.InstallmentId,
        proposal.CommandId,
        proposal.Kind,
        proposal.FamilyVersion,
        proposal.RequestVersion,
        proposal.InstallmentVersion,
        Before = Canonical(proposal.BeforeJson),
        After = Canonical(proposal.AfterJson),
        EvidenceIds = JsonSerializer.Deserialize<Guid[]>(proposal.EvidenceIdsJson),
        proposal.ProposedBy,
        proposal.ProposedByPersonId,
        proposal.Reason
    });
    // jsonb reorders object keys and whitespace on persistence; bind semantic content, not wire formatting.
    private static object? Canonical(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Normalize(document.RootElement);
    }
    private static object? Normalize(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToDictionary(x => x.Name, x => Normalize(x.Value), StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Select(Normalize).ToArray(),
        _ => element.Clone()
    };
    internal static T? Read<T>(AdvancePaymentProposal proposal, bool after = false) where T : class
    {
        try { return proposal.ContentHash == Hash(proposal) ? JsonSerializer.Deserialize<T>(after ? proposal.AfterJson : proposal.BeforeJson) : null; }
        catch (JsonException) { return null; }
    }
}
