using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;

namespace TPR10.Api.FieldAdvances.Settlements;

// All callers retain the executor's family/request lock until the financial decision commits.
public static class SettlementBalanceReader
{
    public static async Task<FinanceResult<SettlementTotals>> ReadLockedAsync(Tpr10DbContext db, Guid requestId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("ต้องอ่านยอดในธุรกรรมที่ล็อกคำขอ");
        var request = await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == requestId, ct);
        var paid = await db.Set<AdvancePaymentResult>().AsNoTracking().Where(x => x.RequestId == requestId).ToArrayAsync(ct);
        var incidents = await db.Set<PaymentIncidentResolution>().AsNoTracking().Where(x => x.RequestId == requestId && x.AcceptedAtUtc != null).ToArrayAsync(ct);
        var recoveries = await db.Set<PaymentIncidentRecovery>().AsNoTracking().Where(x => x.RequestId == requestId && x.AcceptedAtUtc != null).ToArrayAsync(ct);
        if (incidents.GroupBy(x => x.AttemptId).Any(x => x.Count() != 1)
            || recoveries.Any(x => !incidents.Any(i => i.Id == x.ResolutionId))) return Invalid();
        try
        {
            // An accepted incident REPLACES its attempt's original Paid fact, including when
            // discovered before Paid. Never sum both the original fact and its replacement.
            var replaced = incidents.Select(x => x.AttemptId).ToHashSet();
            var owner = paid.Where(x => !replaced.Contains(x.AttemptId) && x.RecipientPersonId == request.OwnerPersonId).Sum(x => x.Amount);
            var wrong = paid.Where(x => !replaced.Contains(x.AttemptId) && x.RecipientPersonId != request.OwnerPersonId).Sum(x => x.Amount);
            decimal ownerReturns = 0;
            foreach (var incident in incidents)
            {
                var returns = recoveries.Where(x => x.ResolutionId == incident.Id).ToArray();
                if (incident.State != "Accepted" || incident.OwnerReceived < 0 || incident.WrongRecipientReceived < 0
                    || incident.WrongRecipientReceived > 0 && (incident.WrongRecipientPersonId is null || incident.WrongRecipientPersonId == request.OwnerPersonId)
                    || returns.Any(x => x.State != "Accepted" || x.Amount <= 0 || x.ReturnKind is not ("Owner" or "WrongRecipient")
                        || x.ReturnKind == "Owner" && x.ReturnedByPersonId != request.OwnerPersonId
                        || x.ReturnKind == "WrongRecipient" && x.ReturnedByPersonId != incident.WrongRecipientPersonId)) return Invalid();
                var fromOwner = returns.Where(x => x.ReturnKind == "Owner").Sum(x => x.Amount);
                var fromWrong = returns.Where(x => x.ReturnKind == "WrongRecipient").Sum(x => x.Amount);
                if (fromOwner > incident.OwnerReceived || fromWrong > incident.WrongRecipientReceived) return Invalid();
                owner = checked(owner + incident.OwnerReceived); ownerReturns = checked(ownerReturns + fromOwner);
                wrong = checked(wrong + incident.WrongRecipientReceived - fromWrong);
            }
            // Expense replacements and refund corrections use validated contiguous net lineages.

            var refunds = await db.Set<Refund>().AsNoTracking().Where(x => x.RequestId == requestId).ToArrayAsync(ct);
            if (refunds.Any(x => x.Amount <= 0 || (x.AcceptedAtUtc != null ? x.State != "Accepted" : x.State is not ("Draft" or "Pending" or "Returned" or "Cancelled")))) return Invalid();
            var effectiveExpense = await ExpenseAdjustmentService.ExpenseTotalAsync(db, requestId, ct);
            if (effectiveExpense is null) return Invalid();
            var expense = effectiveExpense.Value;
            var corrections = await db.Set<RefundCorrection>().AsNoTracking().Where(x => x.RequestId == requestId && x.AcceptedAtUtc != null).ToArrayAsync(ct);
            if (corrections.Any(x => !refunds.Any(r => r.Id == x.RefundId && r.AcceptedAtUtc != null))) return Invalid();
            decimal confirmed = 0;
            foreach (var refund in refunds.Where(x => x.AcceptedAtUtc != null))
            {
                var net = RefundNet(refund, corrections.Where(x => x.RefundId == refund.Id));
                if (net is null) return Invalid();
                confirmed = checked(confirmed + net.Value.Amount);
            }
            // Both owner-return channels reserve the same outstanding liability. Pending facts
            // remain separate rows and never reduce OwnerOutstanding/ConfirmedRefund.
            var pendingRecovery = await db.Set<PaymentIncidentRecovery>().Where(x => x.RequestId == requestId
                && x.AcceptedAtUtc == null && x.State == "Pending" && x.ReturnKind == "Owner").SumAsync(x => x.Amount, ct);
            var pending = checked(refunds.Where(x => x.AcceptedAtUtc == null && x.State is "Draft" or "Pending" or "Returned").Sum(x => x.Amount) + pendingRecovery);
            var open = await db.Set<SettlementExcessCase>().AnyAsync(x => x.RequestId == requestId && x.State != "Resolved", ct)
                || await db.Set<SettlementRound>().AnyAsync(x => x.RequestId == requestId && x.RoundKind == "Adjustment" && x.AcceptedAtUtc == null, ct)
                || await db.Set<RefundCorrection>().AnyAsync(x => x.RequestId == requestId && x.AcceptedAtUtc == null
                    && (x.State == "Pending" || x.State == "Returned" || x.State == "Draft"), ct)
                || wrong > 0 || await db.Set<AdvancePaymentHold>().AnyAsync(x => x.FamilyId == request.FamilyId && x.ReleasedAtUtc == null
                    && (x.Code != "PaymentTerminated" || x.RequestId == null) && (x.RequestId == null || x.RequestId == requestId), ct)
                || await db.Set<SettlementAdjustmentCase>().AnyAsync(x => x.RequestId == requestId && !db.Set<SettlementCaseClosure>().Any(c => c.CaseId == x.Id) && x.State != "Rejected", ct);
            // Ordinary refund validation remains strict. Accepted corrections or incident
            // replacements may reveal company debt from previously confirmed returns.
            var companyPaid = await db.Set<SettlementCasePayment>().Where(x => x.RequestId == requestId && x.AcceptedAtUtc != null).SumAsync(x => x.Amount, ct);
            var totals = SettlementMath.Totals(owner, ownerReturns, expense,
                corrections.Length == 0 && incidents.Length == 0 ? confirmed : 0, pending, wrong, open);
            var netConfirmed = checked(ownerReturns + confirmed);
            var outstanding = checked(owner - netConfirmed - expense + companyPaid);
            if (netConfirmed > 9999999999999999.99m || Math.Abs(outstanding) > 9999999999999999.99m) return Invalid();
            return new(200, "settlement-balance", totals with
            {
                ConfirmedRefund = netConfirmed,
                OwnerOutstanding = outstanding
            });
        }
        catch (Exception e) when (e is ArgumentException or OverflowException) { return Invalid(); }
    }

    internal static (long Version, decimal Amount)? RefundNet(Refund refund, IEnumerable<RefundCorrection> corrections)
    {
        var version = refund.Version;
        var amount = refund.Amount;
        foreach (var correction in corrections.OrderBy(x => x.BaseVersion))
        {
            if (correction.State != "Accepted" || correction.BaseVersion != version || correction.BeforeAmount != amount
                || correction.AfterAmount < 0 || correction.AfterAmount > 9999999999999999.99m
                || correction.AfterAmount != decimal.Round(correction.AfterAmount, 2)
                || correction.Delta != correction.AfterAmount - amount) return null;
            amount = correction.AfterAmount; version = checked(version + 1);
        }
        return (version, amount);
    }

    private static FinanceResult<SettlementTotals> Invalid() => new(409, "settlement-balance-inconsistent", null);
}
