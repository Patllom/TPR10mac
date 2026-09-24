using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Organization.Data;
using TPR10.Api.Scopes.Data;
using static TPR10.Api.IntegrationTests.AuthorizationTests;
using static TPR10.Api.IntegrationTests.MfaTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class OrganizationApiTests(PostgresFixture postgres)
{
    internal const string Root = "/api/v1/organization/workspaces";

    [Theory]
    [InlineData("missing", false)]
    [InlineData("immutable", false)]
    [InlineData("malformed", false)]
    [InlineData("uuid", false)]
    [InlineData("query", false)]
    [InlineData("missing", true)]
    [InlineData("immutable", true)]
    [InlineData("malformed", true)]
    [InlineData("uuid", true)]
    [InlineData("query", true)]
    public async Task Binding_denial_is_audited_and_fails_closed(string scenario, bool auditFailure)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        var token = await IdentityTestDriver.TokenAsync(d.Client);
        await using var db = d.Database.CreateContext();
        var count = await db.AuditEvents.CountAsync();
        if (auditFailure)
            await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION reject_binding_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.event_type LIKE 'organization.%' THEN RAISE EXCEPTION 'audit fault'; END IF; RETURN NEW; END $$; CREATE TRIGGER reject_binding_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION reject_binding_audit();");
        var path = scenario == "query" ? Root + "?page=invalid" : scenario == "uuid" ? Root + "/invalid/projects" : Root + "/" + f.Workspace.WorkspaceId;
        using var request = IdentityTestDriver.Mutation(token, path);
        request.Method = scenario is "query" or "uuid" ? HttpMethod.Get : HttpMethod.Patch;
        request.Content = request.Method == HttpMethod.Get ? null : new StringContent(scenario switch
        {
            "missing" => "{\"name\":\"private-marker\",\"expectedVersion\":1,\"reason\":\"test\"}",
            "immutable" => "{\"name\":\"private-marker\",\"isActive\":false,\"expectedVersion\":1,\"reason\":\"test\",\"code\":\"MOVED\"}",
            _ => "{private-marker"
        }, System.Text.Encoding.UTF8, "application/json");
        using var response = await d.Client.SendAsync(request);
        Assert.Equal(auditFailure ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await db.Set<Workspace>().SingleAsync(x => x.Id == f.Workspace.WorkspaceId)).IsActive);
        if (auditFailure) Assert.Equal(count, await db.AuditEvents.CountAsync());
        else
        {
            var audit = await db.AuditEvents.SingleAsync(x => x.EventType == "organization.access.denied");
            Assert.Equal(actor, audit.ActorId);
            var metadata = await db.AuditMetadata.Where(x => x.AuditEventId == audit.Id).ToArrayAsync();
            Assert.Contains(metadata, x => x.Key == "scope-validation" && x.Value == "requested-unverified");
            Assert.DoesNotContain(metadata, x => x.Value.Contains("private-marker"));
        }
    }

    [Theory]
    [InlineData(64, 200, 201)]
    [InlineData(65, 200, 400)]
    [InlineData(64, 201, 400)]
    public async Task Create_enforces_code_and_name_length_boundaries(int codeLength, int nameLength, int status)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await RoleAuthorizationTests.AdminAsync(d);
        using var response = await d.PostAsync(Root, new { code = new string('A', codeLength), name = new string('ก', nameLength) });
        Assert.Equal(status, (int)response.StatusCode);
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("\ninvalid", 1)]
    [InlineData("valid", 0)]
    public async Task Invalid_update_reason_or_version_does_not_deactivate(string reason, long version)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        using var response = await SendAsync(d, HttpMethod.Patch, Root + "/" + f.Workspace.WorkspaceId,
            new { name = "valid", isActive = false, expectedVersion = version, reason });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = d.Database.CreateContext();
        Assert.True((await db.Set<Workspace>().SingleAsync(x => x.Id == f.Workspace.WorkspaceId)).IsActive);
    }

    [Fact]
    public async Task Identical_update_is_noop_without_new_version_or_success_audit()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        using var response = await SendAsync(d, HttpMethod.Patch, Root + "/" + f.Workspace.WorkspaceId,
            new { name = "พื้นที่หนึ่ง", isActive = true, expectedVersion = 1, reason = "same" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = d.Database.CreateContext();
        var row = await db.Set<Workspace>().SingleAsync(x => x.Id == f.Workspace.WorkspaceId);
        Assert.Equal(1, row.Version); Assert.Null(row.UpdatedAtUtc);
        Assert.False(await db.AuditEvents.AnyAsync(x => x.EventType == "organization.updated"));
    }

    [Theory]
    [InlineData("anonymous", 401)]
    [InlineData("staff", 403)]
    [InlineData("no-mfa", 403)]
    [InlineData("expired-mfa", 403)]
    [InlineData("scoped-only", 403)]
    public async Task Every_organization_route_rejects_insufficient_authority_before_binding(string scenario, int status)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        if (scenario == "scoped-only")
        {
            var f = await ScopeFixture.CreateAsync(d);
            await f.GrantAsync(f.Site, "staff", "organization:manage");
            using var login = await d.LoginAsync("scope-user", Password);
            await ConfirmAsync(d);
        }
        else if (scenario != "anonymous")
        {
            await d.SeedUserAsync("actor", Password, scenario == "staff" ? [] : ["organization:manage"]);
            using var login = await d.LoginAsync("actor", Password);
            if (scenario == "expired-mfa") { await ConfirmAsync(d); d.Advance(TimeSpan.FromMinutes(15)); }
        }
        // Rejections do not rotate the session. Reuse its valid CSRF token so
        // this authorization matrix does not exhaust the issuer's IP budget.
        var csrf = await IdentityTestDriver.TokenAsync(d.Client);
        foreach (var row in IdentityOpenApiTests.Contracts.Where(row => ((string)row[0]).StartsWith("/api/v1/organization/", StringComparison.Ordinal)))
        {
            var path = ((string)row[0]).Replace("{workspaceId}", "10000000-0000-0000-0000-000000000001")
                .Replace("{projectId}", "10000000-0000-0000-0000-000000000002").Replace("{id}", "10000000-0000-0000-0000-000000000003");
            var method = new HttpMethod(((string)row[1]).ToUpperInvariant());
            using var request = IdentityTestDriver.Mutation(csrf, path);
            request.Method = method;
            request.Content = method == HttpMethod.Get ? null : JsonContent.Create(new { });
            using var response = await d.Client.SendAsync(request);
            Assert.Equal(status, (int)response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
    }

    [Fact]
    public async Task Unsafe_routes_reject_missing_csrf_before_changing_organization()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Patch })
        {
            using var request = new HttpRequestMessage(method, method == HttpMethod.Post ? Root : Root + "/" + f.Workspace.WorkspaceId);
            request.Headers.Add("Origin", "https://localhost:4443");
            request.Content = JsonContent.Create(new { code = "NO", name = "ห้าม", isActive = false, expectedVersion = 1, reason = "test" });
            Assert.Equal(HttpStatusCode.Forbidden, (await d.Client.SendAsync(request)).StatusCode);
        }
        await using var db = d.Database.CreateContext();
        Assert.Equal(2, await db.Set<Workspace>().CountAsync());
        Assert.True((await db.Set<Workspace>().SingleAsync(x => x.Id == f.Workspace.WorkspaceId)).IsActive);
    }

    [Theory]
    [InlineData("department")]
    [InlineData("project")]
    [InlineData("site")]
    public async Task Child_code_uniqueness_is_case_normalized_within_parent_only(string kind)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        var path = ChildPath(kind, f.Workspace.WorkspaceId, f.Project.ProjectId!.Value);
        var other = ChildPath(kind, f.OtherSite.WorkspaceId, f.OtherSite.ProjectId!.Value);
        Assert.Equal(HttpStatusCode.Created, (await d.PostAsync(path, new { code = "SAME", name = "หนึ่ง" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await d.PostAsync(path, new { code = " same ", name = "ซ้ำ" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await d.PostAsync(other, new { code = "same", name = "คนละพื้นที่" })).StatusCode);
    }

    [Fact]
    public async Task Duplicate_code_and_stale_version_are_conflicts_without_business_changes()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        using var duplicate = await d.PostAsync(Root, new { code = " w1 ", name = "duplicate" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var stale = await SendAsync(d, HttpMethod.Patch, Root + "/" + f.Workspace.WorkspaceId, new { name = "ห้ามเปลี่ยน", isActive = false, expectedVersion = 2, reason = "test" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        await using var db = d.Database.CreateContext();
        var w = await db.Set<Workspace>().SingleAsync(x => x.Id == f.Workspace.WorkspaceId);
        Assert.Equal("พื้นที่หนึ่ง", w.Name); Assert.True(w.IsActive); Assert.Equal(1, w.Version);
        Assert.Equal(2, await db.Set<Workspace>().CountAsync());
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("project")]
    public async Task Inactive_ancestor_rejects_child_create(string kind)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        await using var db = d.Database.CreateContext();
        if (kind == "workspace") (await db.Set<Workspace>().SingleAsync(x => x.Id == f.Workspace.WorkspaceId)).IsActive = false;
        else (await db.Set<Project>().SingleAsync(x => x.Id == f.Project.ProjectId)).IsActive = false;
        await db.SaveChangesAsync();
        using var response = await d.PostAsync(ChildPath("site", f.Workspace.WorkspaceId, f.Project.ProjectId!.Value), new { code = "NEW", name = "ห้ามสร้าง" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(3, await db.Set<Site>().CountAsync());
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", 400)]
    [InlineData("not-a-uuid", 400)]
    [InlineData("10000000-0000-0000-0000-000000000999", 404)]
    public async Task Invalid_or_missing_parent_does_not_expose_data(string id, int status)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await RoleAuthorizationTests.AdminAsync(d);
        Assert.Equal(status, (int)(await d.Client.GetAsync(Root + "/" + id + "/projects")).StatusCode);
    }

    [Fact]
    public async Task Wrong_workspace_project_tuple_is_404_for_create_and_list()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        var path = ChildPath("site", f.OtherSite.WorkspaceId, f.Project.ProjectId!.Value);
        Assert.Equal(HttpStatusCode.NotFound, (await d.Client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await d.PostAsync(path, new { code = "NO", name = "ห้าม" })).StatusCode);
    }

    [Theory]
    [InlineData("{\"name\":\"x\",\"expectedVersion\":1,\"reason\":\"test\"}")]
    [InlineData("{\"name\":\"x\",\"isActive\":false,\"expectedVersion\":1,\"reason\":\"test\",\"code\":\"MOVED\"}")]
    [InlineData("{\"name\":\"x\",\"isActive\":false,\"expectedVersion\":1,\"reason\":\"test\",\"workspaceId\":\"10000000-0000-0000-0000-000000000001\"}")]
    public async Task Patch_requires_full_shape_and_rejects_immutable_fields(string json)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        using var request = IdentityTestDriver.Mutation(await IdentityTestDriver.TokenAsync(d.Client), Root + "/" + f.Workspace.WorkspaceId);
        request.Method = HttpMethod.Patch;
        request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await d.Client.SendAsync(request)).StatusCode);
        await using var db = d.Database.CreateContext();
        Assert.True((await db.Set<Workspace>().SingleAsync(x => x.Id == f.Workspace.WorkspaceId)).IsActive);
    }

    [Fact]
    public async Task Management_creates_normalized_workspace_without_business_assignment()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        using var response = await d.PostAsync(Root, new { code = " west ", name = " ฝ่ายตะวันตก " });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = json.RootElement;
        Assert.Equal("WEST", item.GetProperty("code").GetString());
        Assert.Equal("ฝ่ายตะวันตก", item.GetProperty("name").GetString());
        Assert.Equal(item.GetProperty("id").GetGuid(), item.GetProperty("workspaceId").GetGuid());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("projectId").ValueKind);
        Assert.Equal(1, item.GetProperty("version").GetInt64());
        Assert.True(item.GetProperty("isActive").GetBoolean());
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(response.Headers.Contains("X-Correlation-ID"));
        await using var db = d.Database.CreateContext();
        Assert.Empty(await db.Set<ScopeAssignment>().ToArrayAsync());
        Assert.Equal(actor, (await db.Set<Workspace>().SingleAsync()).CreatedBy);
    }

    [Theory]
    [InlineData("department")]
    [InlineData("project")]
    [InlineData("site")]
    public async Task Child_create_list_and_patch_are_bound_to_route_parent(string kind)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        var path = ChildPath(kind, f.Workspace.WorkspaceId, f.Project.ProjectId!.Value);
        var wrong = ChildPath(kind, f.OtherSite.WorkspaceId, f.OtherSite.ProjectId!.Value);
        using var created = await d.PostAsync(path, new { code = "child", name = "หน่วยทดสอบ" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(f.Workspace.WorkspaceId, body.RootElement.GetProperty("workspaceId").GetGuid());
        Assert.Equal(kind == "site" ? f.Project.ProjectId : null, body.RootElement.GetProperty("projectId").Deserialize<Guid?>());
        using var denied = await SendAsync(d, HttpMethod.Patch, wrong + "/" + id, new { name = "ไม่ควรเปลี่ยน", isActive = false, expectedVersion = 1, reason = "test" });
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        using var updated = await SendAsync(d, HttpMethod.Patch, path + "/" + id, new { name = "ชื่อใหม่", isActive = true, expectedVersion = 1, reason = "rename" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var changed = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
        Assert.Equal(2, changed.RootElement.GetProperty("version").GetInt64());
        Assert.Equal("CHILD", changed.RootElement.GetProperty("code").GetString());
        using var list = await d.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listing = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.Contains(listing.RootElement.GetProperty("items").EnumerateArray(), x => x.GetProperty("id").GetGuid() == id);
    }

    [Theory]
    [InlineData("bad code", "valid")]
    [InlineData("ภาษาไทย", "valid")]
    [InlineData("\ncode", "valid")]
    [InlineData("code", "\tname")]
    [InlineData("", "valid")]
    [InlineData("code", " ")]
    public async Task Invalid_code_or_name_is_400_without_create(string code, string name)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await RoleAuthorizationTests.AdminAsync(d);
        using var response = await d.PostAsync(Root, new { code, name });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = d.Database.CreateContext();
        Assert.Empty(await db.Set<Workspace>().ToArrayAsync());
    }

    [Theory]
    [InlineData("", 25, 1, 25)]
    [InlineData("?pageSize=999", 100, 1, 100)]
    [InlineData("?page=2&pageSize=10", 10, 2, 10)]
    public async Task Pagination_is_bounded_and_stably_ordered(string query, int count, int page, int pageSize)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        await using var db = d.Database.CreateContext();
        for (var i = 0; i < 105; i++) db.Add(new Workspace { Id = Guid.NewGuid(), Code = $"W{i:D3}", Name = "พื้นที่", CreatedBy = actor, CreatedAtUtc = d.Clock.GetUtcNow() });
        await db.SaveChangesAsync();
        using var response = await d.Client.GetAsync(Root + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(count, body.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(105, body.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(page, body.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(pageSize, body.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Equal(page == 2 ? "W010" : "W000", body.RootElement.GetProperty("items")[0].GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("?page=0")]
    [InlineData("?pageSize=0")]
    [InlineData("?page=2147483647&pageSize=100")]
    public async Task Invalid_pagination_is_400(string query)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await RoleAuthorizationTests.AdminAsync(d);
        Assert.Equal(HttpStatusCode.BadRequest, (await d.Client.GetAsync(Root + query)).StatusCode);
    }

    [Theory]
    [InlineData("anonymous", 401)]
    [InlineData("staff", 403)]
    [InlineData("missing-mfa", 403)]
    public async Task Management_requires_system_capability_and_mfa(string actor, int status)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        if (actor != "anonymous")
        {
            await d.SeedUserAsync("actor", Password, actor == "staff" ? [] : ["organization:manage"]);
            using var login = await d.LoginAsync("actor", Password);
        }
        Assert.Equal(status, (int)(await d.Client.GetAsync(Root)).StatusCode);
        Assert.Equal(status, (int)(await d.PostAsync(Root, new { code = "NO", name = "ห้าม" })).StatusCode);
    }

    internal static string ChildPath(string kind, Guid workspace, Guid project) => Root + "/" + workspace
        + (kind == "site" ? "/projects/" + project + "/sites" : "/" + kind + "s");
}
