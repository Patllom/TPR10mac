using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Identity.Accounts;
using TPR10.Api.Identity.Data;
using TPR10.Api.Organization.Data;
using TPR10.Api.Scopes;
using TPR10.Api.Scopes.Data;
using static TPR10.Api.IntegrationTests.MfaTests;
using static TPR10.Api.IntegrationTests.AuthorizationTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class AssignmentApiTests(PostgresFixture postgres)
{
    internal const string Root = "/api/v1/scope-assignments";

    [Theory]
    [InlineData("10000000-0000-0000-0000-000000000003")]
    [InlineData("10000000-0000-0000-0000-000000000004")]
    [InlineData("10000000-0000-0000-0000-000000000005")]
    public async Task Privileged_grant_invalidates_session_and_next_login_requires_mfa(string roleId)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        using var target = await ScopedIdentityLifecycleTests.LoginTargetAsync(d);
        using var granted = await d.PostAsync(Root, new { userId = f.UserId, scope = f.Site, roleId = Guid.Parse(roleId), reason = "privileged" });
        Assert.Equal(HttpStatusCode.Created, granted.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await target.GetAsync("/api/v1/auth/session")).StatusCode);
        using var request = IdentityTestDriver.Mutation(await IdentityTestDriver.TokenAsync(target), "/api/v1/auth/login");
        request.Content = JsonContent.Create(new { username = "scope-user", password = Password });
        using var login = await target.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal("MfaEnrollmentRequired", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stage").GetString());
    }

    [Fact]
    public async Task Mutation_routes_reject_missing_csrf_without_changes()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await RoleAuthorizationTests.AdminAsync(d);
        foreach (var path in new[] { Root, Root + "/10000000-0000-0000-0000-000000000010/replace", Root + "/10000000-0000-0000-0000-000000000010/revoke" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new { }) };
            request.Headers.Add("Origin", "https://localhost:4443");
            Assert.Equal(HttpStatusCode.Forbidden, (await d.Client.SendAsync(request)).StatusCode);
        }
        await using var db = d.Database.CreateContext();
        Assert.Empty(await db.Set<ScopeAssignment>().ToArrayAsync());
    }

    [Theory]
    [InlineData("replace")]
    [InlineData("revoke")]
    public async Task Assignment_owner_is_immutable_even_with_forged_body(string operation)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        var id = await CreateAsync(d, f);
        var body = new Dictionary<string, object?> { ["userId"] = actor, ["reason"] = "forged", ["expectedVersion"] = 1 };
        if (operation == "replace") { body["scope"] = f.SiblingSite; body["roleId"] = IdentityCatalog.StaffRoleId; }
        using var response = await d.PostAsync($"{Root}/{id}/{operation}", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = d.Database.CreateContext();
        var row = await db.Set<ScopeAssignment>().SingleAsync();
        Assert.Equal(f.UserId, row.UserId); Assert.Null(row.RevokedAtUtc); Assert.Equal(1, row.Version);
        Assert.Equal(actor, (await db.AuditEvents.SingleAsync(x => x.EventType == "scope.assignment.denied")).ActorId);
    }

    [Theory]
    [InlineData("users")]
    [InlineData("roles")]
    public async Task Options_prefix_is_bounded_and_treated_as_literal(string options)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await RoleAuthorizationTests.AdminAsync(d);
        var path = Root + "/options/" + options + "?prefix=";
        Assert.Equal(HttpStatusCode.BadRequest, (await d.Client.GetAsync(path + new string('x', 101))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await d.Client.GetAsync(path + "bad%0Avalue")).StatusCode);
        using var response = await d.Client.GetAsync(path + "%25");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("total").GetInt32());
    }

    [Theory]
    [InlineData("anonymous", 401)]
    [InlineData("staff", 403)]
    [InlineData("no-mfa", 403)]
    [InlineData("expired-mfa", 403)]
    [InlineData("scoped-only", 403)]
    public async Task All_routes_require_system_management_and_recent_mfa(string scenario, int status)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        if (scenario == "scoped-only")
        {
            var f = await ScopeFixture.CreateAsync(d);
            await f.GrantAsync(f.Site, "staff", "scope-assignments:manage");
            await d.LoginAsync("scope-user", Password); await ConfirmAsync(d);
        }
        else if (scenario != "anonymous")
        {
            await d.SeedUserAsync("actor", Password, scenario == "staff" ? [] : ["scope-assignments:manage"]);
            await d.LoginAsync("actor", Password);
            if (scenario == "expired-mfa") { await ConfirmAsync(d); d.Advance(TimeSpan.FromMinutes(15)); }
        }
        var token = await IdentityTestDriver.TokenAsync(d.Client);
        foreach (var (method, path) in new[] { ("GET", Root), ("POST", Root), ("POST", Root + "/10000000-0000-0000-0000-000000000010/replace"),
            ("POST", Root + "/10000000-0000-0000-0000-000000000010/revoke"), ("GET", Root + "/options/users"), ("GET", Root + "/options/roles") })
        {
            using var request = IdentityTestDriver.Mutation(token, path);
            request.Method = new(method); request.Content = method == "GET" ? null : JsonContent.Create(new { });
            using var response = await d.Client.SendAsync(request);
            Assert.Equal(status, (int)response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        }
    }

    [Fact]
    public async Task Assignment_only_manager_gets_minimal_options_not_identity_administration()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await d.SeedUserAsync("manager", Password, ["scope-assignments:manage"]);
        await d.LoginAsync("manager", Password); await ConfirmAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        await using var db = d.Database.CreateContext();
        await db.Set<IdentityUser>().Where(x => x.Id == f.UserId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Email, "private@example.test"));
        var role = Guid.NewGuid();
        db.Add(new IdentityRole { Id = role, Name = "Custom option", RoleClass = "staff", CreatedAtUtc = d.Clock.GetUtcNow() });
        foreach (var p in await db.Set<IdentityPermission>().Where(x => x.Capability == "users:manage" || x.Capability == "scope-probe:read").ToArrayAsync())
            db.Add(new RolePermission { RoleId = role, PermissionId = p.Id });
        await db.SaveChangesAsync();
        using var users = await d.Client.GetAsync(Root + "/options/users?prefix=sco");
        Assert.Equal(HttpStatusCode.OK, users.StatusCode);
        var user = (await users.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().Single();
        Assert.Equal(new[] { "id", "isActive", "username" }, user.EnumerateObject().Select(x => x.Name).OrderBy(x => x));
        Assert.Equal(f.UserId, user.GetProperty("id").GetGuid());
        using var allUsers = await d.Client.GetAsync(Root + "/options/users");
        Assert.DoesNotContain((await allUsers.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray(), x => x.GetProperty("id").GetGuid() == actor);
        using var roles = await d.Client.GetAsync(Root + "/options/roles?prefix=Custom");
        Assert.Equal(HttpStatusCode.OK, roles.StatusCode);
        var option = (await roles.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().Single();
        Assert.Equal(new[] { "scope-probe:read" }, option.GetProperty("businessCapabilities").EnumerateArray().Select(x => x.GetString()));
        using var allRoles = await d.Client.GetAsync(Root + "/options/roles");
        Assert.DoesNotContain((await allRoles.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray(), x => x.GetProperty("roleClass").GetString() == "system-administration");
        Assert.Equal(HttpStatusCode.Forbidden, (await d.Client.GetAsync("/api/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await d.Client.GetAsync("/api/v1/roles")).StatusCode);
    }

    [Theory]
    [InlineData("", 25, 1)]
    [InlineData("?pageSize=999", 100, 1)]
    [InlineData("?page=2&pageSize=10", 10, 2)]
    public async Task Listings_and_options_are_bounded_with_stable_pagination(string query, int size, int page)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        await using var db = d.Database.CreateContext();
        for (var i = 0; i < 105; i++)
        {
            var role = Guid.NewGuid(); var user = Guid.NewGuid();
            db.Add(new IdentityRole { Id = role, Name = $"Option-{i:D3}", RoleClass = "staff", CreatedAtUtc = d.Clock.GetUtcNow() });
            db.Add(new IdentityUser { Id = user, Username = $"option-{i:D3}", NormalizedUsername = $"OPTION-{i:D3}", CreatedAtUtc = d.Clock.GetUtcNow() });
            db.Add(new ScopeAssignment
            {
                Id = Guid.NewGuid(),
                UserId = f.UserId,
                WorkspaceId = f.Workspace.WorkspaceId,
                RoleId = role,
                CreatedBy = actor,
                CreatedAtUtc = d.Clock.GetUtcNow().AddSeconds(i),
                Reason = "setup"
            });
        }
        await db.SaveChangesAsync();
        foreach (var path in new[] { Root, Root + "/options/users", Root + "/options/roles" })
        {
            using var response = await d.Client.GetAsync(path + query);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(size, json.GetProperty("items").GetArrayLength());
            Assert.Equal(size, json.GetProperty("pageSize").GetInt32()); Assert.Equal(page, json.GetProperty("pageNumber").GetInt32());
            Assert.True(response.Headers.CacheControl?.NoStore); Assert.True(response.Headers.Contains("X-Correlation-ID"));
            if (page == 2 && path.EndsWith("/users")) Assert.Equal("option-010", json.GetProperty("items")[0].GetProperty("username").GetString());
        }
    }

    [Theory]
    [InlineData("?page=0")]
    [InlineData("?pageSize=0")]
    [InlineData("?page=2147483647&pageSize=100")]
    [InlineData("?page=bad")]
    public async Task Invalid_pagination_is_audited_for_all_read_routes(string query)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await RoleAuthorizationTests.AdminAsync(d);
        foreach (var path in new[] { Root, Root + "/options/users", Root + "/options/roles" })
            Assert.Equal(HttpStatusCode.BadRequest, (await d.Client.GetAsync(path + query)).StatusCode);
        await using var db = d.Database.CreateContext();
        Assert.Equal(3, await db.AuditEvents.CountAsync(x => x.EventType == "scope.assignment.denied"));
    }

    [Fact]
    public async Task List_filters_exact_nullable_scope_user_and_revoked_state()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        var workspace = await CreateAsync(d, f, f.Workspace); await CreateAsync(d, f, f.Project); await CreateAsync(d, f, f.Site);
        using var scoped = await d.Client.GetAsync($"{Root}?userId={f.UserId}&workspaceId={f.Workspace.WorkspaceId}&revoked=false");
        Assert.Equal(HttpStatusCode.OK, scoped.StatusCode);
        var item = (await scoped.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().Single();
        Assert.Equal(workspace, item.GetProperty("id").GetGuid());
        await d.PostAsync($"{Root}/{workspace}/revoke", new { expectedVersion = 1, reason = "ถอน" });
        using var history = await d.Client.GetAsync($"{Root}?userId={f.UserId}&revoked=true");
        Assert.Equal(workspace, (await history.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.BadRequest, (await d.Client.GetAsync($"{Root}?siteId={f.Site.SiteId}")).StatusCode);
    }

    [Theory]
    [InlineData("actorId")]
    [InlineData("missing")]
    [InlineData("scope-null")]
    public async Task Forged_or_incomplete_body_is_denied_and_audited(string field)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        var body = new Dictionary<string, object?> { ["userId"] = f.UserId, ["scope"] = f.Site, ["roleId"] = IdentityCatalog.StaffRoleId, ["reason"] = "test" };
        if (field == "actorId") body[field] = f.UserId;
        if (field == "missing") body.Remove("reason");
        if (field == "scope-null") body["scope"] = null;
        using var response = await d.PostAsync(Root, body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = d.Database.CreateContext();
        Assert.Empty(await db.Set<ScopeAssignment>().ToArrayAsync());
        Assert.Equal(actor, (await db.AuditEvents.SingleAsync(x => x.EventType == "scope.assignment.denied")).ActorId);
    }
    internal static async Task<Guid> CreateAsync(IdentityTestDriver d, ScopeFixture f, ScopeKey? key = null)
    {
        using var response = await d.PostAsync(Root, new { userId = f.UserId, scope = key ?? f.Site, roleId = IdentityCatalog.StaffRoleId, reason = "มอบหมาย" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("replace")]
    [InlineData("revoke")]
    public async Task Administrator_cannot_modify_own_assignments(string operation)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        await using var db = d.Database.CreateContext();
        var id = Guid.NewGuid();
        if (operation != "grant")
        {
            db.Add(new ScopeAssignment
            {
                Id = id,
                UserId = actor,
                WorkspaceId = f.Site.WorkspaceId,
                ProjectId = f.Site.ProjectId,
                SiteId = f.Site.SiteId,
                RoleId = IdentityCatalog.StaffRoleId,
                CreatedBy = actor,
                CreatedAtUtc = d.Clock.GetUtcNow(),
                Reason = "setup"
            });
            await db.SaveChangesAsync();
        }
        using var response = operation == "grant"
            ? await d.PostAsync(Root, new { userId = actor, scope = f.Site, roleId = IdentityCatalog.StaffRoleId, reason = "self" })
            : operation == "replace" ? await d.PostAsync($"{Root}/{id}/replace", new { scope = f.SiblingSite, roleId = IdentityCatalog.StaffRoleId, expectedVersion = 1, reason = "self" })
            : await d.PostAsync($"{Root}/{id}/revoke", new { expectedVersion = 1, reason = "self" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(operation == "grant" ? 0 : 1, await db.Set<ScopeAssignment>().CountAsync());
        Assert.False(await db.Set<ScopeAssignment>().AnyAsync(x => x.RevokedAtUtc != null || x.Version != 1));
        Assert.Equal(actor, (await db.AuditEvents.SingleAsync(x => x.EventType == "scope.assignment.denied")).ActorId);
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("project")]
    [InlineData("site")]
    public async Task Grant_uses_exact_scope_session_actor_and_revokes_old_cookie(string level)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        using var target = await ScopedIdentityLifecycleTests.LoginTargetAsync(d);
        var key = level == "workspace" ? f.Workspace : level == "project" ? f.Project : f.Site;
        await using var db = d.Database.CreateContext();
        var globalRoles = await db.Set<UserRole>().Where(x => x.UserId == f.UserId).OrderBy(x => x.RoleId).Select(x => x.RoleId).ToArrayAsync();
        var id = await CreateAsync(d, f, key);
        var a = await db.Set<ScopeAssignment>().SingleAsync();
        Assert.Equal(id, a.Id); Assert.Equal(actor, a.CreatedBy); Assert.Equal(f.UserId, a.UserId);
        Assert.Equal(key, new ScopeKey(a.WorkspaceId, a.ProjectId, a.SiteId)); Assert.Equal(1, a.Version);
        Assert.Equal("มอบหมาย", a.Reason);
        Assert.Equal(globalRoles, await db.Set<UserRole>().Where(x => x.UserId == f.UserId).OrderBy(x => x.RoleId).Select(x => x.RoleId).ToArrayAsync());
        Assert.Equal(1, (await db.Set<IdentityUser>().SingleAsync(x => x.Id == f.UserId)).SecurityVersion);
        Assert.Equal(HttpStatusCode.Unauthorized, (await target.GetAsync("/api/v1/auth/session")).StatusCode);
        var audit = await db.AuditEvents.SingleAsync(x => x.EventType == "scope.assignment.granted");
        Assert.Equal(actor, audit.ActorId); Assert.Equal(IdentityCatalog.AdministratorRoleId, audit.ActingRoleId);
        Assert.Equal(key.WorkspaceId, audit.WorkspaceId); Assert.Equal(key.ProjectId, audit.ProjectId); Assert.Equal(key.SiteId, audit.SiteId);
    }

    [Theory]
    [InlineData("system-role", 400)]
    [InlineData("missing-role", 404)]
    [InlineData("inactive-user", 409)]
    [InlineData("missing-user", 404)]
    [InlineData("inactive-workspace", 409)]
    [InlineData("inactive-project", 409)]
    [InlineData("inactive-site", 409)]
    [InlineData("cross-parent", 404)]
    [InlineData("site-without-project", 400)]
    [InlineData("empty-id", 400)]
    [InlineData("empty-reason", 400)]
    [InlineData("control-reason", 400)]
    [InlineData("long-reason", 400)]
    public async Task Invalid_grant_does_not_mutate_assignments_or_target_version(string scenario, int status)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        await using var db = d.Database.CreateContext();
        if (scenario == "inactive-user") await db.Set<IdentityUser>().Where(x => x.Id == f.UserId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        if (scenario == "inactive-workspace") await db.Set<Workspace>().Where(x => x.Id == f.Site.WorkspaceId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        if (scenario == "inactive-project") await db.Set<Project>().Where(x => x.Id == f.Site.ProjectId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        if (scenario == "inactive-site") await db.Set<Site>().Where(x => x.Id == f.Site.SiteId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        var scope = scenario == "cross-parent" ? new ScopeKey(f.OtherSite.WorkspaceId, f.Site.ProjectId, f.Site.SiteId)
            : scenario == "site-without-project" ? new(f.Site.WorkspaceId, null, f.Site.SiteId) : scenario == "empty-id" ? new(Guid.Empty) : f.Site;
        using var response = await d.PostAsync(Root, new
        {
            userId = scenario == "missing-user" ? Guid.NewGuid() : f.UserId,
            scope,
            roleId = scenario == "system-role" ? IdentityCatalog.AdministratorRoleId : scenario == "missing-role" ? Guid.NewGuid() : IdentityCatalog.StaffRoleId,
            reason = scenario == "empty-reason" ? " " : scenario == "control-reason" ? "bad\nreason" : scenario == "long-reason" ? new string('x', 501) : "test"
        });
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Empty(await db.Set<ScopeAssignment>().ToArrayAsync());
        Assert.Equal(0, (await db.Set<IdentityUser>().SingleAsync(x => x.Id == f.UserId)).SecurityVersion);
        Assert.Equal(1, await db.AuditEvents.CountAsync(x => x.EventType == "scope.assignment.denied"));
    }

    [Fact]
    public async Task Replace_retains_history_noop_is_stable_and_revoke_is_versioned()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        var original = await CreateAsync(d, f);
        using var target = await ScopedIdentityLifecycleTests.LoginTargetAsync(d);
        using var noop = await d.PostAsync($"{Root}/{original}/replace", new { scope = f.Site, roleId = IdentityCatalog.StaffRoleId, expectedVersion = 1, reason = "ไม่เปลี่ยน" });
        Assert.Equal(HttpStatusCode.OK, noop.StatusCode);
        Assert.Equal(original, (await noop.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await target.GetAsync("/api/v1/auth/session")).StatusCode);
        using var replaced = await d.PostAsync($"{Root}/{original}/replace", new { scope = f.SiblingSite, roleId = IdentityCatalog.StaffRoleId, expectedVersion = 1, reason = "ย้ายขอบเขต" });
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var next = (await replaced.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.NotEqual(original, next);
        Assert.Equal(HttpStatusCode.Unauthorized, (await target.GetAsync("/api/v1/auth/session")).StatusCode);
        await using var db = d.Database.CreateContext();
        var old = await db.Set<ScopeAssignment>().SingleAsync(x => x.Id == original);
        Assert.NotNull(old.RevokedAtUtc); Assert.Equal(actor, old.RevokedBy); Assert.Equal(2, old.Version); Assert.Equal(f.Site.SiteId, old.SiteId);
        var current = await db.Set<ScopeAssignment>().SingleAsync(x => x.Id == next);
        Assert.Equal(f.UserId, current.UserId); Assert.Equal(f.SiblingSite.SiteId, current.SiteId); Assert.Equal(1, current.Version);
        using var target2 = await ScopedIdentityLifecycleTests.LoginTargetAsync(d);
        using var revoked = await d.PostAsync($"{Root}/{next}/revoke", new { expectedVersion = 1, reason = "ถอน" });
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await target2.GetAsync("/api/v1/auth/session")).StatusCode);
        using var again = await d.PostAsync($"{Root}/{next}/revoke", new { expectedVersion = 1, reason = "ซ้ำ" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(3, (await db.Set<IdentityUser>().SingleAsync(x => x.Id == f.UserId)).SecurityVersion);
        Assert.Equal(2, await db.Set<ScopeAssignment>().CountAsync());
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("stale")]
    public async Task Replace_conflict_preserves_old_assignment_and_session(string conflict)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        var original = await CreateAsync(d, f);
        if (conflict == "duplicate") await CreateAsync(d, f, f.SiblingSite);
        using var target = await ScopedIdentityLifecycleTests.LoginTargetAsync(d);
        using var response = await d.PostAsync($"{Root}/{original}/replace", new { scope = f.SiblingSite, roleId = IdentityCatalog.StaffRoleId, expectedVersion = conflict == "stale" ? 2 : 1, reason = "test" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await using var db = d.Database.CreateContext();
        Assert.False(await db.Set<ScopeAssignment>().AnyAsync(x => x.RevokedAtUtc != null || x.Version != 1));
        Assert.Equal(conflict == "duplicate" ? 2 : 1, (await db.Set<IdentityUser>().SingleAsync(x => x.Id == f.UserId)).SecurityVersion);
        Assert.Equal(HttpStatusCode.OK, (await target.GetAsync("/api/v1/auth/session")).StatusCode);
    }
}
