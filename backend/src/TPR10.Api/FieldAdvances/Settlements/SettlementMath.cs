namespace TPR10.Api.FieldAdvances.Settlements;

/// <summary>Exact THB arithmetic. Command boundaries translate argument/overflow errors to validation results.</summary>
public static class SettlementMath
{
    private const decimal MaximumAmount = 9999999999999999.99m;
    private const decimal MaximumQuantity = 9999999999999999.9999m;

    public static decimal Line(decimal? quantity, decimal? rate, decimal? lumpSum)
    {
        if (quantity is null && rate is null && lumpSum is { } lump)
        {
            Validate(lump, 2, true, nameof(lumpSum));
            return Bound(lump);
        }
        if (lumpSum is not null || quantity is null || rate is null)
            throw new ArgumentException("Choose quantity/rate or lump sum.");
        Validate(quantity.Value, 4, true, nameof(quantity));
        Validate(rate.Value, 2, true, nameof(rate));
        if (quantity.Value > MaximumQuantity) throw new OverflowException("Quantity exceeds storage bounds.");
        Bound(rate.Value);
        return Bound(decimal.Round(checked(quantity.Value * rate.Value), 2, MidpointRounding.AwayFromZero));
    }

    public static SettlementTotals Totals(decimal ownerReceived, decimal incidentOwnerReturns,
        decimal approvedExpense, decimal ordinaryRefund, decimal pendingRefund,
        decimal wrongRecipientOutstanding, bool hasOpenCase)
    {
        foreach (var value in new[] { ownerReceived, incidentOwnerReturns, approvedExpense,
            ordinaryRefund, pendingRefund, wrongRecipientOutstanding })
        {
            Validate(value, 2, false, nameof(value));
            Bound(value);
        }
        var confirmed = Bound(checked(incidentOwnerReturns + ordinaryRefund));
        if (confirmed > ownerReceived) throw new ArgumentException("Confirmed returns exceed owner receipts.");
        var outstanding = checked(ownerReceived - confirmed - approvedExpense);
        // Negative balances remain visible; pending refunds never count as received.
        if (outstanding < -MaximumAmount) throw new OverflowException("Outstanding exceeds storage bounds.");
        return new(ownerReceived, approvedExpense, confirmed, pendingRefund, outstanding,
            wrongRecipientOutstanding, hasOpenCase);
    }

    private static void Validate(decimal value, int places, bool positive, string name)
    {
        if (value < 0 || (positive && value == 0) || decimal.Round(value, places) != value)
            throw new ArgumentException("Invalid sign or decimal precision.", name);
    }

    private static decimal Bound(decimal value) => value <= MaximumAmount
        ? value : throw new OverflowException("Amount exceeds storage bounds.");
}
