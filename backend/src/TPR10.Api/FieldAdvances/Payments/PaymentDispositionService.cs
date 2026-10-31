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
                if (request.State == "Approved" && outcome == "TerminateRemaining")
                    return await ProposeTerminationAsync(actor, request, account, revision, meta, token);
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
                var termination = ReadTermination(proposal);
                if (termination is not null)
                    return await ConfirmTerminationAsync(actor, proposal, termination, meta, token);
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

    // A request-scoped hold survives confirmation and finalization. It never releases other holds.
    private const string TerminationHold = "PaymentTermination";

    private async Task<FinanceResult<PaymentView>> ProposeTerminationAsync(Guid actor, AdvanceRequest request,
        AdvancePaymentAccount account, AdvanceRequestRevision revision, PaymentCommand meta, CancellationToken ct)
    {
        if (!Approved(request, account, revision) || account.Spendable <= account.Paid
            || !await FullyApprovedAsync(revision.Id, ct)) return Conflict();
        var person = await people.ResolveAsync(actor, request.WorkspaceId, ct);
        if (person is null) return Denied();
        var hold = await db.Set<AdvancePaymentHold>().SingleOrDefaultAsync(x => x.RequestId == request.Id
            && x.InstallmentId == null && x.Code == TerminationHold && x.ReleasedAtUtc == null, ct);
        if (hold is null)
        {
            hold = new AdvancePaymentHold
            {
                Id = Guid.NewGuid(),
                WorkspaceId = request.WorkspaceId,
                ProjectId = request.ProjectId,
                FamilyId = request.FamilyId,
                RequestId = request.Id,
                Code = TerminationHold,
                Reason = meta.Reason,
                CreatedBy = actor,
                CreatedByPersonId = person.Value,
                CreatedAtUtc = clock.GetUtcNow()
            };
            db.Add(hold);
        }
        var snapshot = new TerminationSnapshot(true, revision.Id, hold.Id, account.Approved, revision.ApprovalBasisAmount);
        var proposal = new AdvancePaymentProposal
        {
            Id = Guid.NewGuid(),
            WorkspaceId = request.WorkspaceId,
            ProjectId = request.ProjectId,
            FamilyId = request.FamilyId,
            RequestId = request.Id,
            CommandId = meta.CommandId,
            Kind = "PaymentDisposition",
            FamilyVersion = checked(meta.FamilyVersion + 1),
            RequestVersion = checked(meta.RequestVersion + 1),
            BeforeJson = JsonSerializer.Serialize(snapshot),
            AfterJson = JsonSerializer.Serialize(new DispositionOutcome("TerminateRemaining")),
            ContentHash = "",
            ProposedBy = actor,
            ProposedByPersonId = person.Value,
            Reason = meta.Reason,
            CreatedAtUtc = clock.GetUtcNow()
        };
        proposal.ContentHash = TerminationHash(proposal, snapshot); db.Add(proposal);
        return CeilingAmendmentService.Success(request.Id);
    }

    private async Task<FinanceResult<PaymentView>> ConfirmTerminationAsync(Guid actor, AdvancePaymentProposal proposal,
        TerminationSnapshot snapshot, PaymentCommand meta, CancellationToken ct)
    {
        if (proposal.State != "Pending" || proposal.FamilyVersion != meta.FamilyVersion || proposal.RequestVersion != meta.RequestVersion
            || !await CurrentTerminationAsync(proposal, snapshot, ct)) return Conflict();
        var person = await people.ResolveAsync(actor, proposal.WorkspaceId, ct);
        if (person is null || person == proposal.ProposedByPersonId || !await ProposerCurrentAsync(proposal, ct)) return Denied();
        PaymentCommandExecutor.RegisterFinalAuthorization(db, proposal.RequestId, null, meta.CommandId,
            token => ProposerCurrentAsync(proposal, token));
        proposal.State = "Approved"; proposal.ReviewedBy = actor; proposal.ReviewedByPersonId = person; proposal.ReviewedAtUtc = clock.GetUtcNow();
        // Confirmation authorizes the intent, not a stale balance. Reconciliation/cancellation keep their own two-person commands.
        return CeilingAmendmentService.Success(proposal.RequestId);
    }

    public async Task<FinanceResult<PaymentView>> FinalizeAsync(Guid actor, Guid proposalId, PaymentCommand meta, CancellationToken ct)
    {
        var found = await db.Set<AdvancePaymentProposal>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == proposalId && x.Kind == "PaymentDisposition", ct);
        if (found is null || found.InstallmentId is not null) return Denied();
        // Reuse the final-role authorization boundary, with a distinct payload for command-id collision safety.
        return await executor.ExecuteAsync(actor, found.RequestId, null, "terminate-confirm", meta,
            PaymentCommandExecutor.Hash(new { Operation = "disposition.finalize", ProposalId = proposalId }), async token =>
            {
                var proposal = await db.Set<AdvancePaymentProposal>().SingleAsync(x => x.Id == proposalId, token);
                var snapshot = ReadTermination(proposal);
                if (snapshot is null || proposal.State != "Approved" || proposal.ReviewedAtUtc is null
                    || !await CurrentTerminationAsync(proposal, snapshot, token)) return Conflict();
                var person = await people.ResolveAsync(actor, proposal.WorkspaceId, token);
                if (proposal.ReviewedBy != actor || proposal.ReviewedByPersonId != person || person == proposal.ProposedByPersonId
                    || !await ProposerCurrentAsync(proposal, token)) return Denied();
                var account = await db.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == proposal.RequestId, token);
                if (account.Reserved != 0
                    || await db.Set<AdvanceInstallment>().AnyAsync(x => x.RequestId == proposal.RequestId && x.State != "Paid" && x.State != "Cancelled", token)
                    || await db.Set<AdvancePaymentAttempt>().AnyAsync(x => x.RequestId == proposal.RequestId && x.State != "Matched" && x.State != "Unpaid", token)) return Conflict();
                PaymentCommandExecutor.RegisterFinalAuthorization(db, proposal.RequestId, null, meta.CommandId,
                    finalToken => ProposerCurrentAsync(proposal, finalToken));
                account.Spendable = account.Paid; account.Version = checked(account.Version + 1);
                var hold = await db.Set<AdvancePaymentHold>().SingleAsync(x => x.Id == snapshot.HoldId, token);
                hold.Code = "PaymentTerminated";
                return CeilingAmendmentService.Success(proposal.RequestId);
            }, ct);
    }

    private async Task<bool> CurrentTerminationAsync(AdvancePaymentProposal proposal, TerminationSnapshot snapshot, CancellationToken ct)
    {
        var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == proposal.RequestId, ct);
        var account = await db.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == proposal.RequestId, ct);
        var revision = await db.Set<AdvanceRequestRevision>().SingleAsync(x => x.RequestId == proposal.RequestId && x.IsCurrent, ct);
        return Approved(request, account, revision) && revision.Id == snapshot.ApprovedRevision
            && account.Approved == snapshot.Approved && revision.ApprovalBasisAmount == snapshot.Basis
            && await FullyApprovedAsync(revision.Id, ct)
            && await db.Set<AdvancePaymentHold>().AnyAsync(x => x.Id == snapshot.HoldId && x.FamilyId == proposal.FamilyId
                && x.RequestId == proposal.RequestId && x.InstallmentId == null && x.Code == TerminationHold && x.ReleasedAtUtc == null, ct)
            && !await db.Set<AdvancePaymentProposal>().AnyAsync(x => x.RequestId == proposal.RequestId && x.Kind == "PaymentDisposition"
                && x.RequestVersion > proposal.RequestVersion, ct);
    }

    private static bool Approved(AdvanceRequest request, AdvancePaymentAccount account, AdvanceRequestRevision revision)
        => request.State == "Approved" && account.ApprovedRevisionId == revision.Id && revision.SubmittedAtUtc is not null
            && revision.ConfirmedByPersonId == request.OwnerPersonId && account.Approved == revision.Total
            && revision.ApprovalBasisAmount >= account.Approved;

    private async Task<bool> FullyApprovedAsync(Guid revision, CancellationToken ct)
        => await db.Set<AdvanceApprovalStep>().AnyAsync(x => x.RevisionId == revision, ct)
            && !await db.Set<AdvanceApprovalStep>().AnyAsync(x => x.RevisionId == revision && x.Outcome != "Approved", ct);

    private static TerminationSnapshot? ReadTermination(AdvancePaymentProposal proposal)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize<TerminationSnapshot>(proposal.BeforeJson);
            return snapshot is { Termination: true } && proposal.ContentHash == TerminationHash(proposal, snapshot)
                && JsonSerializer.Deserialize<DispositionOutcome>(proposal.AfterJson)?.Outcome == "TerminateRemaining" ? snapshot : null;
        }
        catch (JsonException) { return null; }
    }

    private static string TerminationHash(AdvancePaymentProposal p, TerminationSnapshot snapshot) => PaymentCommandExecutor.Hash(new
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
    private sealed record TerminationSnapshot(bool Termination, Guid ApprovedRevision, Guid HoldId, decimal Approved, decimal Basis);

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
