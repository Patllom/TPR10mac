using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.Identity.Data;
using TPR10.Api.Scopes;

namespace TPR10.Api.Identity.Accounts;

public static class IdentityCatalog
{
    public static readonly Guid AdministratorRoleId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    public static readonly Guid StaffRoleId = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly (Guid Id, string Name, string Class)[] Roles =
    [
        (AdministratorRoleId, "System Administrator", "system-administration"),
        (StaffRoleId, "Staff", "staff"),
        (Guid.Parse("10000000-0000-0000-0000-000000000003"), "Approver", "approval"),
        (Guid.Parse("10000000-0000-0000-0000-000000000004"), "Accounting", "accounting"),
        (Guid.Parse("10000000-0000-0000-0000-000000000005"), "Finance Data Access", "finance-data-access")
    ];
    private static readonly (Guid Id, string Capability)[] Permissions =
    [
        (Guid.Parse("20000000-0000-0000-0000-000000000001"), "users:manage"),
        (Guid.Parse("20000000-0000-0000-0000-000000000002"), "audit:read"),
        (Guid.Parse("20000000-0000-0000-0000-000000000003"), "system:probe"),
        (Guid.Parse("20000000-0000-0000-0000-000000000004"), "roles:manage"),
        (Guid.Parse("20000000-0000-0000-0000-000000000005"), "roles:read"),
        (Guid.Parse("20000000-0000-0000-0000-000000000006"), "users:recover-mfa")
    ];

    public static string[] Capabilities => Permissions.Select(x => x.Capability)
        .Concat(ScopeCatalog.Permissions.Select(x => x.Capability)).ToArray();

    public static async Task SeedAsync(Tpr10DbContext db, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var role in Roles)
            if (!await db.Set<IdentityRole>().AnyAsync(x => x.Id == role.Id, ct))
                db.Add(new IdentityRole { Id = role.Id, Name = role.Name, RoleClass = role.Class, CreatedAtUtc = now });
        foreach (var permission in Permissions.Select(x => (x.Id, x.Capability, Domain: ScopeCatalog.SystemDomain))
                     .Concat(ScopeCatalog.Permissions))
        {
            if (!await db.Set<IdentityPermission>().AnyAsync(x => x.Id == permission.Id, ct))
                db.Add(new IdentityPermission { Id = permission.Id, Capability = permission.Capability, Domain = permission.Domain });
            if (permission.Domain == ScopeCatalog.SystemDomain
                && !await db.Set<RolePermission>().AnyAsync(x => x.RoleId == AdministratorRoleId && x.PermissionId == permission.Id, ct))
                db.Add(new RolePermission { RoleId = AdministratorRoleId, PermissionId = permission.Id });
        }
    }
}
