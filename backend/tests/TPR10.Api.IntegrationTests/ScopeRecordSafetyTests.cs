using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Identity.Data;
using TPR10.Api.Scopes.Data;
using TPR10.Api.Organization.Data;
using TPR10.Api.Scopes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class ScopeRecordSafetyTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("{\"note\":\"changed\",\"expectedVersion\":1,\"actorId\":\"00000000-0000-0000-0000-000000000001\"}")]
    [InlineData("{\"note\":\"changed\",\"expectedVersion\":1,\"restrictedNote\":42}")]
    [InlineData("{\"note\":\"changed\"}")]
    [InlineData("{\"note\":null,\"expectedVersion\":1}")]
    [InlineData("{\"note\":\"changed\",\"expectedVersion\":0}")]
    public async Task Invalid_patch_preserves_public_restricted_and_version(string json)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var f = await ScopeFixture.CreateAsync(d); await f.GrantAsync(f.Site, "staff", "scope-probe:write", "scope-probe:restricted-read");
        var id = await f.RecordAsync(f.Site, "original", "restricted");
        await d.LoginAsync("scope-user", MfaTests.Password); await AuthorizationTests.ConfirmAsync(d);
        using var request = IdentityTestDriver.Mutation(await IdentityTestDriver.TokenAsync(d.Client), f.Path(f.Site) + "/" + id);
        request.Method = HttpMethod.Patch; request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await d.Client.SendAsync(request)).StatusCode);
        await using var db = d.Database.CreateContext();
        var row = await db.Set<ScopeProbeRecord>().SingleAsync();
        Assert.Equal("original", row.Note); Assert.Equal("restricted", row.RestrictedNote); Assert.Equal(1, row.Version);
        Assert.Equal(1, await db.AuditEvents.CountAsync(x => x.EventType == "scope.access.denied"));
    }

    [Theory]
    [InlineData(0, 201)]
    [InlineData(500, 201)]
    [InlineData(501, 400)]
    public async Task Note_length_boundary_and_noop_version(int length, int expected)
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var f = await ScopeFixture.CreateAsync(d); await f.GrantAsync(f.Site, "staff", "scope-probe:write");
        await d.LoginAsync("scope-user", MfaTests.Password);
        var note = new string('ก', length);
        using var response = await d.PostAsync(f.Path(f.Site), new { note });
        Assert.Equal(expected, (int)response.StatusCode);
        if (expected != 201) return;
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var update = await AuthorizationTests.SendAsync(d, HttpMethod.Patch, f.Path(f.Site) + "/" + id, new { note, expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(1, (await update.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64());
        await using var db = d.Database.CreateContext();
        Assert.Null((await db.Set<ScopeProbeRecord>().SingleAsync()).UpdatedAtUtc);
    }

    [Fact]
    public async Task Cross_scope_patch_and_new_site_never_inherit_parent_or_header_scope()
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var f = await ScopeFixture.CreateAsync(d); await f.GrantAsync(f.Project, "staff", "scope-probe:read", "scope-probe:write");
        await f.GrantAsync(f.Site, "staff", "scope-probe:read", "scope-probe:write");
        var foreign = await f.RecordAsync(f.OtherSite, "foreign", "secret-marker");
        var newSite = new ScopeKey(f.Project.WorkspaceId, f.Project.ProjectId, Guid.NewGuid());
        await using var db = d.Database.CreateContext();
        db.Add(new Site { Id = newSite.SiteId!.Value, WorkspaceId = newSite.WorkspaceId, ProjectId = newSite.ProjectId!.Value, Code = "NEW", Name = "ใหม่", CreatedBy = f.UserId, CreatedAtUtc = d.Clock.GetUtcNow() });
        await db.SaveChangesAsync();
        await d.LoginAsync("scope-user", MfaTests.Password);
        d.Client.DefaultRequestHeaders.Add("X-Workspace-Id", f.OtherSite.WorkspaceId.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await d.Client.GetAsync(f.Path(newSite))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AuthorizationTests.SendAsync(d, HttpMethod.Patch, f.Path(f.Site) + "/" + foreign, new { note = "changed", expectedVersion = 1 })).StatusCode);
        var missing = await d.Client.GetAsync(f.Path(f.Site) + "/" + Guid.NewGuid());
        var wrong = await d.Client.GetAsync(f.Path(f.Site) + "/" + foreign);
        Assert.Equal(missing.StatusCode, wrong.StatusCode);
        Assert.Equal((await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString(), (await wrong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString());
        Assert.Equal("foreign", (await db.Set<ScopeProbeRecord>().SingleAsync()).Note);
    }

    [Fact]
    public async Task Restricted_capability_without_recent_mfa_does_not_expose_field()
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var f = await ScopeFixture.CreateAsync(d); await f.GrantAsync(f.Site, "staff", "scope-probe:read", "scope-probe:restricted-read");
        var id = await f.RecordAsync(f.Site, "public", "restricted");
        await d.LoginAsync("scope-user", MfaTests.Password);
        var body = await d.Client.GetFromJsonAsync<JsonElement>(f.Path(f.Site) + "/" + id);
        Assert.False(body.TryGetProperty("restrictedNote", out _));
    }

    [Fact]
    public async Task Production_does_not_map_or_document_record_probes()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var f = await ScopeFixture.CreateAsync(d);
        await using var factory = new ApiFactory(d.Database.ConnectionString, "Production", d.Clock, keys.Settings);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443") });
        foreach (var key in new[] { f.Workspace, f.Project, f.Site })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(f.Path(key))).StatusCode);
        Assert.DoesNotContain("scope-probe-records", await client.GetStringAsync("/api/openapi/v1.json"));
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=-1")]
    [InlineData("pageSize=0")]
    [InlineData("page=2147483647&pageSize=100")]
    [InlineData("page=bad")]
    public async Task Invalid_pagination_is_audited_and_valid_page_is_capped(string query)
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var f = await ScopeFixture.CreateAsync(d); await f.GrantAsync(f.Site, "staff", "scope-probe:read");
        await d.LoginAsync("scope-user", MfaTests.Password);
        using var response = await d.Client.GetAsync(f.Path(f.Site) + "?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(response.Headers.GetValues("X-Correlation-ID").Single(), problem.GetProperty("correlationId").GetString());
        await using var db = d.Database.CreateContext();
        Assert.Equal(1, await db.AuditEvents.CountAsync(x => x.EventType == "scope.access.denied"));
        var page = await d.Client.GetFromJsonAsync<JsonElement>(f.Path(f.Site) + "?pageSize=1000");
        Assert.Equal(100, page.GetProperty("pageSize").GetInt32());
    }

    [Theory]
    [InlineData("{\"note\":\"ok\",\"actorId\":\"00000000-0000-0000-0000-000000000001\"}")]
    [InlineData("{\"note\":\"ok\",\"workspaceId\":\"00000000-0000-0000-0000-000000000001\"}")]
    [InlineData("{\"note\":\"ok\",\"restrictedNote\":42}")]
    [InlineData("{\"note\":null}")]
    [InlineData("{}")]
    [InlineData("{\"note\":\"control\\ntext\"}")]
    [InlineData("{")]
    public async Task Invalid_or_forged_body_is_audited_without_inserting(string json)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var f = await ScopeFixture.CreateAsync(d); await f.GrantAsync(f.Site, "staff", "scope-probe:write", "scope-probe:restricted-read");
        await d.LoginAsync("scope-user", MfaTests.Password); await AuthorizationTests.ConfirmAsync(d);
        using var request = IdentityTestDriver.Mutation(await IdentityTestDriver.TokenAsync(d.Client), f.Path(f.Site));
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await d.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = d.Database.CreateContext();
        Assert.Empty(await db.Set<ScopeProbeRecord>().ToArrayAsync());
        Assert.Equal(1, await db.AuditEvents.CountAsync(x => x.EventType == "scope.access.denied"));
    }

    [Theory]
    [InlineData("record")]
    [InlineData("workspace")]
    [InlineData("project")]
    [InlineData("site")]
    public async Task Malformed_uuid_is_400_with_audit(string component)
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var f = await ScopeFixture.CreateAsync(d); await f.GrantAsync(f.Site, "staff", "scope-probe:read");
        await d.LoginAsync("scope-user", MfaTests.Password);
        var path = f.Path(f.Site) + "/not-a-uuid";
        if (component != "record") path = f.Path(f.Site).Replace((component == "workspace" ? f.Site.WorkspaceId : component == "project" ? f.Site.ProjectId!.Value : f.Site.SiteId!.Value).ToString(), "invalid");
        Assert.Equal(HttpStatusCode.BadRequest, (await d.Client.GetAsync(path)).StatusCode);
        await using var db = d.Database.CreateContext();
        Assert.Equal(1, await db.AuditEvents.CountAsync(x => x.EventType == "scope.access.denied"));
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("restricted-without-mfa")]
    [InlineData("global-admin")]
    public async Task Capabilities_never_imply_other_operations_or_scopes(string mode)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        if (mode == "global-admin") await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        if (mode != "global-admin")
        {
            await f.GrantAsync(f.Site, "staff", mode == "restricted-without-mfa" ? ["scope-probe:write", "scope-probe:restricted-read"] : ["scope-probe:" + mode]);
            await d.LoginAsync("scope-user", MfaTests.Password);
        }
        using var response = mode == "write" || mode == "global-admin" ? await d.Client.GetAsync(f.Path(f.Site))
            : await d.PostAsync(f.Path(f.Site), mode == "read" ? new { note = "no" } : (object)new { note = "no", restrictedNote = (string?)null });
        Assert.Equal(mode == "global-admin" ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden, response.StatusCode);
        await using var db = d.Database.CreateContext();
        Assert.Empty(await db.Set<ScopeProbeRecord>().ToArrayAsync());
    }

    [Theory]
    [InlineData("list")]
    [InlineData("detail")]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("binding")]
    public async Task Audit_failure_returns_503_without_payload_or_record_version_session_changes(string operation)
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var f = await ScopeFixture.CreateAsync(d); await f.GrantAsync(f.Site, "staff", "scope-probe:read", "scope-probe:write");
        var id = await f.RecordAsync(f.Site, "original", "secret-marker");
        await d.LoginAsync("scope-user", MfaTests.Password);
        await using var db = d.Database.CreateContext();
        var version = await db.Set<IdentityUser>().Where(x => x.Id == f.UserId).Select(x => x.SecurityVersion).SingleAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION reject_probe_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.event_type LIKE 'scope.%' THEN RAISE EXCEPTION 'private-db-error'; END IF; RETURN NEW; END $$; CREATE TRIGGER reject_probe_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION reject_probe_audit();");
        using var response = operation switch
        {
            "create" => await d.PostAsync(f.Path(f.Site), new { note = "new" }),
            "update" => await AuthorizationTests.SendAsync(d, HttpMethod.Patch, f.Path(f.Site) + "/" + id, new { note = "changed", expectedVersion = 1 }),
            "detail" => await d.Client.GetAsync(f.Path(f.Site) + "/" + id),
            "binding" => await d.Client.GetAsync(f.Path(f.Site) + "/invalid"),
            _ => await d.Client.GetAsync(f.Path(f.Site))
        };
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret-marker", body); Assert.DoesNotContain("private-db-error", body); Assert.DoesNotContain("original", body);
        var row = Assert.Single(await db.Set<ScopeProbeRecord>().ToArrayAsync());
        Assert.Equal("original", row.Note); Assert.Equal(1, row.Version);
        Assert.Equal(version, await db.Set<IdentityUser>().Where(x => x.Id == f.UserId).Select(x => x.SecurityVersion).SingleAsync());
        Assert.False(await db.AuditEvents.AnyAsync(x => x.EventType.StartsWith("scope.")));
    }

    [Fact]
    public async Task Success_audit_has_exact_actor_role_scope_row_count_without_note_content()
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var f = await ScopeFixture.CreateAsync(d); var assignment = await f.GrantAsync(f.Site, "staff", "scope-probe:read", "scope-probe:write");
        await d.LoginAsync("scope-user", MfaTests.Password);
        using var create = await d.PostAsync(f.Path(f.Site), new { note = "private-body-marker" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await d.Client.GetAsync(f.Path(f.Site))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await d.Client.GetAsync(f.Path(f.Site) + "/" + id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AuthorizationTests.SendAsync(d, HttpMethod.Patch, f.Path(f.Site) + "/" + id, new { note = "changed", expectedVersion = 1 })).StatusCode);
        await using var db = d.Database.CreateContext();
        var role = (await db.Set<ScopeAssignment>().SingleAsync(x => x.Id == assignment)).RoleId;
        var audits = await db.AuditEvents.Where(x => x.EventType.StartsWith("scope.record.")).ToArrayAsync();
        Assert.Equal(4, audits.Length);
        Assert.Equal(new[] { "scope.record.create", "scope.record.detail", "scope.record.list", "scope.record.update" }, audits.Select(x => x.EventType).Order());
        foreach (var audit in audits)
        {
            Assert.Equal(f.UserId, audit.ActorId); Assert.Equal(role, audit.ActingRoleId);
            Assert.Equal(f.Site.WorkspaceId, audit.WorkspaceId); Assert.Equal(f.Site.ProjectId, audit.ProjectId); Assert.Equal(f.Site.SiteId, audit.SiteId);
            Assert.Equal("1", (await db.AuditMetadata.SingleAsync(x => x.AuditEventId == audit.Id && x.Key == "row-count")).Value);
        }
        Assert.DoesNotContain(await db.AuditMetadata.Select(x => x.Value).ToArrayAsync(), x => x.Contains("private-body-marker"));
    }
}
