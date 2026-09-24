using TPR10.Api.Scopes;

namespace TPR10.Api.IntegrationTests;

public sealed class ScopeKeyTests
{
    [Theory]
    [InlineData(1, null, null, true)]
    [InlineData(1, 2, null, true)]
    [InlineData(1, 2, 3, true)]
    [InlineData(0, null, null, false)]
    [InlineData(1, 0, null, false)]
    [InlineData(1, 2, 0, false)]
    [InlineData(1, null, 3, false)]
    public void Scope_key_rejects_empty_ids_and_site_without_project(int workspace, int? project, int? site, bool valid)
    {
        static Guid Id(int n) => new(n, 0, 0, new byte[8]);
        var key = new ScopeKey(Id(workspace), project is { } p ? Id(p) : null, site is { } s ? Id(s) : null);
        Assert.Equal(valid, key.IsValid);
    }
}
