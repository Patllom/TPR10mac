namespace TPR10.Api.FieldAdvances.Requests;

public static class RequestSeparation
{
    internal static string? CapabilityForRole(string role) => role switch
    {
        "Approver" => "request.approve",
        "Checker" => "finance.check",
        "Payer" => "payment.record",
        _ => null
    };
    // Retained server history spans every revision; only duplicate approval is revision-local.
    public static FinanceDecision Check(Guid personId, string action, RequestParticipationHistory history, Guid[] approvedInRevision)
    {
        if (personId == Guid.Empty || history is null || history.OwnerPersonIds is not { Length: > 0 }) return Denied();
        Guid[][] sets = [history.OwnerPersonIds, history.DelegatePersonIds, history.ApprovedPersonIds,
            history.CheckerPersonIds, history.PayerPersonIds, approvedInRevision];
        if (sets.Any(ids => ids is null || ids.Contains(Guid.Empty))
            || history.OwnerPersonIds.Contains(personId) || history.DelegatePersonIds.Contains(personId)) return Denied();
        var approved = history.ApprovedPersonIds.Contains(personId);
        var checkedFinance = history.CheckerPersonIds.Contains(personId);
        var paid = history.PayerPersonIds.Contains(personId);
        return (action switch
        {
            "request.approve" => !approvedInRevision.Contains(personId) && !checkedFinance && !paid,
            "finance.check" => !approved && !approvedInRevision.Contains(personId) && !paid,
            "payment.record" => !approved && !approvedInRevision.Contains(personId) && !checkedFinance,
            _ => false
        }) ? new(true, "allowed") : Denied();
    }

    private static FinanceDecision Denied() => new(false, "separation-denied");
}
