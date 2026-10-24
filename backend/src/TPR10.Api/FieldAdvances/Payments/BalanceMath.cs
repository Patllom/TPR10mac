namespace TPR10.Api.FieldAdvances.Payments;

public static class BalanceMath
{
    public static decimal Available(BalanceSnapshot value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var committed = checked(value.Reserved + value.Paid);
        foreach (var amount in new[] { value.Approved, value.Spendable, value.Reserved, value.Paid })
            if (amount < 0 || amount > 9999999999999999.99m || decimal.Round(amount, 2) != amount)
                throw new ArgumentOutOfRangeException(nameof(value), "Balances must fit the exact stored money representation.");
        if (value.Spendable > value.Approved || committed > value.Spendable)
            throw new InvalidOperationException("The payment balance invariant is violated.");
        return checked(value.Spendable - committed);
    }
}
