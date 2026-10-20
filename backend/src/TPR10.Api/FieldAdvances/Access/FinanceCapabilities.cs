namespace TPR10.Api.FieldAdvances.Access;

// Deliberately separate from IdentityCatalog: generic roles cannot grant these capabilities.
public static class FinanceCapabilities
{
    public static bool Contains(string capability) => capability is
        "policy.edit.company" or "policy.edit.project" or "policy.review"
        or "grant.propose" or "grant.review" or "grant.revoke"
        or "request.approve" or "finance.check" or "payment.record" or "report.read";
}
