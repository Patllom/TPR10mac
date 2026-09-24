namespace TPR10.Api.Scopes;

public static class ScopeCatalog
{
    public const string SystemDomain = "system";
    public const string BusinessDomain = "scoped-business";
    public static IReadOnlyList<(Guid Id, string Capability, string Domain)> Permissions { get; } =
        Array.AsReadOnly<(Guid, string, string)>([
            (Guid.Parse("20000000-0000-0000-0000-000000000007"), "organization:manage", SystemDomain),
            (Guid.Parse("20000000-0000-0000-0000-000000000008"), "scope-assignments:manage", SystemDomain),
            (Guid.Parse("20000000-0000-0000-0000-000000000009"), "scope-probe:read", BusinessDomain),
            (Guid.Parse("20000000-0000-0000-0000-000000000010"), "scope-probe:write", BusinessDomain),
            (Guid.Parse("20000000-0000-0000-0000-000000000011"), "scope-probe:export", BusinessDomain),
            (Guid.Parse("20000000-0000-0000-0000-000000000012"), "scope-probe:restricted-read", BusinessDomain)
        ]);
}
