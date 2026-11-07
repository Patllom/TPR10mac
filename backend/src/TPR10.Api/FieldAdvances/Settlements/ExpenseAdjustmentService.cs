using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;

namespace TPR10.Api.FieldAdvances.Settlements;

public sealed class ExpenseAdjustmentService(SettlementDraftService drafts)
{
    public Task<FinanceResult<SettlementCommandResult>> ProposeAsync(Guid actor, Guid requestId, Guid sourceRoundId,
        RoundInput replacement, SettlementCommand command, CancellationToken ct)
        => drafts.SaveCoreAsync(actor, requestId, null, sourceRoundId, replacement, command, ct);

    internal static async Task<SettlementRevision?> LatestAsync(Tpr10DbContext db, Guid request, Guid round, CancellationToken ct)
    {
        var current = await db.Set<SettlementRevision>().AsNoTracking().SingleOrDefaultAsync(x => x.RequestId == request && x.RoundId == round && x.AcceptedAtUtc != null, ct);
        var seen = new HashSet<Guid>();
        while (current is not null && seen.Add(current.Id))
        {
            var next = await db.Set<SettlementExpenseAdjustment>().AsNoTracking().Where(x => x.RequestId == request && x.SourceRevisionId == current.Id && x.AcceptedAtUtc != null).ToArrayAsync(ct);
            if (next.Length == 0) return current;
            if (next.Length != 1 || next[0].BaseVersion != current.Version || next[0].BeforeAmount != current.Total
                || next[0].AfterAmount != current.Total + next[0].Delta) return null;
            current = await db.Set<SettlementRevision>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == next[0].RevisionId && x.RequestId == request && x.AcceptedAtUtc != null, ct);
            if (current?.Total != next[0].AfterAmount) return null;
        }
        return null;
    }

    internal static async Task<bool> ValidAsync(Tpr10DbContext db, SettlementRevision revision, CancellationToken ct)
    {
        var round = await db.Set<SettlementRound>().AsNoTracking().SingleAsync(x => x.Id == revision.RoundId, ct);
        if (round.RoundKind == "Ordinary") return revision.Total > 0;
        if (round.SourceRoundId is not { } source) return false;
        var latest = await LatestAsync(db, revision.RequestId, source, ct);
        var adjustment = await db.Set<SettlementExpenseAdjustment>().AsNoTracking().SingleOrDefaultAsync(x => x.RevisionId == revision.Id, ct);
        if (latest is null || adjustment is null || adjustment.SourceRevisionId != latest.Id || adjustment.BaseVersion != latest.Version
            || adjustment.BeforeAmount != latest.Total || adjustment.AfterAmount != revision.Total || adjustment.Delta != revision.Total - latest.Total) return false;
        var originals = await db.Set<SettlementLine>().AsNoTracking().Where(x => x.RevisionId == latest.Id).Select(x => x.Id).ToArrayAsync(ct);
        var lines = await db.Set<SettlementLine>().AsNoTracking().Where(x => x.RevisionId == revision.Id).ToArrayAsync(ct);
        return lines.Length == originals.Length && lines.All(x => x.SourceLineId is { } id && originals.Contains(id))
            && lines.Select(x => x.SourceLineId).Distinct().Count() == originals.Length;
    }

    internal static async Task<bool> ClosedRoundAsync(Tpr10DbContext db, Guid request, Guid roundId, CancellationToken ct)
        => await db.Set<SettlementRound>().AnyAsync(x => x.Id == roundId && x.RequestId == request && x.RoundKind == "Adjustment"
            && x.SourceRoundId != null && x.AdjustmentCaseId != null
            && db.Set<SettlementAdjustmentCase>().Any(c => c.Id == x.AdjustmentCaseId && c.RequestId == request && c.RootExpenseRoundId != null
                && !db.Set<SettlementCaseClosure>().Any(cl => cl.CaseId == c.Id)), ct);

    internal static async Task<Guid> OpenCaseAsync(Tpr10DbContext db, SettlementRound round, SettlementRevision source, CancellationToken ct)
    {
        var root = await db.Set<SettlementRound>().AsNoTracking().SingleAsync(x => x.Id == source.RoundId, ct);
        var seen = new HashSet<Guid>();
        while (root.SourceRoundId is { } parent && seen.Add(root.Id)) root = await db.Set<SettlementRound>().AsNoTracking().SingleAsync(x => x.Id == parent, ct);
        var existing = await db.Set<SettlementAdjustmentCase>().SingleOrDefaultAsync(x => x.RootExpenseRoundId == root.Id
            && !db.Set<SettlementCaseClosure>().Any(c => c.CaseId == x.Id), ct);
        if (existing is not null) return existing.Id;
        var closure = await db.Set<ClosureRecord>().SingleAsync(x => x.RequestId == round.RequestId && x.AcceptedAtUtc != null, ct);
        var row = new SettlementAdjustmentCase
        {
            Id = Guid.NewGuid(),
            WorkspaceId = round.WorkspaceId,
            ProjectId = round.ProjectId,
            FamilyId = round.FamilyId,
            RequestId = round.RequestId,
            CommandId = round.CommandId,
            ProposedBy = round.ProposedBy,
            ProposedByPersonId = round.ProposedByPersonId,
            CreatedAtUtc = round.CreatedAtUtc,
            Reason = round.Reason,
            Kind = "ExpenseAdjustment",
            State = "Pending",
            ClosureRecordId = closure.Id,
            RootExpenseRoundId = root.Id,
            BaselineVersion = source.Version,
            BaselineAmount = source.Total
        };
        db.Add(row); return row.Id;
    }

    internal static async Task<decimal?> ExpenseTotalAsync(Tpr10DbContext db, Guid request, CancellationToken ct)
    {
        var revisions = await db.Set<SettlementRevision>().AsNoTracking().Where(x => x.RequestId == request && x.AcceptedAtUtc != null).ToArrayAsync(ct);
        var rounds = await db.Set<SettlementRound>().AsNoTracking().Where(x => x.RequestId == request).ToDictionaryAsync(x => x.Id, ct);
        var adjustments = await db.Set<SettlementExpenseAdjustment>().AsNoTracking().Where(x => x.RequestId == request && x.AcceptedAtUtc != null).ToArrayAsync(ct);
        var visited = new HashSet<Guid>(); var applied = new HashSet<Guid>(); decimal total = 0;
        foreach (var root in revisions.Where(x => rounds[x.RoundId].RoundKind == "Ordinary"))
        {
            var current = root;
            while (true)
            {
                if (!visited.Add(current.Id) || current.State != "Accepted") return null;
                var next = adjustments.Where(x => x.SourceRevisionId == current.Id).ToArray();
                if (next.Length == 0) break;
                if (next.Length != 1) return null;
                var a = next[0]; var target = revisions.SingleOrDefault(x => x.Id == a.RevisionId);
                if (target is null || a.State != "Accepted" || a.BaseVersion != current.Version || a.BeforeAmount != current.Total
                    || a.AfterAmount != target.Total || a.Delta != target.Total - current.Total
                    || rounds[target.RoundId].RoundKind != "Adjustment" || rounds[target.RoundId].SourceRoundId != current.RoundId
                    || a.AdjustmentCaseId != rounds[target.RoundId].AdjustmentCaseId) return null;
                var sourceLines = await db.Set<SettlementLine>().Where(x => x.RevisionId == current.Id).Select(x => x.Id).ToArrayAsync(ct);
                var targetLines = await db.Set<SettlementLine>().AsNoTracking().Where(x => x.RevisionId == target.Id).ToArrayAsync(ct);
                if (targetLines.Length != sourceLines.Length || targetLines.Any(x => x.SourceLineId is null || !sourceLines.Contains(x.SourceLineId.Value))
                    || targetLines.Select(x => x.SourceLineId).Distinct().Count() != sourceLines.Length || targetLines.Sum(x => x.Amount) != target.Total) return null;
                applied.Add(a.Id); current = target;
            }
            total = checked(total + current.Total);
        }
        return visited.Count == revisions.Length && applied.Count == adjustments.Length ? total : null;
    }
}
