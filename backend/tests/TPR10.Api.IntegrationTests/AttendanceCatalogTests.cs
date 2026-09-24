using Microsoft.EntityFrameworkCore;
using TPR10.Api.Identity.Accounts;
using TPR10.Api.Identity.Data;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class AttendanceCatalogTests(PostgresFixture postgres)
{
    [Fact]
    public void Catalog_includes_attendance_without_replacing_existing_capabilities()
    {
        Assert.Equal(19, IdentityCatalog.Capabilities.Distinct().Count());
        Assert.Contains("attendance:record", IdentityCatalog.Capabilities);
        Assert.Contains("scope-probe:restricted-read", IdentityCatalog.Capabilities);
    }

    [Fact]
    public async Task Seeding_is_idempotent_and_does_not_auto_grant_new_system_or_business_permissions()
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await using var db = d.Database.CreateContext();
        for (var i = 0; i < 2; i++)
        {
            await IdentityCatalog.SeedAsync(db, d.Clock.GetUtcNow(), default);
            await db.SaveChangesAsync();
        }
        var permissions = await db.Set<IdentityPermission>().OrderBy(x => x.Id).ToArrayAsync();
        Assert.Equal(19, permissions.Length);
        string[] names = ["users:manage", "audit:read", "system:probe", "roles:manage", "roles:read", "users:recover-mfa",
            "organization:manage", "scope-assignments:manage", "scope-probe:read", "scope-probe:write", "scope-probe:export",
            "scope-probe:restricted-read", "attendance:record", "attendance:team-read", "attendance:hr-read",
            "attendance:approve-supervisor", "attendance:approve-hr", "attendance:directory-manage", "attendance:storage-manage"];
        for (var i = 0; i < names.Length; i++)
        {
            Assert.Equal(Guid.Parse($"20000000-0000-0000-0000-{i + 1:D12}"), permissions[i].Id);
            Assert.Equal(names[i], permissions[i].Capability);
            Assert.Equal(i < 8 || i >= 17 ? "system" : i < 13 ? "scoped-business" : "attendance", permissions[i].Domain);
        }
        var grants = await db.Set<RolePermission>().Where(x => x.RoleId == IdentityCatalog.AdministratorRoleId)
            .OrderBy(x => x.PermissionId).Select(x => x.PermissionId).ToArrayAsync();
        Assert.Equal(permissions.Take(8).Select(x => x.Id), grants);
        Assert.Equal(8, await db.Set<RolePermission>().CountAsync());
    }
}
