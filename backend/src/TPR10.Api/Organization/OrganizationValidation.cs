using TPR10.Api.Scopes;

namespace TPR10.Api.Organization;

internal static class OrganizationValidation
{
    public static string? Code(string? value)
    {
        if (value is null || value.Any(char.IsControl)) return null;
        var code = value.Trim();
        if (code.Length is < 1 or > 64 || code.Any(c => !(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-'))) return null;
        return code.ToUpperInvariant();
    }

    public static bool Text(string? value, int max) => !string.IsNullOrWhiteSpace(value)
        && value.Trim().Length <= max && !value.Any(char.IsControl);

    public static bool Parent(OrganizationKind kind, ScopeKey? parent) => kind switch
    {
        OrganizationKind.Workspace => parent is null,
        OrganizationKind.Department or OrganizationKind.Project => parent is { IsValid: true, ProjectId: null, SiteId: null },
        OrganizationKind.Site => parent is { IsValid: true, ProjectId: not null, SiteId: null },
        _ => false
    };
}
