using System.Globalization;
using TPR10.Api.FieldAdvances.Policies;

namespace TPR10.Api.FieldAdvances.Requests;

public static class EstimateCalculator
{
    // Same numeric(18,2) storage boundary as 7A; not a business approval limit.
    private const decimal MaximumAmount = 9999999999999999.99m;
    // Quantity is stored as numeric(20,4), independently of the resulting amount.
    private const decimal MaximumQuantity = 9999999999999999.9999m;

    public static FinanceResult<Estimate> Calculate(EstimateLineInput[] lines, PolicySnapshot policy)
    {
        if (policy is null || PolicyValidation.Validate(policy.Content).Length != 0)
            return new(400, "invalid-policy", null);
        if (lines is not { Length: > 0 }) return new(400, "invalid-lines", null);

        var calculated = new List<EstimateLine>(lines.Length);
        decimal total = 0m;
        foreach (var line in lines)
        {
            if (line is null) return new(400, "invalid-line", null);
            var category = policy.Content.Categories.FirstOrDefault(c => c.Id == line.CategoryId && c.Active);
            if (category is null) return new(400, "invalid-category", null);

            decimal? quantity = null;
            decimal? rate = null;
            decimal amount;
            if (line.Mode == "QuantityRate")
            {
                if (!TryDecimal(line.Quantity, 4, out var parsedQuantity) || parsedQuantity > MaximumQuantity)
                    return new(400, "invalid-quantity", null);
                quantity = parsedQuantity;
                if (category.StandardRate is { } standardRate)
                {
                    if (line.UnitRate is not null
                        && (!TryDecimal(line.UnitRate, 2, out var suppliedRate) || suppliedRate != standardRate))
                        return new(400, "invalid-standard-rate", null);
                    rate = standardRate;
                }
                else
                {
                    if (!TryDecimal(line.UnitRate, 2, out var parsedRate) || parsedRate > MaximumAmount)
                        return new(400, "invalid-rate", null);
                    rate = parsedRate;
                }

                try
                {
                    amount = decimal.Round(checked(parsedQuantity * rate.Value), 2, MidpointRounding.AwayFromZero);
                }
                catch (OverflowException)
                {
                    return new(400, "amount-overflow", null);
                }
            }
            else if (line.Mode == "LumpSum" && category.StandardRate is null)
            {
                if (!TryDecimal(line.LumpSum, 2, out amount))
                    return new(400, "invalid-lump-sum", null);
            }
            else return new(400, "invalid-mode", null);

            if (amount > MaximumAmount) return new(400, "amount-overflow", null);
            try
            {
                total = checked(total + amount);
            }
            catch (OverflowException)
            {
                return new(400, "total-overflow", null);
            }
            if (total > MaximumAmount) return new(400, "total-overflow", null);
            calculated.Add(new(line.Id, line.CategoryId, line.Description, line.Mode, quantity, rate, amount));
        }

        return total > 0m
            ? new(200, "ok", new(calculated.ToArray(), total))
            : new(400, "invalid-total", null);
    }

    private static bool TryDecimal(string? text, int precision, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrEmpty(text)) return false;
        var dot = -1;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '.' && dot < 0 && index > 0 && index < text.Length - 1)
                dot = index;
            else if (character is < '0' or > '9') return false;
        }
        if (dot >= 0 && text.Length - dot - 1 > precision) return false;

        // TryParse may silently round a significand that exceeds decimal precision.
        // Compare its exact normalized text to preserve every supplied input digit.
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value))
            return false;
        var integer = text.AsSpan(0, dot < 0 ? text.Length : dot).TrimStart('0');
        var fraction = dot < 0 ? ReadOnlySpan<char>.Empty : text.AsSpan(dot + 1).TrimEnd('0');
        var normalized = integer.IsEmpty ? "0" : integer.ToString();
        if (!fraction.IsEmpty) normalized += "." + fraction.ToString();
        return value.ToString("0.############################", CultureInfo.InvariantCulture) == normalized;
    }
}
