using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Identity.Data;
using TPR10.Api.Organization.Data;
using TPR10.Api.Scopes.Data;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Data;
using TPR10.Api.Organization;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Identity.Accounts;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using static TPR10.Api.IntegrationTests.AuthorizationTests;
using static TPR10.Api.IntegrationTests.OrganizationApiTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class OrganizationLifecycleTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("create", 201)]
    [InlineData("update", 200)]
    public async Task Concurrent_requests_with_same_code_or_version_have_one_winner(string operation, int success)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        await using var observer = d.Database.CreateContext();
        var snapshot = await observer.Set<IdentitySession>().AsNoTracking().SingleAsync(x => x.UserId == actor && x.RevokedAtUtc == null);
        var gate = new IdentityLockGate();
        async Task<int?> Attempt()
        {
            await using var db = new Tpr10DbContext(new DbContextOptionsBuilder<Tpr10DbContext>().UseNpgsql(d.Database.ConnectionString).AddInterceptors(gate).Options);
            var current = new RequestSession { Entity = snapshot };
            var permission = new Identity.Authorization.PermissionContext();
            var audit = AccountProvisioningTests.Audit(d, db);
            var sessions = new SessionService(db, d.Clock, current, new EffectiveRolePolicy(db));
            var service = new OrganizationService(db, current, new Identity.Authorization.PermissionMutationGuard(db, current, permission, d.Clock),
                permission, new Scopes.Assignments.AssignmentLifecycle(db, audit, d.Clock, permission), sessions, audit, d.Clock);
            var result = operation == "create" ? await service.CreateAsync(actor, OrganizationKind.Workspace, null, new("RACE", "พื้นที่"), default)
                : await service.UpdateAsync(actor, OrganizationKind.Workspace, null, f.Workspace.WorkspaceId, new("แก้พร้อมกัน", true, 1, "race"), default);
            return AccountProvisioningTests.Status(result);
        }
        var first = Attempt(); var second = Attempt();
        try { await gate.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { gate.Resume.TrySetResult(); }
        Assert.Equal(new int?[] { success, 409 }, (await Task.WhenAll(first, second)).OrderBy(x => x).ToArray());
        if (operation == "create") Assert.Equal(1, await observer.Set<Workspace>().CountAsync(x => x.Code == "RACE"));
        else Assert.Equal(2, (await observer.Set<Workspace>().SingleAsync(x => x.Id == f.Workspace.WorkspaceId)).Version);
        Assert.Equal(1, await observer.AuditEvents.CountAsync(x => x.EventType == (operation == "create" ? "organization.created" : "organization.updated")));
    }

    private sealed class IdentityLockGate : DbCommandInterceptor
    {
        private int arrivals;
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
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
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("list")]
    public async Task Service_rechecks_grants_and_commits_denial_without_business_changes(string operation)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        await using var scope = d.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>();
        var current = scope.ServiceProvider.GetRequiredService<RequestSession>();
        current.Entity = await db.Set<IdentitySession>().AsNoTracking().SingleAsync(x => x.UserId == actor && x.RevokedAtUtc == null);
        var permission = await db.Set<IdentityPermission>().SingleAsync(x => x.Capability == "organization:manage");
        await db.Set<RolePermission>().Where(x => x.PermissionId == permission.Id).ExecuteDeleteAsync();
        var service = scope.ServiceProvider.GetRequiredService<OrganizationService>();
        var result = operation == "create" ? await service.CreateAsync(actor, OrganizationKind.Workspace, null, new("NO", "ห้าม"), default)
            : operation == "update" ? await service.UpdateAsync(actor, OrganizationKind.Workspace, null, f.Workspace.WorkspaceId, new("ห้าม", false, 1, "test"), default)
            : await service.ListAsync(OrganizationKind.Workspace, null, 1, 25, default);
        Assert.Equal(403, AccountProvisioningTests.Status(result));
        Assert.Equal(2, await db.Set<Workspace>().CountAsync());
        Assert.True((await db.Set<Workspace>().SingleAsync(x => x.Id == f.Workspace.WorkspaceId)).IsActive);
        var denial = await db.AuditEvents.SingleAsync(x => x.EventType == "organization.access.denied");
        Assert.Equal(actor, denial.ActorId); Assert.Null(denial.ActingRoleId);
        Assert.Equal("denied", denial.Outcome);
        Assert.Equal("requested-unverified", (await db.AuditMetadata.SingleAsync(x => x.AuditEventId == denial.Id && x.Key == "scope-validation")).Value);
    }

    [Fact]
    public async Task Successful_audit_contains_typed_scope_actor_and_no_request_body_or_credentials()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        using var created = await d.PostAsync(ChildPath("site", f.Workspace.WorkspaceId, f.Project.ProjectId!.Value), new { code = "AUDIT", name = "BODY-MARKER" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = json.RootElement.GetProperty("id").GetGuid();
        await using var db = d.Database.CreateContext();
        var audit = await db.AuditEvents.SingleAsync(x => x.EventType == "organization.created");
        Assert.Equal(actor, audit.ActorId); Assert.Equal(IdentityCatalog.AdministratorRoleId, audit.ActingRoleId);
        Assert.Equal(f.Workspace.WorkspaceId, audit.WorkspaceId); Assert.Equal(f.Project.ProjectId, audit.ProjectId); Assert.Equal(id, audit.SiteId);
        Assert.Equal(id, audit.TargetId); Assert.Equal("site", audit.TargetType); Assert.Equal("success", audit.Outcome);
        Assert.Equal(created.Headers.GetValues("X-Correlation-ID").Single(), audit.CorrelationId);
        var metadata = string.Join(" ", await db.AuditMetadata.Select(x => x.Value).ToArrayAsync());
        Assert.DoesNotContain("BODY-MARKER", metadata); Assert.DoesNotContain(MfaTests.Password, metadata);
    }

    [Theory]
    [InlineData("VALID", "พื้นที่", 201)]
    [InlineData("bad code", "พื้นที่", 400)]
    public async Task Audit_failure_on_create_or_denial_returns_503_and_commits_nothing(string code, string name, int normalStatus)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await RoleAuthorizationTests.AdminAsync(d);
        await using var db = d.Database.CreateContext();
        var token = await IdentityTestDriver.TokenAsync(d.Client);
        var before = await db.AuditEvents.CountAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION reject_org_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.event_type LIKE 'organization.%' THEN RAISE EXCEPTION 'audit fault'; END IF; RETURN NEW; END $$; CREATE TRIGGER reject_org_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION reject_org_audit();");
        using var request = IdentityTestDriver.Mutation(token, Root);
        request.Content = JsonContent.Create(new { code, name });
        using var response = await d.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(await db.Set<Workspace>().ToArrayAsync());
        Assert.Equal(before, await db.AuditEvents.CountAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_org_audit ON audit_events; DROP FUNCTION reject_org_audit();");
        Assert.Equal(normalStatus, (int)(await d.PostAsync(Root, new { code, name })).StatusCode);
    }

    [Theory]
    [InlineData("workspace", 4)]
    [InlineData("project", 3)]
    [InlineData("site", 1)]
    [InlineData("department", 0)]
    public async Task Deactivation_revokes_only_target_subtree_once_and_reactivation_does_not_restore(string kind, int count)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        var ids = new[] { await f.GrantAsync(f.Workspace, "staff"), await f.GrantAsync(f.Project, "staff"),
            await f.GrantAsync(f.Site, "staff"), await f.GrantAsync(f.SiblingSite, "staff"), await f.GrantAsync(f.OtherSite, "staff") };
        using var target = await ScopedIdentityLifecycleTests.LoginTargetAsync(d);
        var path = kind == "workspace" ? Root + "/" + f.Workspace.WorkspaceId
            : kind == "project" ? Root + "/" + f.Workspace.WorkspaceId + "/projects/" + f.Project.ProjectId
            : ChildPath("site", f.Workspace.WorkspaceId, f.Project.ProjectId!.Value) + "/" + f.Site.SiteId;
        if (kind == "department")
        {
            using var created = await d.PostAsync(ChildPath(kind, f.Workspace.WorkspaceId, f.Project.ProjectId!.Value), new { code = "D1", name = "แผนก" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            path = ChildPath(kind, f.Workspace.WorkspaceId, f.Project.ProjectId!.Value) + "/" + json.RootElement.GetProperty("id").GetGuid();
        }
        using var response = await SendAsync(d, HttpMethod.Patch, path, new { name = "ปิดพื้นที่", isActive = false, expectedVersion = 1, reason = "ปิดชั่วคราว" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = d.Database.CreateContext();
        var revoked = await db.Set<ScopeAssignment>().AsNoTracking().Where(x => x.RevokedAtUtc != null).ToArrayAsync();
        Assert.Equal(count, revoked.Length);
        var expected = kind == "workspace" ? ids.Take(4) : kind == "project" ? ids.Skip(1).Take(3) : kind == "site" ? [ids[2]] : Array.Empty<Guid>();
        Assert.Equal(expected.OrderBy(x => x), revoked.Select(x => x.Id).OrderBy(x => x));
        Assert.All(revoked, a => { Assert.Equal(actor, a.RevokedBy); Assert.Equal(2, a.Version); Assert.Equal("ปิดชั่วคราว", a.RevocationReason); });
        Assert.Equal(count == 0 ? 0 : 1, (await db.Set<IdentityUser>().SingleAsync(x => x.Id == f.UserId)).SecurityVersion);
        Assert.Equal(count == 0 ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, (await target.GetAsync("/api/v1/auth/session")).StatusCode);
        using var restored = await SendAsync(d, HttpMethod.Patch, path, new { name = "เปิดพื้นที่", isActive = true, expectedVersion = 2, reason = "เปิดใหม่" });
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal(count, await db.Set<ScopeAssignment>().CountAsync(x => x.RevokedAtUtc != null));
        Assert.All(await db.Set<ScopeAssignment>().AsNoTracking().Where(x => x.RevokedAtUtc != null).ToArrayAsync(), a => Assert.Equal(2, a.Version));
    }

    [Fact]
    public async Task Audit_failure_rolls_back_organization_assignments_versions_and_live_session()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        var assignment = await f.GrantAsync(f.Site, "staff");
        using var target = await ScopedIdentityLifecycleTests.LoginTargetAsync(d);
        var token = await IdentityTestDriver.TokenAsync(d.Client);
        await using var db = d.Database.CreateContext();
        var before = await db.Set<Workspace>().AsNoTracking().SingleAsync(x => x.Id == f.Workspace.WorkspaceId);
        var auditCount = await db.AuditEvents.CountAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION reject_org_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.event_type LIKE 'organization.%' THEN RAISE EXCEPTION 'audit fault'; END IF; RETURN NEW; END $$; CREATE TRIGGER reject_org_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION reject_org_audit();");
        using var request = IdentityTestDriver.Mutation(token, Root + "/" + f.Workspace.WorkspaceId);
        request.Method = HttpMethod.Patch;
        request.Content = JsonContent.Create(new { name = "ห้ามค้าง", isActive = false, expectedVersion = 1, reason = "test" });
        using var response = await d.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var after = await db.Set<Workspace>().AsNoTracking().SingleAsync(x => x.Id == f.Workspace.WorkspaceId);
        Assert.True(after.IsActive); Assert.Equal(before.Name, after.Name); Assert.Equal(before.Version, after.Version);
        Assert.Null(after.UpdatedBy); Assert.Null(after.UpdatedAtUtc);
        var a = await db.Set<ScopeAssignment>().SingleAsync(x => x.Id == assignment);
        Assert.Null(a.RevokedAtUtc); Assert.Null(a.RevokedBy); Assert.Null(a.RevocationReason); Assert.Equal(1, a.Version);
        Assert.Equal(0, (await db.Set<IdentityUser>().SingleAsync(x => x.Id == f.UserId)).SecurityVersion);
        Assert.Null((await db.Set<IdentitySession>().SingleAsync(x => x.UserId == f.UserId)).RevokedAtUtc);
        Assert.Equal(auditCount, await db.AuditEvents.CountAsync());
        Assert.Equal(HttpStatusCode.OK, (await target.GetAsync("/api/v1/auth/session")).StatusCode);
    }
}
