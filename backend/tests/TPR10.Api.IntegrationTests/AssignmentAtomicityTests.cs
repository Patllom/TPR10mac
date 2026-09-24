using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Identity.Accounts;
using TPR10.Api.Identity.Data;
using TPR10.Api.Scopes.Data;
using static TPR10.Api.IntegrationTests.AssignmentApiTests;
using Microsoft.Extensions.DependencyInjection;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TPR10.Api.Data;
using TPR10.Api.Identity.Authorization;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Organization;
using TPR10.Api.Scopes.Assignments;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class AssignmentAtomicityTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("grant", 201)]
    [InlineData("replace", 200)]
    public async Task Concurrent_duplicate_or_version_requests_have_one_atomic_winner(string operation, int success)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        var original = operation == "replace" ? await CreateAsync(d, f) : Guid.Empty;
        await using var observer = d.Database.CreateContext();
        var snapshot = await observer.Set<IdentitySession>().AsNoTracking().SingleAsync(x => x.UserId == actor && x.RevokedAtUtc == null);
        var gate = new LockGate();
        async Task<int?> Attempt()
        {
            await using var db = new Tpr10DbContext(new DbContextOptionsBuilder<Tpr10DbContext>().UseNpgsql(d.Database.ConnectionString).AddInterceptors(gate).Options);
            var current = new RequestSession { Entity = snapshot }; var permission = new PermissionContext();
            var service = new AssignmentService(db, current, new PermissionMutationGuard(db, current, permission, d.Clock), permission,
                new SessionService(db, d.Clock, current, new EffectiveRolePolicy(db)), AccountProvisioningTests.Audit(d, db), d.Clock);
            var result = operation == "grant" ? await service.GrantAsync(actor, new(f.UserId, f.Site, IdentityCatalog.StaffRoleId, "race"), default)
                : await service.ReplaceAsync(actor, original, new(f.SiblingSite, IdentityCatalog.StaffRoleId, 1, "race"), default);
            return AccountProvisioningTests.Status(result);
        }
        var first = Attempt(); var second = Attempt();
        try { await gate.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10)); } finally { gate.Resume.TrySetResult(); }
        Assert.Equal(new int?[] { success, 409 }, (await Task.WhenAll(first, second)).OrderBy(x => x));
        Assert.Equal(1, await observer.Set<ScopeAssignment>().CountAsync(x => x.RevokedAtUtc == null));
        Assert.Equal(operation == "grant" ? 1 : 2, (await observer.Set<IdentityUser>().SingleAsync(x => x.Id == f.UserId)).SecurityVersion);
        Assert.Equal(operation == "grant" ? 1 : 2, await observer.Set<ScopeAssignment>().CountAsync());
    }

    private sealed class LockGate : DbCommandInterceptor
    {
        private int arrivals;
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("pg_advisory_xact_lock(7241002)", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref arrivals) == 2) Ready.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    [Theory]
    [InlineData("grant", false)]
    [InlineData("replace", false)]
    [InlineData("revoke", false)]
    [InlineData("list", false)]
    [InlineData("users", false)]
    [InlineData("roles", false)]
    [InlineData("grant", true)]
    [InlineData("replace", true)]
    [InlineData("revoke", true)]
    [InlineData("list", true)]
    [InlineData("users", true)]
    [InlineData("roles", true)]
    public async Task Service_rechecks_current_permission_and_revoked_session(string operation, bool revoke)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        var original = await f.GrantAsync(f.Site, "staff");
        await using var scope = d.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>();
        var current = scope.ServiceProvider.GetRequiredService<RequestSession>();
        current.Entity = await db.Set<IdentitySession>().AsNoTracking().SingleAsync(x => x.UserId == actor && x.RevokedAtUtc == null);
        if (revoke) await db.Set<IdentitySession>().Where(x => x.UserId == actor).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAtUtc, d.Clock.GetUtcNow()));
        else
        {
            var permission = await db.Set<IdentityPermission>().SingleAsync(x => x.Capability == "scope-assignments:manage");
            await db.Set<RolePermission>().Where(x => x.PermissionId == permission.Id).ExecuteDeleteAsync();
        }
        var service = scope.ServiceProvider.GetRequiredService<AssignmentService>();
        var result = operation switch
        {
            "grant" => await service.GrantAsync(actor, new(f.UserId, f.Site, IdentityCatalog.StaffRoleId, "test"), default),
            "replace" => await service.ReplaceAsync(actor, original, new(f.SiblingSite, IdentityCatalog.StaffRoleId, 1, "test"), default),
            "revoke" => await service.RevokeAsync(actor, original, new(1, "test"), default),
            "users" => await service.UsersAsync(null, 1, 25, default),
            "roles" => await service.RolesAsync(null, 1, 25, default),
            _ => await service.ListAsync(null, null, null, 1, 25, default)
        };
        Assert.Equal(revoke ? 401 : 403, AccountProvisioningTests.Status(result));
        Assert.Equal(1, await db.Set<ScopeAssignment>().CountAsync());
        Assert.False(await db.Set<ScopeAssignment>().AnyAsync(x => x.RevokedAtUtc != null || x.Version != 1));
        Assert.Equal(0, (await db.Set<IdentityUser>().SingleAsync(x => x.Id == f.UserId)).SecurityVersion);
        Assert.Null((await db.AuditEvents.SingleAsync(x => x.EventType == "scope.assignment.denied")).ActingRoleId);
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("replace")]
    [InlineData("revoke")]
    [InlineData("denial")]
    [InlineData("binding")]
    public async Task Audit_failure_rolls_back_assignment_versions_and_target_session(string operation)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        // Setup bypass is not evidence for grant behavior; the HTTP operation below is.
        var original = operation is "replace" or "revoke" ? await f.GrantAsync(f.Site, "staff") : Guid.Empty;
        using var target = await ScopedIdentityLifecycleTests.LoginTargetAsync(d);
        var token = await IdentityTestDriver.TokenAsync(d.Client);
        await using var db = d.Database.CreateContext();
        var auditCount = await db.AuditEvents.CountAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION reject_assignment_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.event_type LIKE 'scope.assignment.%' THEN RAISE EXCEPTION 'audit fault'; END IF; RETURN NEW; END $$; CREATE TRIGGER reject_assignment_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION reject_assignment_audit();");
        using var request = IdentityTestDriver.Mutation(token, operation is "replace" or "revoke" ? $"{Root}/{original}/{operation}" : Root);
        request.Content = operation == "binding" ? JsonContent.Create(new { actorId = Guid.NewGuid() })
            : operation == "replace" ? JsonContent.Create(new { scope = f.SiblingSite, roleId = IdentityCatalog.StaffRoleId, expectedVersion = 1, reason = "test" })
            : operation == "revoke" ? JsonContent.Create(new { expectedVersion = 1, reason = "test" })
            : JsonContent.Create(new { userId = operation == "denial" ? actor : f.UserId, scope = f.Site, roleId = IdentityCatalog.StaffRoleId, reason = "test" });
        using var response = await d.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(original == Guid.Empty ? 0 : 1, await db.Set<ScopeAssignment>().CountAsync());
        Assert.False(await db.Set<ScopeAssignment>().AnyAsync(x => x.RevokedAtUtc != null || x.RevokedBy != null || x.RevocationReason != null || x.Version != 1));
        Assert.Equal(0, (await db.Set<IdentityUser>().SingleAsync(x => x.Id == f.UserId)).SecurityVersion);
        Assert.False(await db.Set<IdentitySession>().AnyAsync(x => x.UserId == f.UserId && x.RevokedAtUtc != null));
        Assert.Equal(auditCount, await db.AuditEvents.CountAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_assignment_audit ON audit_events; DROP FUNCTION reject_assignment_audit();");
        Assert.Equal(HttpStatusCode.OK, (await target.GetAsync("/api/v1/auth/session")).StatusCode);
    }
}
