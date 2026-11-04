using TPR10.Api.FieldAdvances.Settlements;
namespace TPR10.Api.IntegrationTests;

public sealed class FieldAdvanceSettlementMathTests
{
    [Fact]
    public void Decimal_line_and_owner_balance_are_exact()
    {
        Assert.Equal(1.01m, SettlementMath.Line(0.5m, 2.01m, null));
        Assert.Equal(1500m, SettlementMath.Totals(10000m, 0m, 8000m, 500m, 1000m, 0m, false).OwnerOutstanding);
        Assert.Equal(0.02m, SettlementMath.Line(0.5m, 0.01m, null) + SettlementMath.Line(0.5m, 0.01m, null));
        Assert.Equal(2.10m, SettlementMath.Line(null, null, 2.100m));
        Assert.Equal(1m, SettlementMath.Line(1.00000m, 1.000m, null));
    }

    [Fact]
    public void Pending_and_wrong_recipient_money_do_not_reduce_owner_balance()
    {
        var total = SettlementMath.Totals(8000m, 500m, 6000m, 1000m, 500m, 2000m, true);
        Assert.Equal(500m, total.OwnerOutstanding);
        Assert.Equal(1500m, total.ConfirmedRefund);
        Assert.Equal(500m, total.PendingRefund);
        Assert.Equal(2000m, total.WrongRecipientOutstanding);
        Assert.True(total.HasOpenCase);
        Assert.Equal(-2000m, SettlementMath.Totals(8000m, 0m, 10000m, 0m, 0m, 0m, false).OwnerOutstanding);
        Assert.Equal(0m, SettlementMath.Totals(0m, 0m, 0m, 0m, 0m, 10000m, true).OwnerOutstanding);
    }

    [Fact]
    public void Invalid_modes_signs_and_precision_are_not_silently_rounded()
    {
        foreach (var input in new (decimal?, decimal?, decimal?)[] {
            (null,null,null), (1m,null,null), (null,1m,null), (1m,1m,1m),
            (0m,1m,null), (-1m,1m,null), (1m,0m,null), (1m,-1m,null),
            (null,null,0m), (null,null,-1m), (0.00001m,1m,null),
            (1m,1.001m,null), (null,null,1.001m) })
            Assert.Throws<ArgumentException>(() => SettlementMath.Line(input.Item1, input.Item2, input.Item3));
        Assert.Throws<ArgumentException>(() => SettlementMath.Totals(100m, 60m, 0m, 41m, 0m, 0m, false));
        for (var index = 0; index < 6; index++)
            foreach (var invalid in new[] { -0.01m, 0.001m })
            {
                var values = new decimal[6]; values[index] = invalid;
                Assert.Throws<ArgumentException>(() => SettlementMath.Totals(values[0], values[1], values[2], values[3], values[4], values[5], false));
            }
    }

    [Fact]
    public void Storage_and_arithmetic_overflow_are_explicit()
    {
        Assert.Throws<OverflowException>(() => SettlementMath.Line(decimal.MaxValue, 1m, null));
        Assert.Throws<OverflowException>(() => SettlementMath.Line(9999999999999999m, 9999999999999999m, null));
        Assert.Throws<OverflowException>(() => SettlementMath.Line(null, null, 10000000000000000m));
        Assert.Throws<OverflowException>(() => SettlementMath.Totals(9999999999999999.99m, 9999999999999999.99m, 0m, 1m, 0m, 0m, false));
        Assert.Equal(9999999999999999.99m, SettlementMath.Line(null, null, 9999999999999999.99m));
    }
}
