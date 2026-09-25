using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using TPR10.Api.Data;
using TPR10.Api.Identity.Accounts;
using TPR10.Api.Identity.Authorization;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Organization;
using TPR10.Api.Identity.Data;
using TPR10.Api.Scopes.Data;
using static TPR10.Api.IntegrationTests.AuthorizationTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class AttendanceIdentityTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Disable_committed_before_waiting_grant_rechecks_actor_under_lock()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        var role = await RoleAsync(d, "approval");
        await using var observer = d.Database.CreateContext();
        var snapshot = await observer.Set<IdentitySession>().AsNoTracking().SingleAsync(x => x.UserId == actor && x.RevokedAtUtc == null);
        var gate = new LockGate();
        await using var db = new Tpr10DbContext(new DbContextOptionsBuilder<Tpr10DbContext>().UseNpgsql(d.Database.ConnectionString).AddInterceptors(gate).Options);
        var current = new RequestSession { Entity = snapshot };
        var permission = new PermissionContext();
        var policy = new EffectiveRolePolicy(db);
        var service = new RoleAdministration(db, current, new PermissionMutationGuard(db, current, permission, d.Clock), permission,
            new SessionService(db, d.Clock, current, policy), AccountProvisioningTests.Audit(d, db), d.Clock, policy);
        var pending = service.GrantsAsync(role, new([Permission(15)]), default);
        try
        {
            await gate.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var tx = await observer.Database.BeginTransactionAsync();
            await observer.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
            await observer.Set<IdentityUser>().Where(x => x.Id == actor).ExecuteUpdateAsync(x => x.SetProperty(u => u.IsActive, false));
            await tx.CommitAsync();
        }
        finally { gate.Resume.TrySetResult(); }
        Assert.Equal(403, AccountProvisioningTests.Status(await pending));
        Assert.Empty(await observer.Set<RolePermission>().Where(x => x.RoleId == role).ToArrayAsync());
    }

    private sealed class LockGate : DbCommandInterceptor
    {
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("pg_advisory_xact_lock(7241002)", StringComparison.Ordinal))
            {
                Ready.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Held_approval_role_cannot_receive_attendance_self_grant(bool scoped)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        var role = await RoleAsync(d, "approval");
        await using var db = d.Database.CreateContext();
        if (scoped)
        {
            var f = await ScopeFixture.CreateAsync(d);
            db.Add(new ScopeAssignment { Id = Guid.NewGuid(), UserId = actor, RoleId = role, WorkspaceId = f.Workspace.WorkspaceId, CreatedBy = actor, CreatedAtUtc = d.Clock.GetUtcNow(), Reason = "ทดสอบ" });
        }
        else db.Add(new UserRole { UserId = actor, RoleId = role, CreatedAtUtc = d.Clock.GetUtcNow() });
        await db.SaveChangesAsync();
        var before = await db.Set<IdentityUser>().AsNoTracking().SingleAsync(x => x.Id == actor);
        using var response = await SendAsync(d, HttpMethod.Put, $"/api/v1/roles/{role}/permissions", new { permissionIds = new[] { Permission(15) } });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await db.Set<RolePermission>().Where(x => x.RoleId == role).ToArrayAsync());
        Assert.Equal(before.SecurityVersion, (await db.Set<IdentityUser>().AsNoTracking().SingleAsync(x => x.Id == actor)).SecurityVersion);
        Assert.True(await db.AuditEvents.AnyAsync(x => x.ActorId == actor && x.Outcome == "denied"));
        Assert.Equal(HttpStatusCode.OK, (await d.Client.GetAsync("/api/v1/auth/session")).StatusCode);
    }

    [Theory]
    [InlineData("roles")]
    [InlineData("account")]
    public async Task Self_assignment_cannot_introduce_attendance_authority(string route)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        var role = await RoleAsync(d, "approval", 15);
        await using var db = d.Database.CreateContext();
        var prior = await db.Set<UserRole>().Where(x => x.UserId == actor).Select(x => x.RoleId).ToArrayAsync();
        using var response = await SendAsync(d, route == "roles" ? HttpMethod.Put : HttpMethod.Patch,
            $"/api/v1/users/{actor}" + (route == "roles" ? "/roles" : ""), new { roleIds = prior.Append(role).ToArray() });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(await db.Set<UserRole>().AnyAsync(x => x.UserId == actor && x.RoleId == role));
        Assert.True(await db.AuditEvents.AnyAsync(x => x.ActorId == actor && x.Outcome == "denied"));
    }

    [Theory]
    [InlineData("staff", 400)]
    [InlineData("system-administration", 400)]
    [InlineData("approval", 204)]
    [InlineData("accounting", 204)]
    [InlineData("finance-data-access", 204)]
    public async Task Another_operator_can_grant_only_eligible_role_classes(string roleClass, int status)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await RoleAuthorizationTests.AdminAsync(d);
        var role = await RoleAsync(d, roleClass);
        using var response = await SendAsync(d, HttpMethod.Put, $"/api/v1/roles/{role}/permissions", new { permissionIds = new[] { Permission(15) } });
        Assert.Equal(status, (int)response.StatusCode);
        await using var db = d.Database.CreateContext();
        Assert.Equal(status == 204, await db.Set<RolePermission>().AnyAsync(x => x.RoleId == role && x.PermissionId == Permission(15)));
    }

    [Fact]
    public async Task Self_revocation_is_allowed_and_revokes_the_old_session()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        var role = await RoleAsync(d, "approval", 15);
        await using var db = d.Database.CreateContext();
        db.Add(new UserRole { UserId = actor, RoleId = role, CreatedAtUtc = d.Clock.GetUtcNow() });
        await db.SaveChangesAsync();
        using var response = await SendAsync(d, HttpMethod.Put, $"/api/v1/roles/{role}/permissions", new { permissionIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await db.Set<RolePermission>().Where(x => x.RoleId == role).ToArrayAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await d.Client.GetAsync("/api/v1/auth/session")).StatusCode);
    }

    internal static Guid Permission(int suffix) => Guid.Parse($"20000000-0000-0000-0000-{suffix:D12}");
    internal static async Task<Guid> RoleAsync(IdentityTestDriver d, string roleClass, params int[] permissions)
    {
        await using var db = d.Database.CreateContext();
        var id = Guid.NewGuid();
        db.Add(new IdentityRole { Id = id, Name = "attendance-fixture-" + id, RoleClass = roleClass, CreatedAtUtc = d.Clock.GetUtcNow() });
        foreach (var suffix in permissions) db.Add(new RolePermission { RoleId = id, PermissionId = Permission(suffix) });
        await db.SaveChangesAsync();
        return id;
    }
}
