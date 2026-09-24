using Microsoft.EntityFrameworkCore;
using TPR10.Api.Identity.Accounts;
using TPR10.Api.Identity.Data;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class ScopeCatalogTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Fresh_catalog_grants_only_system_permissions_to_administrator()
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await using var db = d.Database.CreateContext();
        await IdentityCatalog.SeedAsync(db, d.Clock.GetUtcNow(), default);
        await db.SaveChangesAsync();
        Assert.Equal(19, await db.Set<IdentityPermission>().CountAsync());
        var granted = await (from rp in db.Set<RolePermission>()
                             join p in db.Set<IdentityPermission>() on rp.PermissionId equals p.Id
                             where rp.RoleId == IdentityCatalog.AdministratorRoleId
                             select p.Capability).OrderBy(x => x).ToArrayAsync();
        Assert.Equal(new[] { "audit:read", "organization:manage", "roles:manage", "roles:read", "scope-assignments:manage", "system:probe", "users:manage", "users:recover-mfa" }, granted);
        Assert.Equal(5, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM permissions WHERE domain='scoped-business'").SingleAsync());
        Assert.Equal(10, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM permissions WHERE domain='system'").SingleAsync());
        Assert.Equal(4, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM permissions WHERE domain='attendance'").SingleAsync());
        await IdentityCatalog.SeedAsync(db, d.Clock.GetUtcNow(), default);
        await db.SaveChangesAsync();
        Assert.Equal(8, await db.Set<RolePermission>().CountAsync());
    }
}
