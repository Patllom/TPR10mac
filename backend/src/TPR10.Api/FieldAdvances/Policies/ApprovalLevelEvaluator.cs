namespace TPR10.Api.FieldAdvances.Policies;

public static class ApprovalLevelEvaluator
{
    public static FinanceResult<string[]> Evaluate(decimal total, PolicySnapshot snapshot)
    {
        if (total <= 0m || !PolicyValidation.IsMoney(total))
            return new(400, "invalid-total", null);
        if (snapshot is null || snapshot.CompanyVersionId == Guid.Empty || snapshot.ProjectVersionId == Guid.Empty
            || snapshot.ContentHash is not { Length: 64 }
            || snapshot.ContentHash.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            || PolicyValidation.Validate(snapshot.Content).Length != 0)
            return new(400, "invalid-policy", null);

        var levels = new List<string> { "supervisor", "project-manager" };
        foreach (var band in snapshot.Content.Bands)
        {
            levels.Add(band.LevelCode);
            // Validated ascending bands define (previous upper, upper inclusive].
            if (total <= band.UpperInclusive) return new(200, "ok", levels.ToArray());
        }

        return new(400, "unsupported-total", null);
    }
}
