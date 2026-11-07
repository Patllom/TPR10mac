using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Evidence;
using TPR10.Api.FieldAdvances.Payments;

namespace TPR10.Api.FieldAdvances.Settlements;

// Incident facts are replacements of an attempt, never additional Paid or spendable capacity.
internal static class IncidentAccounting
{
    internal static bool Money(decimal value) => value >= 0 && value <= 9999999999999999.99m && decimal.Round(value, 2) == value;

    internal static async Task<IncidentSource?> SourceAsync(Tpr10DbContext db, AdvanceRequest request, Guid attemptId,
        FinanceEvidenceOptions options, CancellationToken ct, Guid? releasedHoldId = null)
    {
        var attempt = await db.Set<AdvancePaymentAttempt>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == attemptId
            && x.WorkspaceId == request.WorkspaceId && x.ProjectId == request.ProjectId && x.FamilyId == request.FamilyId && x.RequestId == request.Id, ct);
        if (attempt is null) return null;
        var revision = await db.Set<AdvanceInstallmentRevision>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == attempt.RevisionId
            && x.InstallmentId == attempt.InstallmentId && x.RequestId == request.Id && x.IsCurrent && x.ReviewedAtUtc != null, ct);
        if (revision is null || revision.ReviewedByPersonId is null || revision.ReviewedBy is null) return null;
        var holds = await db.Set<AdvancePaymentHold>().AsNoTracking().Where(x => x.WorkspaceId == request.WorkspaceId
            && x.ProjectId == request.ProjectId && x.FamilyId == request.FamilyId && x.Code == "PaymentMismatch" && (x.ReleasedAtUtc == null || x.Id == releasedHoldId)).ToArrayAsync(ct);
        // A legacy family hold cannot be proven unrelated to this attempt. Never guess a backfill.
        if (holds.Any(x => x.SourceProposalId is null)) return null;
        var ids = holds.Select(x => x.SourceProposalId).ToArray();
        var proposals = await db.Set<AdvancePaymentProposal>().AsNoTracking().Where(x => ids.Contains(x.Id)
            && x.RequestId == request.Id && x.InstallmentId == attempt.InstallmentId).ToArrayAsync(ct);
        var matches = new List<IncidentSource>();
        foreach (var proposal in proposals)
        {
            var source = PaymentRecovery.Snapshot(proposal);
            if (source is null) return null;
            if (source.AttemptId != attemptId) continue;
            if (proposal.WorkspaceId != request.WorkspaceId || proposal.ProjectId != request.ProjectId || proposal.FamilyId != request.FamilyId
                || proposal.Kind is not ("PaymentMismatch" or "PaymentOutcome") || proposal.State is not ("Pending" or "Approved")
                || source.RevisionId != revision.Id || source.RevisionHash != revision.ContentHash
                || source.Input?.Actual is not { } actual || !PaymentRecovery.ValidActual(actual, out _)
                || !PaymentRecovery.Mismatch(actual, revision) || source.Evidence is null) return null;
            var evidence = await RefundService.EvidenceAsync(db, request, PaymentRecovery.EvidenceIds(source.Input), options, ct);
            if (evidence is null || evidence.Length != source.Evidence.Length || source.Evidence.Any(old =>
                old.WorkspaceId != request.WorkspaceId || old.ProjectId != request.ProjectId || old.FamilyId != request.FamilyId
                || old.RequestId != request.Id || old.InstallmentId != null && old.InstallmentId != attempt.InstallmentId
                || old.State != "Ready" || !evidence.Any(e => e.Id == old.Id && e.Sha256 == old.Sha256 && e.Length == old.Length && e.ContentType == old.ContentType))) return null;
            var hold = holds.Single(x => x.SourceProposalId == proposal.Id);
            matches.Add(new(hold.Id, proposal.Id, proposal.ContentHash, attemptId, revision.Id, revision.ContentHash,
                revision.Amount, actual.RecipientPersonId));
        }
        return matches.Count == 1 ? matches[0] : null;
    }

    internal static IncidentSnapshot? Read(PaymentIncidentResolution row)
    {
        try
        {
            var s = JsonSerializer.Deserialize<IncidentSnapshot>(row.SnapshotJson);
            return s is { SchemaVersion: 1, Input: not null, Source: not null, Evidence: not null }
                && s.Input.AttemptId == row.AttemptId && s.Source.AttemptId == row.AttemptId && s.Source.HoldId == row.HoldId
                && s.Input.OwnerReceived == row.OwnerReceived && s.Input.WrongRecipientReceived == row.WrongRecipientReceived
                && s.Input.EvidenceIds is { Length: > 0 } && s.Input.EvidenceIds[0] == row.EvidenceId
                && s.Input.EvidenceIds.SequenceEqual(s.Evidence.Select(x => x.Id))
                && (row.WrongRecipientReceived == 0 ? row.WrongRecipientPersonId is null : row.WrongRecipientPersonId == s.Source.ActualRecipientPersonId) ? s : null;
        }
        catch (JsonException) { return null; }
    }
    internal static IncidentRecoverySnapshot? Read(PaymentIncidentRecovery row)
    {
        try
        {
            var s = JsonSerializer.Deserialize<IncidentRecoverySnapshot>(row.SnapshotJson);
            return s is { SchemaVersion: 1, Evidence: not null, EvidenceIds.Length: > 0 }
                && s.IncidentId == row.ResolutionId && s.Amount == row.Amount && s.Source == row.ReturnKind
                && s.ReturnedByPersonId == row.ReturnedByPersonId && s.EvidenceIds[0] == row.EvidenceId
                && s.EvidenceIds.SequenceEqual(s.Evidence.Select(x => x.Id)) ? s : null;
        }
        catch (JsonException) { return null; }
    }

    internal static async Task<bool> RecoveryFitsAsync(Tpr10DbContext db, PaymentIncidentResolution incident, string source,
        decimal amount, Guid? exclude, CancellationToken ct)
    {
        var related = source == "Owner" ? incident.OwnerReceived : incident.WrongRecipientReceived;
        var claimed = await db.Set<PaymentIncidentRecovery>().Where(x => x.ResolutionId == incident.Id && x.ReturnKind == source
            && x.Id != exclude && (x.AcceptedAtUtc != null || x.State == "Pending")).SumAsync(x => x.Amount, ct);
        if (amount > related - claimed) return false;
        if (source == "Owner")
        {
            var balance = await SettlementBalanceReader.ReadLockedAsync(db, incident.RequestId, ct);
            if (balance.Value is null) return false;
            var pending = balance.Value.PendingRefund - (exclude is null ? 0 : amount);
            if (amount > balance.Value.OwnerOutstanding - pending) return false;
        }
        return true;
    }

    // Only compare the two return channels: ordinary refunds remain governed by their own
    // existing rules. Retain all historical accepted correction evidence as claimed proof.
    internal static async Task<bool> RecoveryProofUsedAsync(Tpr10DbContext db, Guid workspace,
        RefundEvidenceSnapshot[] evidence, Guid? exclude, CancellationToken ct)
    {
        var rows = await db.Set<PaymentIncidentRecovery>().AsNoTracking().Where(x => x.WorkspaceId == workspace && x.Id != exclude
            && (x.AcceptedAtUtc != null || x.State == "Pending")).ToArrayAsync(ct);
        return rows.Any(x => Read(x) is not { } s || Overlap(evidence, s.Evidence));
    }
    internal static async Task<bool> OrdinaryProofUsedAsync(Tpr10DbContext db, Guid workspace, RefundEvidenceSnapshot[] evidence, CancellationToken ct)
    {
        var refunds = await db.Set<Refund>().AsNoTracking().Where(x => x.WorkspaceId == workspace
            && (x.AcceptedAtUtc != null || x.State == "Draft" || x.State == "Pending" || x.State == "Returned")).ToArrayAsync(ct);
        var corrections = await db.Set<RefundCorrection>().AsNoTracking().Where(x => x.WorkspaceId == workspace
            && (x.AcceptedAtUtc != null || x.State == "Pending" || x.State == "Returned")).ToArrayAsync(ct);
        // Legacy rows with no complete proof snapshot cannot establish exclusive ownership.
        return refunds.Any(x => RefundService.ReadSnapshot(x) is not { } s || Overlap(evidence, s.Evidence))
            || corrections.Any(x => RefundCorrectionService.Read(x) is not { } s || Overlap(evidence, s.Evidence));
    }
    private static bool Overlap(RefundEvidenceSnapshot[] a, RefundEvidenceSnapshot[] b)
        => a.Any(x => b.Any(y => x.Id == y.Id || x.Sha256 == y.Sha256));

    internal static void Event(Tpr10DbContext db, AdvanceRequest request, Guid installmentId, Guid actor, Guid person,
        SettlementCommand command, DateTimeOffset at, string kind, object facts)
        => db.Add(new AdvancePaymentEvent
        {
            Id = Guid.NewGuid(),
            WorkspaceId = request.WorkspaceId,
            ProjectId = request.ProjectId,
            FamilyId = request.FamilyId,
            RequestId = request.Id,
            InstallmentId = installmentId,
            CommandId = command.CommandId,
            ActorId = actor,
            ActorPersonId = person,
            FamilyVersion = checked(command.FamilyVersion + 1),
            RequestVersion = checked(command.RequestVersion + 1),
            Kind = kind,
            DataJson = JsonSerializer.Serialize(facts),
            Reason = command.Reason.Trim(),
            CreatedAtUtc = at
        });
}

internal sealed record IncidentSource(Guid HoldId, Guid ProposalId, string ProposalHash, Guid AttemptId, Guid RevisionId,
    string RevisionHash, decimal InstallmentAmount, Guid ActualRecipientPersonId);
internal sealed record IncidentSnapshot(int SchemaVersion, IncidentInput Input, IncidentSource Source, RefundEvidenceSnapshot[] Evidence);
internal sealed record IncidentRecoverySnapshot(int SchemaVersion, Guid IncidentId, decimal Amount, string Source,
    Guid ReturnedByPersonId, Guid[] EvidenceIds, RefundEvidenceSnapshot[] Evidence);
