using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Identity.Data;
using TPR10.Api.Organization.Data;
using TPR10.Api.Scopes;
using TPR10.Api.Scopes.Data;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class ScopeDiscoveryTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Site_discovery_does_not_inherit_parent_or_sibling_permissions()
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var f = await ScopeFixture.CreateAsync(d);
        await ScopeAuthorizationTests.SeedCatalogAsync(d);
        await f.GrantAsync(f.Site, "staff", "scope-probe:read", "users:manage");
        await f.RecordAsync(f.Site, "ข้อมูลลับไม่ใช่ breadcrumb", "restricted-marker");
        Assert.Equal(HttpStatusCode.OK, (await d.LoginAsync("scope-user", MfaTests.Password)).StatusCode);
        using var response = await d.Client.GetAsync("/api/v1/scopes");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.True(response.Headers.Contains("X-Correlation-ID"));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(f.Site, item.GetProperty("scope").Deserialize<ScopeKey>(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal("พื้นที่หนึ่ง", item.GetProperty("workspaceName").GetString());
        Assert.Equal("โครงการหนึ่ง", item.GetProperty("projectName").GetString());
        Assert.Equal("ไซต์หนึ่ง", item.GetProperty("siteName").GetString());
        Assert.Equal(new[] { "scope-probe:read" }, item.GetProperty("capabilities").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(new[] { "capabilities", "projectName", "scope", "siteName", "workspaceName" }, item.EnumerateObject().Select(x => x.Name).Order());
        Assert.DoesNotContain("restricted-marker", json.RootElement.ToString());
        await using var db = d.Database.CreateContext();
        var audit = await db.AuditEvents.SingleAsync(x => x.EventType == "scope.discovery.list");
        Assert.Equal(f.UserId, audit.ActorId);
        Assert.Null(audit.ActingRoleId);
        Assert.Null(audit.WorkspaceId);
        Assert.Equal("success", audit.Outcome);
        Assert.Equal(response.Headers.GetValues("X-Correlation-ID").Single(), audit.CorrelationId);
    }

    [Fact]
    public async Task Dedupe_before_pagination_keeps_exact_capabilities_and_stable_order()
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var f = await ScopeFixture.CreateAsync(d);
        await f.GrantAsync(f.Workspace, "staff", "scope-probe:read");
        await f.GrantAsync(f.Project, "staff", "scope-probe:write");
        await f.GrantAsync(f.Site, "staff", "scope-probe:read");
        await f.GrantAsync(f.Site, "staff", "scope-probe:write");
        await f.GrantAsync(f.SiblingSite, "staff");
        await d.LoginAsync("scope-user", MfaTests.Password);
        var all = await d.Client.GetFromJsonAsync<Page<ScopeChoice>>("/api/v1/scopes?pageSize=1000");
        Assert.Equal(4, all!.Total); Assert.Equal(100, all.PageSize);
        Assert.Equal(f.Workspace, all.Items[0].Scope); Assert.Equal(f.Project, all.Items[1].Scope);
        Assert.Equal(new[] { "scope-probe:read", "scope-probe:write" }, all.Items.Single(x => x.Scope == f.Site).Capabilities);
        Assert.Equal(new[] { "scope-probe:write" }, all.Items.Single(x => x.Scope == f.Project).Capabilities);
        Assert.Empty(all.Items.Single(x => x.Scope == f.SiblingSite).Capabilities);
        for (var page = 1; page <= 4; page++)
        {
            var part = await d.Client.GetFromJsonAsync<Page<ScopeChoice>>($"/api/v1/scopes?page={page}&pageSize=1");
            Assert.Equal(4, part!.Total); Assert.Equal(page, part.PageNumber);
            Assert.Equal(all.Items[page - 1].Scope, Assert.Single(part.Items).Scope);
        }
        Assert.Empty((await d.Client.GetFromJsonAsync<Page<ScopeChoice>>("/api/v1/scopes?page=5&pageSize=1"))!.Items);
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("workspace")]
    [InlineData("project")]
    [InlineData("site")]
    public async Task Unavailable_assignments_disappear_without_exposing_other_scopes(string state)
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var f = await ScopeFixture.CreateAsync(d);
        var id = await f.GrantAsync(f.Site, "staff", "scope-probe:read");
        await f.GrantAsync(f.OtherSite, "staff", "scope-probe:write");
        await using var db = d.Database.CreateContext();
        if (state == "revoked") await db.Set<ScopeAssignment>().Where(x => x.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.RevokedAtUtc, d.Clock.GetUtcNow()).SetProperty(x => x.RevokedBy, f.UserId).SetProperty(x => x.RevocationReason, "test"));
        if (state == "workspace") await db.Set<Workspace>().Where(x => x.Id == f.Workspace.WorkspaceId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        if (state == "project") await db.Set<Project>().Where(x => x.Id == f.Project.ProjectId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        if (state == "site") await db.Set<Site>().Where(x => x.Id == f.Site.SiteId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        await d.LoginAsync("scope-user", MfaTests.Password);
        var page = await d.Client.GetFromJsonAsync<Page<ScopeChoice>>("/api/v1/scopes");
        Assert.Equal(f.OtherSite, Assert.Single(page!.Items).Scope);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_assignment_including_admin_has_empty_discovery(bool admin)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        if (admin) await RoleAuthorizationTests.AdminAsync(d);
        else { await d.SeedUserAsync("staff", MfaTests.Password, ["scope-probe:read"]); await d.LoginAsync("staff", MfaTests.Password); }
        await ScopeFixture.CreateAsync(d);
        var page = await d.Client.GetFromJsonAsync<Page<ScopeChoice>>("/api/v1/scopes");
        Assert.Empty(page!.Items); Assert.Equal(0, page.Total); Assert.Equal(25, page.PageSize);
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=0")]
    [InlineData("page=-1")]
    [InlineData("page=2147483647&pageSize=100")]
    [InlineData("page=bad")]
    public async Task Invalid_pagination_is_audited(string query)
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await d.SeedUserAsync("staff", MfaTests.Password, []); await d.LoginAsync("staff", MfaTests.Password);
        Assert.Equal(HttpStatusCode.BadRequest, (await d.Client.GetAsync("/api/v1/scopes?" + query)).StatusCode);
        await using var db = d.Database.CreateContext();
        Assert.Equal(1, await db.AuditEvents.CountAsync(x => x.EventType == "scope.access.denied"));
    }

    [Fact]
    public async Task Error_problem_contains_the_response_correlation_id()
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await d.SeedUserAsync("staff", MfaTests.Password, []); await d.LoginAsync("staff", MfaTests.Password);
        using var response = await d.Client.GetAsync("/api/v1/scopes?page=0");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(response.Headers.GetValues("X-Correlation-ID").Single(), body.RootElement.GetProperty("correlationId").GetString());
    }

    [Theory]
    [InlineData("anonymous", 401)]
    [InlineData("password", 403)]
    [InlineData("enrollment", 403)]
    [InlineData("expired-mfa", 403)]
    public async Task Http_boundary_requires_active_session(string mode, int expected)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        if (mode != "anonymous")
        {
            await d.SeedUserAsync("staff", MfaTests.Password, [], requiresMfa: mode == "enrollment", mustChangePassword: mode == "password");
            await d.LoginAsync("staff", MfaTests.Password);
            if (mode == "expired-mfa") { await AuthorizationTests.ConfirmAsync(d); d.Advance(TimeSpan.FromMinutes(15)); }
        }
        Assert.Equal(expected, (int)(await d.Client.GetAsync("/api/v1/scopes")).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Audit_failure_never_returns_discovery_payload(bool denied)
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var f = await ScopeFixture.CreateAsync(d); await f.GrantAsync(f.Site, "staff", "scope-probe:read");
        await d.LoginAsync("scope-user", MfaTests.Password);
        await using var db = d.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION reject_scope_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.event_type LIKE 'scope.%' THEN RAISE EXCEPTION 'private-db-error'; END IF; RETURN NEW; END $$; CREATE TRIGGER reject_scope_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION reject_scope_audit();");
        using var response = await d.Client.GetAsync("/api/v1/scopes" + (denied ? "?page=bad" : ""));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("พื้นที่หนึ่ง", body); Assert.DoesNotContain("private-db-error", body);
        Assert.False(await db.AuditEvents.AnyAsync(x => x.EventType.StartsWith("scope.")));
    }

    [Fact]
    public async Task Client_identifiers_do_not_select_another_users_discovery()
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var f = await ScopeFixture.CreateAsync(d); await f.GrantAsync(f.Site, "staff", "scope-probe:read");
        var other = await d.SeedUserAsync("other", MfaTests.Password, []);
        await (f with { UserId = other }).GrantAsync(f.OtherSite, "staff", "scope-probe:write");
        await d.LoginAsync("scope-user", MfaTests.Password);
        d.Client.DefaultRequestHeaders.Add("X-User-Id", other.ToString());
        d.Client.DefaultRequestHeaders.Add("X-Workspace-Id", f.OtherSite.WorkspaceId.ToString());
        var response = await d.Client.GetFromJsonAsync<Page<ScopeChoice>>($"/api/v1/scopes?userId={other}&workspaceId={f.OtherSite.WorkspaceId}");
        Assert.Equal(f.Site, Assert.Single(response!.Items).Scope);
    }
}
