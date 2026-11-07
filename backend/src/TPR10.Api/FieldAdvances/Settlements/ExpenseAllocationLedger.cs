using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;

namespace TPR10.Api.FieldAdvances.Settlements;

// No independent transaction: all reads, releases and reservations use the executor's registry lock.
public sealed class ExpenseAllocationLedger(Tpr10DbContext db, SettlementCommandExecutor executor)
{
    internal async Task<FinanceDecision> ReserveAsync(Guid requestId, Guid roundId, EvidenceAllocation[] allocations, CancellationToken ct)
    {
        executor.RequireAllocationContext(requestId, SettlementActions.Confirm);
        var round = await db.Set<SettlementRound>().SingleOrDefaultAsync(x => x.Id == roundId && x.RequestId == requestId, ct);
        if (round is null || round.AcceptedAtUtc is not null || round.State is not ("Draft" or "AwaitingOwner")) return Denied();
        var revision = await db.Set<SettlementRevision>().Where(x => x.RoundId == roundId && x.RequestId == requestId)
            .OrderByDescending(x => x.RevisionNumber).FirstOrDefaultAsync(ct);
        if (revision is null || !executor.IsAllocationEntity(revision.Id) || revision.State is not ("Draft" or "AwaitingOwner") || revision.SubmittedAtUtc is not null || revision.AcceptedAtUtc is not null) return Denied();
        var lines = await db.Set<SettlementLine>().Where(x => x.RevisionId == revision.Id).ToArrayAsync(ct);
        var rows = await db.Set<ExpenseAllocation>().Where(x => x.RevisionId == revision.Id).ToArrayAsync(ct);
        if (lines.Length == 0 || revision.Total < 0 || revision.Total == 0 && round.RoundKind != "Adjustment" || allocations is null) return Denied();
        var planned = new Dictionary<Guid, decimal>();
        var amounts = new Dictionary<Guid, decimal>();
        var acceptedKinds = new Dictionary<Guid, List<string[]>>();
        try
        {
            foreach (var line in lines)
            {
                var evidence = JsonSerializer.Deserialize<SettlementDraftService.LineEvidence>(line.SnapshotJson);
                if (evidence?.Allocations is null || evidence.EvidenceTypes is null) return Denied();
                // Policy kinds are accepted alternatives, not a checklist of required kinds.
                if (evidence.Allocations.Sum(x => x.Amount) != line.Amount) return Denied();
                foreach (var allocation in evidence.Allocations)
                {
                    var row = rows.SingleOrDefault(x => x.LineId == line.Id && x.DocumentId == allocation.DocumentId);
                    if (row is null || row.AcceptedAtUtc is not null || row.State is not ("Draft" or "Reserved")
                        || !Valid(allocation)) return Denied();
                    amounts.Add(row.Id, allocation.Amount);
                    if (!acceptedKinds.TryGetValue(allocation.DocumentId, out var kinds))
                        acceptedKinds[allocation.DocumentId] = kinds = [];
                    kinds.Add(evidence.EvidenceTypes);
                    planned[allocation.DocumentId] = checked(planned.GetValueOrDefault(allocation.DocumentId) + allocation.Amount);
                }
            }
            var supplied = new Dictionary<Guid, decimal>();
            foreach (var item in allocations)
            {
                if (!Valid(item)) return Denied();
                supplied[item.DocumentId] = checked(supplied.GetValueOrDefault(item.DocumentId) + item.Amount);
            }
            if (amounts.Count != rows.Length || supplied.Count != planned.Count
                || supplied.Any(x => planned.GetValueOrDefault(x.Key) != x.Value)) return Denied();
        }
        catch (Exception e) when (e is JsonException or ArgumentException or OverflowException) { return Denied(); }
        var ids = planned.Keys.ToArray();
        var documents = await db.Set<ExpenseDocument>().FromSqlInterpolated(
            $"SELECT * FROM field_advance_expense_documents WHERE request_id={requestId} AND id=ANY({ids}) ORDER BY id FOR UPDATE")
            .AsNoTracking().ToArrayAsync(ct);
        if (documents.Length != ids.Length) return Denied();
        var credits = new Dictionary<Guid, decimal>();
        foreach (var document in documents)
        {
            ExpenseDocumentService.DocumentEvidence? metadata;
            try { metadata = JsonSerializer.Deserialize<ExpenseDocumentService.DocumentEvidence>(document.SnapshotJson); }
            catch (JsonException) { return Denied(); }
            var kind = metadata?.EvidenceType?.Trim();
            if (string.IsNullOrEmpty(kind) || acceptedKinds[document.Id].Any(kinds =>
                !kinds.Any(accepted => string.Equals(accepted?.Trim(), kind, StringComparison.Ordinal)))) return Denied();
            if (await DuplicateDocumentService.IsBlockedLockedAsync(db, requestId, document.Id, ct)
                || !await ReadyEvidence(document, metadata, ct)) return Denied();
            // One row is counted once even when both its allocation and revision are accepted.
            var usage = await UsageAsync(db, requestId, document.Id, roundId, ct);
            var sourceAmount = await SourceAmountAsync(db, revision.Id, document.Id, ct);
            credits[document.Id] = sourceAmount;
            if (Math.Max(0, planned[document.Id] - sourceAmount) > document.Amount - usage.Consumed - usage.Reserved)
                return new(false, "document-budget-exceeded");
        }
        foreach (var row in rows)
        {
            var covered = Math.Min(amounts[row.Id], credits.GetValueOrDefault(row.DocumentId));
            credits[row.DocumentId] = credits.GetValueOrDefault(row.DocumentId) - covered;
            row.Amount = amounts[row.Id] - covered; row.State = "Reserved"; row.Version++;
        }
        return new(true, "document-reserved");
    }

    internal async Task RetainAsync(Guid requestId, Guid roundId, List<ExpenseAllocation> replacements, CancellationToken ct)
    {
        executor.RequireAllocationContext(requestId, SettlementActions.Draft);
        var previous = await (from a in db.Set<ExpenseAllocation>()
                              join r in db.Set<SettlementRevision>() on a.RevisionId equals r.Id
                              where r.RoundId == roundId && r.RequestId == requestId && a.State == "Reserved"
                              select a).ToArrayAsync(ct);
        var retained = previous.GroupBy(x => x.DocumentId).ToDictionary(x => x.Key, x => x.Sum(a => a.Amount));
        var sourceRound = await db.Set<SettlementRound>().Where(x => x.Id == roundId).Select(x => x.SourceRoundId).SingleOrDefaultAsync(ct);
        var sourceRevision = await db.Set<SettlementRevision>().Where(x => x.RoundId == sourceRound && x.AcceptedAtUtc != null).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
        var sourceAmounts = await db.Set<ExpenseAllocation>().Where(x => x.RevisionId == sourceRevision).GroupBy(x => x.DocumentId)
            .Select(x => new { Document = x.Key, Amount = x.Sum(a => a.Amount) }).ToDictionaryAsync(x => x.Document, x => x.Amount, ct);
        // Preserve the prior submitted amounts and snapshots; only release their active ledger state.
        foreach (var row in previous) { row.State = "Released"; row.Version++; }
        foreach (var row in replacements)
        {
            var covered = Math.Min(row.Amount, sourceAmounts.GetValueOrDefault(row.DocumentId));
            sourceAmounts[row.DocumentId] = sourceAmounts.GetValueOrDefault(row.DocumentId) - covered;
            var amount = Math.Min(row.Amount - covered, retained.GetValueOrDefault(row.DocumentId));
            if (amount <= 0) continue;
            retained[row.DocumentId] -= amount; row.Amount = amount; row.State = "Reserved";
        }
    }

    internal static async Task<Dictionary<Guid, decimal>?> PlannedAsync(Tpr10DbContext db, Guid revision, ExpenseAllocation[] rows, CancellationToken ct)
    {
        try
        {
            var lines = await db.Set<SettlementLine>().AsNoTracking().Where(x => x.RevisionId == revision).ToArrayAsync(ct);
            var planned = new Dictionary<Guid, decimal>();
            foreach (var line in lines)
            {
                var snapshot = JsonSerializer.Deserialize<SettlementDraftService.LineEvidence>(line.SnapshotJson);
                if (snapshot?.Allocations is null || snapshot.Allocations.Sum(x => x.Amount) != line.Amount) return null;
                foreach (var allocation in snapshot.Allocations)
                {
                    var row = rows.SingleOrDefault(x => x.LineId == line.Id && x.DocumentId == allocation.DocumentId);
                    if (row is null || !Valid(allocation)) return null;
                    planned.Add(row.Id, allocation.Amount);
                }
            }
            return planned.Count == rows.Length ? planned : null;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or OverflowException) { return null; }
    }

    internal static async Task<bool> ValidReservationAsync(Tpr10DbContext db, Guid revision, ExpenseAllocation[] rows, Dictionary<Guid, decimal> planned, CancellationToken ct)
    {
        if (rows.Any(x => x.State != "Reserved" || x.Amount < 0 || x.Amount > planned[x.Id])) return false;
        foreach (var group in rows.GroupBy(x => x.DocumentId))
            if (group.Sum(x => x.Amount) != Math.Max(0, group.Sum(x => planned[x.Id]) - await SourceAmountAsync(db, revision, group.Key, ct))) return false;
        return true;
    }

    internal static async Task<decimal> SourceAmountAsync(Tpr10DbContext db, Guid revision, Guid document, CancellationToken ct)
    {
        var source = await db.Set<SettlementExpenseAdjustment>().Where(x => x.RevisionId == revision).Select(x => (Guid?)x.SourceRevisionId).SingleOrDefaultAsync(ct);
        return source is null ? 0 : await db.Set<ExpenseAllocation>().Where(x => x.RevisionId == source && x.DocumentId == document).SumAsync(x => x.Amount, ct);
    }

    internal static async Task<(decimal Consumed, decimal Reserved)> UsageAsync(Tpr10DbContext db, Guid request, Guid document, Guid? excludedRound, CancellationToken ct)
    {
        var superseded = await db.Set<SettlementExpenseAdjustment>().Where(x => x.RequestId == request && x.AcceptedAtUtc != null).Select(x => x.SourceRevisionId).ToArrayAsync(ct);
        var rows = await (from a in db.Set<ExpenseAllocation>()
                          join rev in db.Set<SettlementRevision>() on a.RevisionId equals rev.Id
                          where a.RequestId == request && a.DocumentId == document && rev.RoundId != excludedRound
                          select new { Allocation = a, Revision = rev }).AsNoTracking().ToArrayAsync(ct);
        var consumed = rows.Where(x => (x.Allocation.AcceptedAtUtc != null || x.Revision.AcceptedAtUtc != null) && !superseded.Contains(x.Revision.Id)).Sum(x => x.Allocation.Amount);
        var reserved = rows.Where(x => x.Allocation.State == "Reserved" && x.Revision.AcceptedAtUtc == null).Sum(x => x.Allocation.Amount);
        return (consumed, reserved);
    }

    private async Task<bool> ReadyEvidence(ExpenseDocument doc, ExpenseDocumentService.DocumentEvidence? snapshot, CancellationToken ct)
    {
        var ids = snapshot?.EvidenceIds is { Length: > 0 } values ? values : [doc.EvidenceId];
        if (!ids.Contains(doc.EvidenceId) || ids.Distinct().Count() != ids.Length) return false;
        var evidence = await db.Set<AdvanceEvidence>().FromSqlInterpolated(
            $"SELECT * FROM field_advance_evidence WHERE request_id={doc.RequestId} AND id=ANY({ids}) ORDER BY id FOR UPDATE")
            .AsNoTracking().ToArrayAsync(ct);
        return evidence.Length == ids.Length && evidence.All(x => x.State == "Ready" && x.WorkspaceId == doc.WorkspaceId
            && x.ProjectId == doc.ProjectId && x.FamilyId == doc.FamilyId);
    }
    private static bool Valid(EvidenceAllocation? allocation) => allocation is not null && allocation.DocumentId != Guid.Empty
        && allocation.Amount > 0 && allocation.Amount <= 9999999999999999.99m && decimal.Round(allocation.Amount, 2) == allocation.Amount;
    private static FinanceDecision Denied() => new(false, "document-allocation-invalid");
}
