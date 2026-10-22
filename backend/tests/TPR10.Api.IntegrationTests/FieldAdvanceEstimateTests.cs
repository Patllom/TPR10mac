using System.Text.Json;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Requests;

namespace TPR10.Api.IntegrationTests;

public sealed class FieldAdvanceEstimateTests
{
    private static readonly Guid CategoryId = Guid.NewGuid();
    private static PolicySnapshot Policy(decimal? rate = null, bool active = true) => new(
        Guid.NewGuid(), null, new(7, [new(1, "L1", 9999999999999999.99m)],
        [new(CategoryId, "หมวดทดสอบ", active, "หน่วย", rate, ["ใบเสร็จ"])]), new('a', 64));
    private static EstimateLineInput Line(string? quantity = "1", string? rate = "10", string? lump = null,
        string mode = "QuantityRate") => new(Guid.NewGuid(), CategoryId, "รายการทดสอบ", mode, quantity, rate, lump);

    [Fact]
    public void Rounds_each_line_before_sum()
    {
        var first = Line("1.2345");
        var second = Line("1.2345");
        var result = EstimateCalculator.Calculate([first, second], Policy());
        Assert.Equal(200, result.Status);
        Assert.Equal(24.70m, result.Value!.Total);
        Assert.Equal(new EstimateLine(first.Id, CategoryId, first.Description, "QuantityRate", 1.2345m, 10m, 12.35m), result.Value.Lines[0]);
        Assert.Equal(12.35m, result.Value.Lines[1].Amount);
    }

    [Theory]
    [InlineData("1.00000", "10", null, "QuantityRate")]
    [InlineData("1", "10.000", null, "QuantityRate")]
    [InlineData(null, null, "10.000", "LumpSum")]
    public void Rejects_excess_lexical_precision(string? quantity, string? rate, string? lump, string mode)
        => Reject(Line(quantity, rate, lump, mode));

    [Fact]
    public void Cannot_override_standard_rate()
    {
        Assert.Equal(400, EstimateCalculator.Calculate([Line(rate: "11")], Policy(10m)).Status);
        Assert.Equal(400, EstimateCalculator.Calculate([Line(null, null, "10", "LumpSum")], Policy(10m)).Status);
        Assert.Equal(400, EstimateCalculator.Calculate([Line(rate: "10.000")], Policy(10m)).Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("10.00")]
    public void Standard_rate_comes_from_snapshot(string? supplied)
    {
        var result = EstimateCalculator.Calculate([Line("2", supplied)], Policy(10m));
        Assert.Equal(200, result.Status);
        Assert.Equal(20m, result.Value!.Total);
        Assert.Equal(10m, result.Value.Lines[0].UnitRate);
    }

    [Theory]
    [InlineData("-1", "10", null, "QuantityRate")]
    [InlineData("1", "-10", null, "QuantityRate")]
    [InlineData(null, null, "-10", "LumpSum")]
    [InlineData("79228162514264337593543950336", "1", null, "QuantityRate")]
    [InlineData("79228162514264337593543950335", "10", null, "QuantityRate")]
    [InlineData("1", "10000000000000000", null, "QuantityRate")]
    [InlineData(null, null, "10000000000000000", "LumpSum")]
    [InlineData("2", "9999999999999999.99", null, "QuantityRate")]
    public void Rejects_overflow_and_negative(string? quantity, string? rate, string? lump, string mode)
        => Reject(Line(quantity, rate, lump, mode));

    [Theory]
    [InlineData("")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("+1")]
    [InlineData("1e1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1,000")]
    [InlineData("๑")]
    [InlineData(".1")]
    [InlineData("1.")]
    [InlineData("1.2.3")]
    public void Rejects_non_ascii_decimal_text(string text)
    {
        Reject(Line(quantity: text));
        Reject(Line(rate: text));
        Reject(Line(null, null, text, "LumpSum"));
    }

    [Fact]
    public void Json_numbers_are_not_wire_strings()
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EstimateLineInput>("""{"Quantity":1.23}"""));

    [Theory]
    [InlineData("0.005", "1", "0.01")]
    [InlineData("0.0049", "1", "0")]
    [InlineData("2", "3.25", "6.50")]
    public void Nonstandard_quantity_rate_rounds_half_up(string quantity, string rate, string amount)
    {
        var result = EstimateCalculator.Calculate([Line(quantity, rate), Line(null, null, "1", "LumpSum")], Policy());
        Assert.Equal(200, result.Status);
        Assert.Equal(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), result.Value!.Lines[0].Amount);
    }

    [Fact]
    public void Nonstandard_lump_sum_preserves_maximum_storage_amount()
    {
        var result = EstimateCalculator.Calculate([Line(null, null, "9999999999999999.99", "LumpSum")], Policy());
        Assert.Equal(200, result.Status);
        Assert.Equal(9999999999999999.99m, result.Value!.Total);
        Assert.Null(result.Value.Lines[0].Quantity);
        Assert.Null(result.Value.Lines[0].UnitRate);
    }

    [Fact]
    public void Rejects_sum_exceeding_storage_limit()
        => Assert.Equal(400, EstimateCalculator.Calculate([
            Line(null, null, "9999999999999999.99", "LumpSum"), Line(null, null, "0.01", "LumpSum")], Policy()).Status);

    [Fact]
    public void Accepts_exact_decimal_quantity_without_an_extra_business_cap()
    {
        var result = EstimateCalculator.Calculate([
            Line("79228162514264337593543950335", "0"), Line(null, null, "1", "LumpSum")], Policy());
        Assert.Equal(200, result.Status);
        Assert.Equal(decimal.MaxValue, result.Value!.Lines[0].Quantity);
        Assert.Equal(1m, result.Value.Total);
    }

    [Fact]
    public void Rejects_decimal_parse_rounding_even_with_valid_fractional_precision()
        => Assert.Equal(400, EstimateCalculator.Calculate([
            Line("7922816251426433759354395033.51", "0"), Line(null, null, "1", "LumpSum")], Policy()).Status);

    [Fact]
    public void Rejects_zero_empty_and_missing_operands()
    {
        Reject(Line("0"));
        Reject(Line("0.0001", "0.01"));
        Reject(Line(null));
        Reject(Line(rate: null));
        Reject(Line(null, null, null, "LumpSum"));
        Assert.Equal(400, EstimateCalculator.Calculate([], Policy()).Status);
        Assert.Equal(400, EstimateCalculator.Calculate(null!, Policy()).Status);
        Assert.Equal(400, EstimateCalculator.Calculate([null!], Policy()).Status);
    }

    [Fact]
    public void Rejects_unknown_inactive_categories_and_unknown_mode()
    {
        Assert.Equal(400, EstimateCalculator.Calculate([Line() with { CategoryId = Guid.NewGuid() }], Policy()).Status);
        Assert.Equal(400, EstimateCalculator.Calculate([Line()], Policy(active: false)).Status);
        Reject(Line(mode: "Other"));
    }

    private static void Reject(EstimateLineInput line)
    {
        var result = EstimateCalculator.Calculate([line], Policy());
        Assert.Equal(400, result.Status);
        Assert.Null(result.Value);
    }
}
