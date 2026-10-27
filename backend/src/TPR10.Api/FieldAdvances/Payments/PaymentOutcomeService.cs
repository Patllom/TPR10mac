using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Evidence;
using TPR10.Api.FieldAdvances.Requests;
using static TPR10.Api.FieldAdvances.Payments.InstallmentService;

namespace TPR10.Api.FieldAdvances.Payments;

public sealed class PaymentOutcomeService(Tpr10DbContext db, PaymentCommandExecutor executor, ReservationLedger ledger,
    FinancePersonResolver people, FinanceEvidenceService evidence, FinanceEvidenceOptions options, TimeProvider clock)
{
    public async Task<FinanceResult<PaymentView>> ProposeAsync(Guid actor, Guid installmentId, PaymentOutcomeInput input, PaymentCommand meta, CancellationToken ct)
    {
        var requestId = await RequestIdAsync(db, installmentId, ct);
        if (requestId is null) return Denied();
        var recovery = new PaymentRecovery(db, people, evidence, options, clock);
        var ids = PaymentRecovery.EvidenceIds(input);
        var prepared = await recovery.PrepareAsync(actor, requestId.Value, ids, meta, false, ct);
        return await executor.ExecuteAsync(actor, requestId.Value, installmentId, "record", meta,
            PaymentCommandExecutor.Hash(new { Operation = "outcome.propose", Input = input }), async token =>
            {
                var installment = await db.Set<AdvanceInstallment>().SingleAsync(x => x.Id == installmentId, token);
                var attempt = await recovery.PendingAttemptAsync(installment, token);
                if (attempt is null) return Conflict();
                var owner = await PaymentRecovery.OwnerAsync(db, attempt, token);
                if (owner.Actor != actor || await people.ResolveAsync(actor, installment.WorkspaceId, token) != owner.Person) return Conflict();
                var revision = await recovery.RevisionAsync(attempt, token);
                if (revision is null || !await ReservationMatchesAsync(db, installment, revision.Amount, token)) return Conflict();
                if (input is null || input.Outcome is not ("Matched" or "Unpaid" or "Unknown")
                    || !recovery.ValidIds(input.EvidenceIds)
                    || input.Outcome == "Matched" && input.Actual is null
                    || input.Outcome == "Unpaid" && input.Actual is not null
                    || input.Actual is not null && !PaymentRecovery.ValidActual(input.Actual, out _)) return Invalid();
                if (!await recovery.CheckEvidenceAsync(prepared, ids, installment, token)) return Invalid();
                var mismatch = input.Actual is not null && PaymentRecovery.Mismatch(input.Actual, revision);
                var snapshot = new RecoverySnapshot(attempt.Id, revision.Id, revision.ContentHash, owner, input, prepared.Rows,
                    input.Outcome == "Unpaid" ? Guid.NewGuid() : null);
                db.Add(recovery.Proposal(installment, actor, owner.Person, "PaymentOutcome", snapshot, meta));
                installment.State = "OutcomePending";
                if (mismatch) recovery.Hold(installment, actor, owner.Person, meta.Reason);
                return Success(requestId.Value, installmentId);
            }, ct);
    }

    public async Task<FinanceResult<PaymentView>> ConfirmAsync(Guid actor, Guid proposalId, PaymentCommand meta, CancellationToken ct)
    {
        var found = await db.Set<AdvancePaymentProposal>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == proposalId && x.Kind == "PaymentOutcome", ct);
        if (found?.InstallmentId is not { } installmentId) return Denied();
        var recovery = new PaymentRecovery(db, people, evidence, options, clock);
        var snapshot = PaymentRecovery.Snapshot(found);
        var ids = snapshot is null ? [] : PaymentRecovery.EvidenceIds(snapshot.Input);
        var prepared = await recovery.PrepareAsync(actor, found.RequestId, ids, meta, true, ct);
        return await executor.ExecuteAsync(actor, found.RequestId, installmentId, "reconcile", meta,
            PaymentCommandExecutor.Hash(new { Operation = "outcome.confirm", ProposalId = proposalId }), async token =>
            {
                var proposal = await db.Set<AdvancePaymentProposal>().SingleAsync(x => x.Id == proposalId, token);
                var installment = await db.Set<AdvanceInstallment>().SingleAsync(x => x.Id == installmentId, token);
                var current = PaymentRecovery.Snapshot(proposal);
                var attempt = await recovery.PendingAttemptAsync(installment, token);
                if (current is null || attempt is null || attempt.Id != current.AttemptId
                    || !PaymentRecovery.CurrentProposal(proposal, meta) || PaymentCommandExecutor.Hash(snapshot) != PaymentCommandExecutor.Hash(current)) return Conflict();
                var owner = await PaymentRecovery.OwnerAsync(db, attempt, token);
                if (owner != current.Owner || owner.Actor != proposal.ProposedBy || owner.Person != proposal.ProposedByPersonId) return Conflict();
                var person = await people.ResolveAsync(actor, installment.WorkspaceId, token);
                if (person is null || person == owner.Person || !await recovery.CurrentPayerAsync(proposal, token)) return Denied();
                PaymentCommandExecutor.RegisterFinalAuthorization(db, found.RequestId, installmentId, meta.CommandId,
                    finalToken => recovery.CurrentPayerAsync(proposal, finalToken));
                var revision = await recovery.RevisionAsync(attempt, token);
                if (revision is null || revision.Id != current.RevisionId || revision.ContentHash != current.RevisionHash
                    || !await ReservationMatchesAsync(db, installment, revision.Amount, token)) return Conflict();
                if (!await recovery.CheckEvidenceAsync(prepared, ids, installment, token)
                    || PaymentCommandExecutor.Hash(prepared.Rows) != PaymentCommandExecutor.Hash(current.Evidence)) return Conflict();
                var input = current.Input;
                var mismatch = input.Actual is not null && PaymentRecovery.Mismatch(input.Actual, revision);
                if (input.Outcome == "Matched" && !mismatch)
                {
                    var actual = input.Actual!;
                    await ledger.ConsumeAsync(found.RequestId, installmentId, revision.Amount, meta.CommandId, token);
                    db.Add(new AdvancePaymentResult
                    {
                        Id = Guid.NewGuid(),
                        WorkspaceId = installment.WorkspaceId,
                        ProjectId = installment.ProjectId,
                        FamilyId = installment.FamilyId,
                        RequestId = installment.RequestId,
                        InstallmentId = installmentId,
                        AttemptId = attempt.Id,
                        CommandId = meta.CommandId,
                        Amount = revision.Amount,
                        RecipientPersonId = actual.RecipientPersonId,
                        PaidOn = actual.PaidOn,
                        Reference = actual.Reference,
                        EvidenceIdsJson = JsonSerializer.Serialize(ids),
                        RecordedBy = actor,
                        RecordedByPersonId = person.Value,
                        CreatedAtUtc = clock.GetUtcNow()
                    });
                    attempt.State = "Matched"; installment.State = "Paid";
                }
                else if (input.Outcome == "Unpaid")
                {
                    if (revision.RevisionNumber == int.MaxValue || current.NewRevisionId is null) return Conflict();
                    revision.IsCurrent = false;
                    await db.SaveChangesAsync(token);
                    db.Add(new AdvanceInstallmentRevision
                    {
                        Id = current.NewRevisionId.Value,
                        WorkspaceId = revision.WorkspaceId,
                        ProjectId = revision.ProjectId,
                        FamilyId = revision.FamilyId,
                        RequestId = revision.RequestId,
                        InstallmentId = installmentId,
                        RevisionNumber = revision.RevisionNumber + 1,
                        IsCurrent = true,
                        Amount = revision.Amount,
                        Channel = revision.Channel,
                        RecipientPersonId = revision.RecipientPersonId,
                        BankCode = revision.BankCode,
                        AccountNumber = revision.AccountNumber,
                        AccountHolder = revision.AccountHolder,
                        ContentHash = revision.ContentHash,
                        Reason = meta.Reason,
                        CreatedBy = actor,
                        CreatedAtUtc = clock.GetUtcNow()
                    });
                    attempt.State = "Unpaid"; installment.State = "PendingReview";
                }
                else
                {
                    attempt.State = mismatch ? "Mismatch" : "Unknown"; installment.State = "OutcomePending";
                    // A mismatching proposal already established the permanent family hold.
                }
                recovery.Approve(proposal, actor, person.Value);
                return Success(found.RequestId, installmentId);
            }, ct);
    }

    // Genuine recovery provenance replaces preparation only for the exact revision produced by confirmation.
    internal static async Task<AdvancePaymentProposal?> UnpaidProvenanceAsync(Tpr10DbContext db, AdvanceInstallmentRevision revision,
        long? version, CancellationToken ct)
    {
        var proposals = await db.Set<AdvancePaymentProposal>().AsNoTracking().Where(x => x.InstallmentId == revision.InstallmentId
            && x.Kind == "PaymentOutcome" && x.State == "Approved" && x.ReviewedAtUtc != null).ToArrayAsync(ct);
        foreach (var proposal in proposals)
        {
            var snapshot = PaymentRecovery.Snapshot(proposal);
            if (snapshot?.Input.Outcome != "Unpaid" || snapshot.NewRevisionId != revision.Id || snapshot.RevisionHash != revision.ContentHash) continue;
            if (await db.Set<AdvancePaymentAttempt>().AnyAsync(x => x.Id == snapshot.AttemptId && x.InstallmentId == revision.InstallmentId
                && x.RevisionId == snapshot.RevisionId && x.State == "Unpaid", ct)
                && await db.Set<AdvancePaymentEvent>().AnyAsync(x => x.InstallmentId == revision.InstallmentId && x.Kind == "reconcile"
                    && x.InstallmentVersion == version && x.ActorId == proposal.ReviewedBy
                    && x.InstallmentVersion == proposal.InstallmentVersion + 1, ct)) return proposal;
        }
        return null;
    }
}

internal sealed record RecoveryOwner(Guid Actor, Guid Person, Guid? TakeoverId);
internal sealed record RecoveryEvidence(Guid Id, Guid WorkspaceId, Guid ProjectId, Guid FamilyId, Guid RequestId, Guid? InstallmentId,
    string Sha256, long Length, string ContentType, string State);
internal sealed record RecoverySnapshot(Guid AttemptId, Guid RevisionId, string RevisionHash, RecoveryOwner Owner,
    PaymentOutcomeInput Input, RecoveryEvidence[] Evidence, Guid? NewRevisionId);
internal sealed record RecoveryPreparation(bool Ready, RecoveryEvidence[] Rows);

// Shared preparation and persisted proposal contract for the three recovery services; never grants access.
internal sealed class PaymentRecovery(Tpr10DbContext db, FinancePersonResolver people, FinanceEvidenceService evidence,
    FinanceEvidenceOptions options, TimeProvider clock)
{
    internal bool ValidIds(Guid[]? ids) => ids is { Length: > 0 } && ids.Length <= options.MaxFiles
        && ids.All(x => x != Guid.Empty) && ids.Distinct().Count() == ids.Length;
    internal static Guid[] EvidenceIds(PaymentOutcomeInput? input) => (input?.EvidenceIds ?? []).Concat(input?.Actual?.EvidenceIds ?? []).Distinct().Order().ToArray();
    internal async Task<RecoveryPreparation> PrepareAsync(Guid actor, Guid request, Guid[] ids, PaymentCommand meta, bool checker, CancellationToken ct)
    {
        if (await db.Set<FinanceCommandReceipt>().AsNoTracking().AnyAsync(x => x.CommandId == meta.CommandId, ct) || !ValidIds(ids)) return new(false, []);
        var rows = (await db.Set<AdvanceEvidence>().AsNoTracking().Where(x => ids.Contains(x.Id)).OrderBy(x => x.Id).ToArrayAsync(ct)).Select(Snapshot).ToArray();
        var ready = checker ? await evidence.ValidateCheckerLinksAsync(actor, request, ids, ct) : await evidence.ValidateLinksAsync(actor, request, ids, ct);
        return new(ready, rows);
    }
    private static RecoveryEvidence Snapshot(AdvanceEvidence row) => new(row.Id, row.WorkspaceId, row.ProjectId, row.FamilyId, row.RequestId,
        row.InstallmentId, row.Sha256, row.Length, row.ContentType, row.State);
    internal async Task<bool> CheckEvidenceAsync(RecoveryPreparation prepared, Guid[] ids, AdvanceInstallment installment, CancellationToken ct)
    {
        if (!prepared.Ready || !ValidIds(ids)) return false;
        var rows = await db.Set<AdvanceEvidence>().FromSqlInterpolated(
            $"SELECT * FROM field_advance_evidence WHERE id = ANY({ids}) ORDER BY id FOR UPDATE").ToArrayAsync(ct);
        return rows.Length == ids.Length && rows.All(x => x.State == "Ready" && x.WorkspaceId == installment.WorkspaceId
            && x.ProjectId == installment.ProjectId && x.FamilyId == installment.FamilyId && x.RequestId == installment.RequestId
            && (x.InstallmentId == null || x.InstallmentId == installment.Id) && x.Length > 0 && x.Length <= options.MaxFileBytes)
            && rows.Sum(x => x.Length) <= options.MaxTotalBytes
            && rows.Select(Snapshot).OrderBy(x => x.Id).SequenceEqual(prepared.Rows.OrderBy(x => x.Id));
    }
    internal async Task<AdvancePaymentAttempt?> PendingAttemptAsync(AdvanceInstallment installment, CancellationToken ct)
    {
        if (installment.State is not ("Paying" or "OutcomePending")) return null;
        return await db.Set<AdvancePaymentAttempt>().SingleOrDefaultAsync(x => x.InstallmentId == installment.Id
            && (x.State == "Open" || x.State == "Unknown" || x.State == "Mismatch"), ct);
    }
    internal async Task<AdvanceInstallmentRevision?> RevisionAsync(AdvancePaymentAttempt attempt, CancellationToken ct)
    {
        var revision = await db.Set<AdvanceInstallmentRevision>().SingleOrDefaultAsync(x => x.Id == attempt.RevisionId && x.IsCurrent
            && x.InstallmentId == attempt.InstallmentId, ct);
        var owner = await db.Set<AdvanceRequest>().Where(x => x.Id == attempt.RequestId).Select(x => x.OwnerPersonId).SingleAsync(ct);
        return revision is not null && revision.ReviewedBy != null && revision.ReviewedByPersonId != null && revision.ReviewedAtUtc != null
            && ValidRevision(revision, owner) ? revision : null;
    }
    internal static async Task<RecoveryOwner> OwnerAsync(Tpr10DbContext db, AdvancePaymentAttempt attempt, CancellationToken ct)
    {
        var proposals = await db.Set<AdvancePaymentProposal>().AsNoTracking().Where(x => x.InstallmentId == attempt.InstallmentId
            && x.Kind == "PaymentTakeover" && x.State == "Approved" && x.ReviewedAtUtc != null)
            .OrderByDescending(x => x.InstallmentVersion).ToArrayAsync(ct);
        foreach (var proposal in proposals)
        {
            var snapshot = Snapshot(proposal);
            // An invalid authoritative record must never silently restore the original payer.
            if (snapshot is null) return new(Guid.Empty, Guid.Empty, proposal.Id);
            if (snapshot.AttemptId == attempt.Id) return new(proposal.ProposedBy, proposal.ProposedByPersonId, proposal.Id);
        }
        return new(attempt.PayerId, attempt.PayerPersonId, null);
    }
    internal async Task<bool> CurrentPayerAsync(AdvancePaymentProposal proposal, CancellationToken ct)
    {
        // Validate the other participant's current identity/grant/history, without impersonating their session.
        // The executor validates the confirming actor's live session, MFA and active project.
        return await people.ResolveAsync(proposal.ProposedBy, proposal.WorkspaceId, ct) == proposal.ProposedByPersonId
            && RequestSeparation.Check(proposal.ProposedByPersonId, "payment.record", await FamilyParticipation.ReadHistoryAsync(db, proposal.FamilyId, ct), []).Allowed
            && await FinanceGrantQueries.Current(db, new(proposal.WorkspaceId, proposal.ProjectId), "payment.record", clock.GetUtcNow())
                .AnyAsync(x => x.RecipientId == proposal.ProposedBy && x.RecipientPersonId == proposal.ProposedByPersonId, ct);
    }
    internal AdvancePaymentProposal Proposal(AdvanceInstallment installment, Guid actor, Guid person, string kind, RecoverySnapshot snapshot, PaymentCommand meta)
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
            ContentHash = "",
            BeforeJson = JsonSerializer.Serialize(new { snapshot.AttemptId, snapshot.RevisionId, snapshot.RevisionHash, snapshot.Owner }),
            AfterJson = JsonSerializer.Serialize(snapshot),
            EvidenceIdsJson = JsonSerializer.Serialize(EvidenceIds(snapshot.Input)),
            ProposedBy = actor,
            ProposedByPersonId = person,
            Reason = meta.Reason,
            CreatedAtUtc = clock.GetUtcNow()
        };
        proposal.ContentHash = ProposalHash(proposal, snapshot);
        return proposal;
    }
    private static string ProposalHash(AdvancePaymentProposal proposal, RecoverySnapshot snapshot)
        => PaymentCommandExecutor.Hash(new
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
            proposal.ProposedBy,
            proposal.ProposedByPersonId,
            proposal.Reason,
            EvidenceIds = JsonSerializer.Deserialize<Guid[]>(proposal.EvidenceIdsJson),
            Snapshot = snapshot
        });
    internal static RecoverySnapshot? Snapshot(AdvancePaymentProposal proposal)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize<RecoverySnapshot>(proposal.AfterJson);
            return snapshot is not null && ProposalHash(proposal, snapshot) == proposal.ContentHash ? snapshot : null;
        }
        catch (JsonException) { return null; }
    }
    internal static bool CurrentProposal(AdvancePaymentProposal proposal, PaymentCommand meta)
        => proposal.State == "Pending" && proposal.FamilyVersion == meta.FamilyVersion && proposal.RequestVersion == meta.RequestVersion
            && proposal.InstallmentVersion == meta.InstallmentVersion;
    internal void Approve(AdvancePaymentProposal proposal, Guid actor, Guid person)
    { proposal.State = "Approved"; proposal.ReviewedBy = actor; proposal.ReviewedByPersonId = person; proposal.ReviewedAtUtc = clock.GetUtcNow(); }
    internal void Hold(AdvanceInstallment installment, Guid actor, Guid person, string reason)
        => db.Add(new AdvancePaymentHold
        {
            Id = Guid.NewGuid(),
            WorkspaceId = installment.WorkspaceId,
            ProjectId = installment.ProjectId,
            FamilyId = installment.FamilyId,
            Code = "PaymentMismatch",
            Reason = reason,
            CreatedBy = actor,
            CreatedByPersonId = person,
            CreatedAtUtc = clock.GetUtcNow()
        });
    internal static bool ValidActual(ActualPaymentInput input, out decimal amount)
    {
        amount = 0;
        if (string.IsNullOrEmpty(input.Amount) || input.RecipientPersonId == Guid.Empty || input.PaidOn == default || input.PaidOn == DateOnly.MaxValue
            || input.EvidenceIds is not { Length: > 0 } || input.EvidenceIds.Any(x => x == Guid.Empty)
            || input.EvidenceIds.Distinct().Count() != input.EvidenceIds.Length) return false;
        var parts = input.Amount.Split('.');
        return parts.Length <= 2 && parts.All(x => x.Length > 0 && x.All(c => c is >= '0' and <= '9'))
            && (parts.Length == 1 || parts[1].Length <= 2)
            && decimal.TryParse(input.Amount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount)
            && amount > 0 && amount <= 9999999999999999.99m;
    }
    internal static bool Mismatch(ActualPaymentInput input, AdvanceInstallmentRevision revision)
        => !ValidActual(input, out var amount) || amount != revision.Amount || input.RecipientPersonId != revision.RecipientPersonId;
}
