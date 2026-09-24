using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Identity.Data;
using TPR10.Api.Scopes.Data;
using TPR10.Api.Scopes;
using TPR10.Api.Scopes.Assignments;
using TPR10.Api.Identity.Authorization;
using static TPR10.Api.IntegrationTests.MfaTests;
using static TPR10.Api.IntegrationTests.AuthorizationTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class ScopedIdentityLifecycleTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Operator_recovery_rechecks_system_domain_inside_use_case()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        await using var db = d.Database.CreateContext();
        var actorSession = await db.Set<IdentitySession>().SingleAsync(x => x.UserId == actor && x.RevokedAtUtc == null);
        (await db.Set<IdentityPermission>().SingleAsync(x => x.Capability == "users:recover-mfa")).Domain = "scoped-business";
        await db.SaveChangesAsync();
        var sessions = new Identity.Sessions.SessionService(db, d.Clock, new Identity.Sessions.RequestSession(), new Organization.EffectiveRolePolicy(db));
        var service = new Identity.Mfa.OperatorMfaRecovery(db, sessions, AccountProvisioningTests.Audit(d, db), d.Clock);
        Assert.Equal(403, AccountProvisioningTests.Status(await service.RecoverAsync(actorSession.Id, f.UserId, "lost", "CASE-TEST", default)));
        Assert.Equal(0, (await db.Set<IdentityUser>().SingleAsync(x => x.Id == f.UserId)).SecurityVersion);
    }

    [Fact]
    public async Task Effective_role_policy_is_account_bound_and_affected_users_are_sorted_distinct_union()
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var f = await ScopeFixture.CreateAsync(d);
        var id = await f.GrantAsync(f.Site, "approval");
        var globalUser = await d.SeedUserAsync("global", Password, []);
        var revokedUser = await d.SeedUserAsync("revoked", Password, []);
        await using var db = d.Database.CreateContext();
        var role = (await db.Set<ScopeAssignment>().SingleAsync(x => x.Id == id)).RoleId;
        db.Add(new UserRole { UserId = globalUser, RoleId = role, CreatedAtUtc = d.Clock.GetUtcNow() });
        db.Add(new UserRole { UserId = f.UserId, RoleId = role, CreatedAtUtc = d.Clock.GetUtcNow() });
        db.Add(new ScopeAssignment
        {
            Id = Guid.NewGuid(),
            UserId = revokedUser,
            RoleId = role,
            WorkspaceId = f.Workspace.WorkspaceId,
            CreatedBy = f.UserId,
            CreatedAtUtc = d.Clock.GetUtcNow(),
            Reason = "test",
            RevokedBy = f.UserId,
            RevokedAtUtc = d.Clock.GetUtcNow(),
            RevocationReason = "test"
        });
        await db.SaveChangesAsync();
        var policy = new Organization.EffectiveRolePolicy(db);
        Assert.Equal(new[] { f.UserId, globalUser }.OrderBy(x => x).ToArray(), await policy.AffectedUsersAsync(role, default));
        Assert.True(await policy.RequiresMfaAsync(f.UserId, default));
        (await db.Set<Organization.Data.Workspace>().SingleAsync(x => x.Id == f.Workspace.WorkspaceId)).IsActive = false;
        await db.SaveChangesAsync();
        Assert.True(await policy.RequiresMfaAsync(f.UserId, default)); // global privilege survives inactive scoped ancestor
        (await db.Set<IdentityUser>().SingleAsync(x => x.Id == f.UserId)).IsActive = false;
        await db.SaveChangesAsync();
        Assert.False(await policy.RequiresMfaAsync(f.UserId, default));
    }

    [Fact]
    public async Task Repeated_lifecycle_calls_before_save_revoke_and_audit_only_once()
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var f = await ScopeFixture.CreateAsync(d);
        await f.GrantAsync(f.Site, "staff");
        await using var db = d.Database.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        var service = new AssignmentLifecycle(db, AccountProvisioningTests.Audit(d, db), d.Clock, new PermissionContext());
        Assert.Equal(1, await service.RevokeForUserAsync(f.UserId, f.UserId, "test", default));
        Assert.Equal(0, await service.RevokeForUserAsync(f.UserId, f.UserId, "test", default));
        Assert.Empty(await service.RevokeForScopeAsync(f.Workspace, f.UserId, "test", default));
        await db.SaveChangesAsync();
        Assert.Equal(2, (await db.Set<ScopeAssignment>().SingleAsync()).Version);
        Assert.Single(await db.AuditEvents.Where(x => x.EventType == "scope.assignment.revoked").ToArrayAsync());
    }

    [Fact]
    public async Task Provisioning_and_last_admin_check_reject_management_capability_in_business_domain()
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var admin = await AccountProvisioningTests.BootstrapAsync(d);
        await using var db = d.Database.CreateContext();
        (await db.Set<IdentityPermission>().SingleAsync(x => x.Capability == "users:manage")).Domain = "scoped-business";
        await db.SaveChangesAsync();
        Assert.False(await PermissionMutationGuard.HasManagingAdminAsync(db, default));
        Assert.Equal(403, AccountProvisioningTests.Status(await AccountProvisioningTests.Provisioning(d, db).ListAsync(admin, 1, 25, default)));
    }

    [Theory]
    [InlineData("workspace", 4)]
    [InlineData("project", 3)]
    [InlineData("site", 1)]
    public async Task Scope_lifecycle_cascades_only_below_target_and_leaves_commit_and_sessions_to_caller(string level, int count)
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var f = await ScopeFixture.CreateAsync(d);
        var assignments = new Dictionary<ScopeKey, Guid>();
        foreach (var key in new[] { f.Workspace, f.Project, f.Site, f.SiblingSite, f.OtherSite }) assignments[key] = await f.GrantAsync(key, "staff");
        using var target = await LoginTargetAsync(d);
        await using var db = d.Database.CreateContext();
        var lifecycle = new AssignmentLifecycle(db, AccountProvisioningTests.Audit(d, db), d.Clock, new PermissionContext());
        var scope = level == "workspace" ? f.Workspace : level == "project" ? f.Project : f.Site;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
            Assert.Equal(new[] { f.UserId }, await lifecycle.RevokeForScopeAsync(scope, f.UserId, "scope-disabled", default));
            await db.SaveChangesAsync();
            Assert.Equal(count, await db.Set<ScopeAssignment>().CountAsync(x => x.RevokedAtUtc != null));
            var expectedKeys = level == "workspace" ? new[] { f.Workspace, f.Project, f.Site, f.SiblingSite }
                : level == "project" ? new[] { f.Project, f.Site, f.SiblingSite } : new[] { f.Site };
            Assert.Equal(expectedKeys.Select(key => assignments[key]).OrderBy(id => id).ToArray(),
                (await db.Set<ScopeAssignment>().Where(x => x.RevokedAtUtc != null).Select(x => x.Id).ToArrayAsync()).OrderBy(id => id).ToArray());
            Assert.Equal(count, await db.AuditEvents.CountAsync(x => x.EventType == "scope.assignment.revoked"));
            Assert.Empty(await lifecycle.RevokeForScopeAsync(scope, f.UserId, "scope-disabled", default));
            await db.SaveChangesAsync();
            Assert.All(await db.Set<ScopeAssignment>().Where(x => x.RevokedAtUtc != null).ToArrayAsync(), a => Assert.Equal(2, a.Version));
            Assert.Equal(0, (await db.Set<IdentityUser>().SingleAsync(x => x.Id == f.UserId)).SecurityVersion);
            Assert.Null((await db.Set<IdentitySession>().SingleAsync()).RevokedAtUtc);
            await tx.RollbackAsync();
        }
        db.ChangeTracker.Clear();
        Assert.False(await db.Set<ScopeAssignment>().AnyAsync(x => x.RevokedAtUtc != null));
        Assert.False(await db.AuditEvents.AnyAsync(x => x.EventType == "scope.assignment.revoked"));
        Assert.Equal(HttpStatusCode.OK, (await target.GetAsync("/api/v1/auth/session")).StatusCode);
    }

    [Fact]
    public async Task Lifecycle_requires_transaction_and_rejects_invalid_scope_without_mutations()
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var f = await ScopeFixture.CreateAsync(d);
        await f.GrantAsync(f.Site, "staff");
        await using var db = d.Database.CreateContext();
        var service = new AssignmentLifecycle(db, AccountProvisioningTests.Audit(d, db), d.Clock, new PermissionContext());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RevokeForUserAsync(f.UserId, f.UserId, "test", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RevokeForScopeAsync(f.Site, f.UserId, "test", default));
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        await Assert.ThrowsAsync<ArgumentException>(() => service.RevokeForScopeAsync(new ScopeKey(Guid.Empty), f.UserId, "test", default));
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task Disable_revokes_assignments_and_sessions_and_enable_never_restores_them()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        var ids = new[] { await f.GrantAsync(f.Workspace, "staff"), await f.GrantAsync(f.Site, "staff") };
        using var target = await LoginTargetAsync(d);
        using var disabled = await SendAsync(d, HttpMethod.Patch, $"/api/v1/users/{f.UserId}", new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        await using var db = d.Database.CreateContext();
        var assignments = await db.Set<ScopeAssignment>().AsNoTracking().Where(x => ids.Contains(x.Id)).ToArrayAsync();
        Assert.All(assignments, a =>
        {
            Assert.Equal(d.Clock.GetUtcNow(), a.RevokedAtUtc);
            Assert.Equal(actor, a.RevokedBy);
            Assert.Equal("account-disabled", a.RevocationReason);
            Assert.Equal(2, a.Version);
        });
        Assert.Equal(HttpStatusCode.Unauthorized, (await target.GetAsync("/api/v1/auth/session")).StatusCode);
        using var enabled = await SendAsync(d, HttpMethod.Patch, $"/api/v1/users/{f.UserId}", new { isActive = true });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        Assert.All(await db.Set<ScopeAssignment>().AsNoTracking().ToArrayAsync(), a => Assert.Equal(2, a.Version));
        Assert.False(await db.Set<ScopeAssignment>().AnyAsync(x => x.RevokedAtUtc == null));
        var audits = await db.AuditEvents.Where(x => x.EventType == "scope.assignment.revoked").ToArrayAsync();
        Assert.Equal(2, audits.Length);
        Assert.All(audits, a => { Assert.Equal(actor, a.ActorId); Assert.NotNull(a.ActingRoleId); Assert.Equal(f.Workspace.WorkspaceId, a.WorkspaceId); });
        foreach (var assignment in assignments)
        {
            var audit = Assert.Single(audits, a => a.TargetId == assignment.Id);
            Assert.Equal(assignment.ProjectId, audit.ProjectId);
            Assert.Equal(assignment.SiteId, audit.SiteId);
            Assert.Equal("scope-assignment", audit.TargetType);
            Assert.Equal("success", audit.Outcome);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Role_grant_change_revokes_scoped_user_exactly_once_even_with_global_membership(bool alsoGlobal)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        var assignment = await f.GrantAsync(f.Site, "staff", "scope-probe:read");
        await using var db = d.Database.CreateContext();
        var role = (await db.Set<ScopeAssignment>().SingleAsync(x => x.Id == assignment)).RoleId;
        if (alsoGlobal)
        {
            db.Add(new UserRole { UserId = f.UserId, RoleId = role, CreatedAtUtc = d.Clock.GetUtcNow() });
            await db.SaveChangesAsync();
        }
        using var target = await LoginTargetAsync(d);
        using var response = await SendAsync(d, HttpMethod.Put, $"/api/v1/roles/{role}/permissions", new { permissionIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, (await db.Set<IdentityUser>().SingleAsync(x => x.Id == f.UserId)).SecurityVersion);
        Assert.Equal(HttpStatusCode.Unauthorized, (await target.GetAsync("/api/v1/auth/session")).StatusCode);
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("grants")]
    public async Task Audit_failure_rolls_back_assignment_account_grants_and_live_cookie(string operation)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        var id = await f.GrantAsync(f.Site, "staff", "scope-probe:read");
        using var target = await LoginTargetAsync(d);
        await using var db = d.Database.CreateContext();
        var role = (await db.Set<ScopeAssignment>().AsNoTracking().SingleAsync(x => x.Id == id)).RoleId;
        var token = await IdentityTestDriver.TokenAsync(d.Client);
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION reject_scoped_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'audit fault'; END $$; CREATE TRIGGER reject_scoped_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION reject_scoped_audit();");
        using var request = IdentityTestDriver.Mutation(token, operation == "disable" ? $"/api/v1/users/{f.UserId}" : $"/api/v1/roles/{role}/permissions");
        request.Method = operation == "disable" ? HttpMethod.Patch : HttpMethod.Put;
        request.Content = JsonContent.Create(operation == "disable" ? (object)new { isActive = false } : new { permissionIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await d.Client.SendAsync(request)).StatusCode);
        var user = await db.Set<IdentityUser>().AsNoTracking().SingleAsync(x => x.Id == f.UserId);
        Assert.True(user.IsActive); Assert.Equal(0, user.SecurityVersion);
        var a = await db.Set<ScopeAssignment>().AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Null(a.RevokedAtUtc); Assert.Null(a.RevokedBy); Assert.Null(a.RevocationReason); Assert.Equal(1, a.Version);
        Assert.Null((await db.Set<IdentitySession>().SingleAsync(x => x.UserId == f.UserId)).RevokedAtUtc);
        Assert.Single(await db.Set<RolePermission>().Where(x => x.RoleId == role).ToArrayAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_scoped_audit ON audit_events; DROP FUNCTION reject_scoped_audit();");
        Assert.Equal(HttpStatusCode.OK, (await target.GetAsync("/api/v1/auth/session")).StatusCode);
    }

    internal static async Task<HttpClient> LoginTargetAsync(IdentityTestDriver d)
    {
        var target = d.NewClient();
        using var request = IdentityTestDriver.Mutation(await IdentityTestDriver.TokenAsync(target), "/api/v1/auth/login");
        request.Content = JsonContent.Create(new { username = "scope-user", password = Password });
        using var response = await target.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return target;
    }
}
