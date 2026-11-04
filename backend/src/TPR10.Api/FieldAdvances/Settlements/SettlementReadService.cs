using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;

namespace TPR10.Api.FieldAdvances.Settlements;

public sealed record SettlementSummary(Guid RequestId, string State, SettlementTotals Totals, string[] Holds, string[] AllowedActions, SettlementDeclarationHttp? Declaration = null);
public sealed record SettlementFamilySummary(SettlementSummary[] Requests, SettlementTotals Totals, bool Complete, bool Settled);

public sealed class SettlementReadService(Tpr10DbContext db, SettlementAccess access)
{
    public async Task<FinanceResult<SettlementSummary>> ReadAsync(Guid actor, Guid requestId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!(await access.CheckAsync(actor, requestId, SettlementActions.Read, null, ct)).Allowed) return Denied<SettlementSummary>();
        var value = await Summary(actor, requestId, ct);
        if (!(await access.CheckAsync(actor, requestId, SettlementActions.Read, null, ct)).Allowed) return Denied<SettlementSummary>();
        return value;
    }

    public async Task<FinanceResult<SettlementFamilySummary>> ReadFamilyAsync(Guid actor, Guid requestId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!(await access.CheckAsync(actor, requestId, SettlementActions.Read, null, ct)).Allowed) return Denied<SettlementFamilySummary>();
        return await ReadFamilyLockedAsync(actor, requestId, ct);
    }

    internal async Task<FinanceResult<SettlementFamilySummary>> ReadFamilyLockedAsync(Guid actor, Guid requestId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("A family read requires authorization locks.");
        if (!(await access.CheckAsync(actor, requestId, SettlementActions.Read, null, ct)).Allowed) return Denied<SettlementFamilySummary>();
        var family = await db.Set<AdvanceRequest>().Where(x => x.Id == requestId).Select(x => x.FamilyId).SingleAsync(ct);
        var ids = await db.Set<AdvanceRequest>().Where(x => x.FamilyId == family).OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync(ct);
        var rows = new List<SettlementSummary>();
        var complete = true; var settled = true;
        foreach (var id in ids)
        {
            // Never load hidden balances, identifiers, holds or actions into the projection.
            if (!(await access.CheckAsync(actor, id, SettlementActions.Read, null, ct)).Allowed) { complete = false; continue; }
            var result = await Summary(actor, id, ct);
            if (result.Value is not { } row) return new(result.Status, result.Code, null);
            rows.Add(row);
            var zero = row.Totals.OwnerOutstanding == 0 && row.Totals.WrongRecipientOutstanding == 0
                && row.Totals.PendingRefund == 0 && !row.Totals.HasOpenCase;
            var terminal = row.State == "Closed" || row.State is "Rejected" or "Withdrawn"
                && row.Totals.OwnerReceived == 0 && row.Totals.ApprovedExpense == 0 && row.Totals.ConfirmedRefund == 0;
            settled &= terminal && zero && row.Holds.Length == 0 && !await SettlementClosureService.HasUnfinishedAsync(db, id, ct);
            if (row.State != "Closed")
            {
                var account = await db.Set<AdvancePaymentAccount>().AsNoTracking().SingleAsync(x => x.RequestId == id, ct);
                settled &= account.Reserved == 0 && account.Paid == 0 && account.Spendable == 0;
            }
        }
        foreach (var row in rows)
            if (!(await access.CheckAsync(actor, row.RequestId, SettlementActions.Read, null, ct)).Allowed) return Denied<SettlementFamilySummary>();
        var totals = new SettlementTotals(rows.Sum(x => x.Totals.OwnerReceived), rows.Sum(x => x.Totals.ApprovedExpense),
            rows.Sum(x => x.Totals.ConfirmedRefund), rows.Sum(x => x.Totals.PendingRefund), rows.Sum(x => x.Totals.OwnerOutstanding),
            rows.Sum(x => x.Totals.WrongRecipientOutstanding), rows.Any(x => x.Totals.HasOpenCase));
        // A partial view cannot certify the hidden part of a family as settled.
        return new(200, "settlement-family-summary", new(rows.ToArray(), totals, complete, complete && settled));
    }

    internal sealed record FamilySlice(SettlementSummary[] Requests, SettlementTotals PageTotals, bool PageComplete,
        bool PageSettled, Guid? LastScannedId, bool HasMore);

    internal async Task<FinanceResult<FamilySlice>> ReadFamilyPageLockedAsync(Guid actor, Guid requestId, int take, Guid? after, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("A family page requires authorization locks.");
        if (take is < 1 or > 100) return new(400, "invalid-page", null);
        if (!(await access.CheckAsync(actor, requestId, SettlementActions.Read, null, ct)).Allowed) return Denied<FamilySlice>();
        var family = await db.Set<AdvanceRequest>().Where(x => x.Id == requestId).Select(x => x.FamilyId).SingleAsync(ct);
        var query = db.Set<AdvanceRequest>().AsNoTracking().Where(x => x.FamilyId == family);
        if (after is { } last) query = query.Where(x => x.Id.CompareTo(last) > 0);
        // Bound candidate retrieval AND current-rights checks, even when every candidate is hidden.
        // A sparse/empty page can have a continuation. Never scan ahead until enough visible rows are found.
        var ids = await query.OrderBy(x => x.Id).Select(x => x.Id).Take(take + 1).ToArrayAsync(ct);
        var scanned = ids.Take(take).ToArray();
        var rows = new List<SettlementSummary>(); var complete = true; var settled = true;
        foreach (var id in scanned)
        {
            if (!(await access.CheckAsync(actor, id, SettlementActions.Read, null, ct)).Allowed) { complete = false; continue; }
            var result = await Summary(actor, id, ct);
            if (result.Value is not { } row) return new(result.Status, result.Code, null);
            rows.Add(row);
            settled &= await IsSettledAsync(row, ct);
        }
        var totals = new SettlementTotals(rows.Sum(x => x.Totals.OwnerReceived), rows.Sum(x => x.Totals.ApprovedExpense),
            rows.Sum(x => x.Totals.ConfirmedRefund), rows.Sum(x => x.Totals.PendingRefund), rows.Sum(x => x.Totals.OwnerOutstanding),
            rows.Sum(x => x.Totals.WrongRecipientOutstanding), rows.Any(x => x.Totals.HasOpenCase));
        return new(200, "settlement-family-page", new(rows.ToArray(), totals, complete,
            complete && rows.Count > 0 && settled, scanned.Length == 0 ? null : scanned[^1], ids.Length > take));
    }

    private async Task<bool> IsSettledAsync(SettlementSummary row, CancellationToken ct)
    {
        var zero = row.Totals.OwnerOutstanding == 0 && row.Totals.WrongRecipientOutstanding == 0
            && row.Totals.PendingRefund == 0 && !row.Totals.HasOpenCase;
        var terminal = row.State == "Closed" || row.State is "Rejected" or "Withdrawn"
            && row.Totals.OwnerReceived == 0 && row.Totals.ApprovedExpense == 0 && row.Totals.ConfirmedRefund == 0;
        if (!terminal || !zero || row.Holds.Length != 0 || await SettlementClosureService.HasUnfinishedAsync(db, row.RequestId, ct)) return false;
        if (row.State == "Closed") return true;
        var account = await db.Set<AdvancePaymentAccount>().AsNoTracking().SingleAsync(x => x.RequestId == row.RequestId, ct);
        return account.Reserved == 0 && account.Paid == 0 && account.Spendable == 0;
    }

    internal async Task<FinanceResult<SettlementSummary>> Summary(Guid actor, Guid id, CancellationToken ct)
    {
        var balance = await SettlementBalanceReader.ReadLockedAsync(db, id, ct);
        if (balance.Value is null) return new(balance.Status, balance.Code, null);
        var request = await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == id, ct);
        var holds = await db.Set<AdvancePaymentHold>().Where(x => x.FamilyId == request.FamilyId && x.ReleasedAtUtc == null
                    && (x.Code != "PaymentTerminated" || x.RequestId == null)
            && (x.RequestId == null || x.RequestId == id)).Select(x => x.Code).Distinct().OrderBy(x => x).ToArrayAsync(ct);
        if (request.HoldCode is { } hold) holds = holds.Append(hold).Distinct().Order().ToArray();
        var declarationRow = await SettlementCompletionService.CurrentDeclarationAsync(db, id, ct);
        SettlementDeclarationHttp? declarationWire = null;
        if (declarationRow is not null)
        {
            using var snapshot = System.Text.Json.JsonDocument.Parse(declarationRow.SnapshotJson);
            var onTime = snapshot.RootElement.TryGetProperty("CompletionOnTime", out var timing) && timing.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False ? timing.GetBoolean() : (bool?)null;
            declarationWire = new(declarationRow.Id, declarationRow.Version, declarationRow.IsWithdrawal, declarationRow.CreatedAtUtc, declarationRow.WorkCompletionId, onTime);
        }
        var actions = new List<string> { SettlementActions.Read };
        if (request.State == "Approved")
        {
            var end = await SettlementCompletionService.CurrentEndAsync(db, id, ct);
            var declaration = await SettlementCompletionService.CurrentDeclarationAsync(db, id, ct);
            var candidates = new List<string>();
            if (end is null) candidates.Add(SettlementActions.CompletionPropose);
            else candidates.Add(SettlementActions.Declare);
            if (declaration is null || declaration.IsWithdrawal) candidates.Add(SettlementActions.Draft);
            candidates.Add(SettlementActions.Document);
            if (balance.Value.OwnerOutstanding > balance.Value.PendingRefund) candidates.Add(SettlementActions.RefundPropose);
            foreach (var action in candidates)
                if ((await access.CheckAsync(actor, id, action, null, ct)).Allowed) actions.Add(action);
        }
        // These are request-level entry actions. Entity-specific review still requires its
        // entity/version context; the internal worker close operation is never advertised.
        if (request.State is "Approved" or "Closed")
        {
            if (await db.Set<Refund>().AnyAsync(x => x.RequestId == id && x.AcceptedAtUtc != null, ct)
                && (await access.CheckAsync(actor, id, SettlementActions.CorrectionCreate, null, ct)).Allowed)
                actions.Add(SettlementActions.CorrectionCreate);
            if (await db.Set<SettlementRound>().AnyAsync(x => x.RequestId == id && x.AcceptedAtUtc != null, ct)
                && (await access.CheckAsync(actor, id, SettlementActions.ExpensePropose, null, ct)).Allowed)
                actions.Add(SettlementActions.ExpensePropose);
        }
        return new(200, "settlement-summary", new(id, request.State, balance.Value, holds, actions.ToArray(), declarationWire));
    }
    private static FinanceResult<T> Denied<T>() => new(403, "finance-access-denied", default);
}
