namespace TPR10.Api.FieldAdvances.Access;

public static class SeparationRules
{
    // IDs must come from retained server history, never a client-supplied authorization snapshot.
    public static FinanceDecision Check(Guid personId, string action, RequestParticipation participation)
    {
        if (personId == Guid.Empty || participation is null || participation.OwnerPersonId == Guid.Empty
            || participation.DelegatePersonId == Guid.Empty || participation.ApproverPersonIds is null
            || participation.CheckerPersonIds is null || participation.PayerPersonIds is null
            || participation.ApproverPersonIds.Contains(Guid.Empty) || participation.CheckerPersonIds.Contains(Guid.Empty)
            || participation.PayerPersonIds.Contains(Guid.Empty)) return Denied();
        if (participation.OwnerPersonId == personId || participation.DelegatePersonId == personId) return Denied();
        var approved = participation.ApproverPersonIds.Contains(personId);
        var checkedFinance = participation.CheckerPersonIds.Contains(personId);
        var paid = participation.PayerPersonIds.Contains(personId);
        var allowed = action switch
        {
            "request.approve" => !approved && !checkedFinance && !paid,
            "finance.check" => !approved && !paid,
            "payment.record" => !approved && !checkedFinance,
            _ => false
        };
        return allowed ? new(true, "allowed") : Denied();
    }

    private static FinanceDecision Denied() => new(false, "separation-denied");
}
