using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Identity.Data;
using TPR10.Api.Scopes;
using TPR10.Api.Scopes.Data;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class ScopeExportTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(0, 200)]
    [InlineData(100, 200)]
    [InlineData(101, 400)]
    public async Task Export_is_bounded_without_silent_truncation(int count, int expected)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var f = await ScopeFixture.CreateAsync(d); await f.GrantAsync(f.Site, "approval", "scope-probe:export");
        await SeedAsync(f, f.Site, count, d.Clock.GetUtcNow());
        await d.LoginAsync("scope-user", MfaTests.Password); await AuthorizationTests.ConfirmAsync(d);
        using var response = await d.PostAsync(f.Path(f.Site) + "/export-simulation", new { });
        Assert.Equal(expected, (int)response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (expected == 200)
        {
            Assert.Equal(count, body.GetProperty("rowCount").GetInt32());
            Assert.Equal(count, body.GetProperty("items").GetArrayLength());
            Assert.All(body.GetProperty("items").EnumerateArray(), x => Assert.False(x.TryGetProperty("restrictedNote", out _)));
        }
        else
        {
            Assert.False(body.TryGetProperty("items", out _));
            await using var db = d.Database.CreateContext();
            Assert.Equal(1, await db.AuditEvents.CountAsync(x => x.EventType == "scope.access.denied"));
            // Positive control: the same authorized endpoint works with a narrower filter.
            using var smaller = await d.PostAsync(f.Path(f.Site) + "/export-simulation", new { createdTo = d.Clock.GetUtcNow().AddSeconds(100) });
            Assert.Equal(HttpStatusCode.OK, smaller.StatusCode);
            Assert.Equal(100, (await smaller.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rowCount").GetInt32());
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Export_filters_exact_nullable_tuple_before_cap_and_uses_inclusive_exclusive_utc(int level)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var f = await ScopeFixture.CreateAsync(d);
        ScopeKey[] scopes = [f.Workspace, f.Project, f.Site, f.SiblingSite, f.OtherSite]; var key = scopes[level];
        await f.GrantAsync(key, "staff", "scope-probe:export");
        foreach (var other in scopes.Where(x => x != key)) await SeedAsync(f, other, 101, d.Clock.GetUtcNow());
        var start = d.Clock.GetUtcNow(); await SeedAsync(f, key, 3, start);
        await d.LoginAsync("scope-user", MfaTests.Password); await AuthorizationTests.ConfirmAsync(d);
        using var all = await d.PostAsync(f.Path(key) + "/export-simulation", new { });
        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
        var page = await all.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, page.GetProperty("rowCount").GetInt32());
        using var filtered = await d.PostAsync(f.Path(key) + "/export-simulation", new { createdFrom = start.AddSeconds(1), createdTo = start.AddSeconds(2) });
        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
        var item = Assert.Single((await filtered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray());
        Assert.Equal("public-1", item.GetProperty("note").GetString());
        using var future = await d.PostAsync(f.Path(key) + "/export-simulation", new { createdFrom = start.AddDays(1), createdTo = start.AddDays(2) });
        Assert.Equal(HttpStatusCode.OK, future.StatusCode);
        Assert.Equal(0, (await future.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rowCount").GetInt32());
        using var repeated = await d.PostAsync(f.Path(key) + "/export-simulation", new { });
        Assert.Equal(page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()),
            (await repeated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()));
    }

    [Theory]
    [InlineData("{\"createdFrom\":\"2026-09-24T00:00:00+07:00\"}")]
    [InlineData("{\"createdTo\":\"2026-09-24T00:00:00-01:00\"}")]
    [InlineData("{\"createdFrom\":\"2026-09-24T00:00:00Z\",\"createdTo\":\"2026-09-24T00:00:00Z\"}")]
    [InlineData("{\"createdFrom\":\"2026-09-25T00:00:00Z\",\"createdTo\":\"2026-09-24T00:00:00Z\"}")]
    [InlineData("{\"createdFrom\":\"bad\"}")]
    [InlineData("{\"createdFrom\":42}")]
    [InlineData("{\"workspaceId\":\"00000000-0000-0000-0000-000000000001\"}")]
    [InlineData("{\"note\":\"search-expression\"}")]
    [InlineData("{")]
    public async Task Invalid_filter_or_forged_body_has_durable_denial_without_records(string json)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var f = await ScopeFixture.CreateAsync(d); await f.GrantAsync(f.Site, "staff", "scope-probe:export");
        await f.RecordAsync(f.Site, "private-marker");
        await d.LoginAsync("scope-user", MfaTests.Password); await AuthorizationTests.ConfirmAsync(d);
        using var request = IdentityTestDriver.Mutation(await IdentityTestDriver.TokenAsync(d.Client), f.Path(f.Site) + "/export-simulation");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await d.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("private-marker", await response.Content.ReadAsStringAsync());
        await using var db = d.Database.CreateContext();
        Assert.Equal(1, await db.AuditEvents.CountAsync(x => x.EventType == "scope.access.denied"));
    }

    [Theory]
    [InlineData("no-mfa", 403)]
    [InlineData("no-export", 403)]
    [InlineData("no-assignment", 404)]
    [InlineData("unknown", 404)]
    [InlineData("anonymous", 401)]
    public async Task Export_requires_its_exact_assignment_capability_and_recent_mfa(string mode, int expected)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var f = await ScopeFixture.CreateAsync(d);
        if (mode != "no-assignment") await f.GrantAsync(f.Site, "staff", mode == "no-export" ? "scope-probe:read" : "scope-probe:export");
        if (mode != "anonymous") { await d.LoginAsync("scope-user", MfaTests.Password); if (mode != "no-mfa") await AuthorizationTests.ConfirmAsync(d); }
        var key = mode == "unknown" ? new ScopeKey(Guid.NewGuid()) : f.Site;
        using var response = await d.PostAsync(f.Path(key) + "/export-simulation", new { });
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.DoesNotContain("items", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Export_permission_does_not_need_read_but_restricted_visibility_is_separate(bool restricted)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var f = await ScopeFixture.CreateAsync(d);
        await f.GrantAsync(f.Site, "approval", restricted ? ["scope-probe:export", "scope-probe:restricted-read"] : ["scope-probe:export"]);
        await f.GrantAsync(f.SiblingSite, "staff", "scope-probe:read");
        await f.RecordAsync(f.Site, "public", "restricted-marker"); await f.RecordAsync(f.Site, "nullable", null);
        await d.LoginAsync("scope-user", MfaTests.Password); await AuthorizationTests.ConfirmAsync(d);
        Assert.Equal(HttpStatusCode.Forbidden, (await d.Client.GetAsync(f.Path(f.Site))).StatusCode);
        using var response = await d.PostAsync(f.Path(f.Site) + "/export-simulation", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.All(body.GetProperty("items").EnumerateArray(), x => Assert.Equal(restricted, x.TryGetProperty("restrictedNote", out _)));
        Assert.Equal(HttpStatusCode.Forbidden, (await d.PostAsync(f.Path(f.SiblingSite) + "/export-simulation", new { })).StatusCode);
    }

    [Fact]
    public async Task Fresh_login_after_role_change_preserves_exact_scope_and_current_field_visibility()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        var assignment = await f.GrantAsync(f.Site, "approval", "scope-probe:export", "scope-probe:restricted-read");
        await f.GrantAsync(f.SiblingSite, "staff", "scope-probe:read");
        await f.RecordAsync(f.Site, "public", "private-restricted");
        using var client = d.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443") });
        var secret = await ScopeRaceTests.EnrollAsync(client, d);
        using var before = await ScopeRaceTests.SendAsync(client, HttpMethod.Post, f.Path(f.Site) + "/export-simulation", new { });
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.Equal("private-restricted", Assert.Single((await before.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray()).GetProperty("restrictedNote").GetString());
        await using var db = d.Database.CreateContext();
        var role = (await db.Set<ScopeAssignment>().SingleAsync(x => x.Id == assignment)).RoleId;
        var export = (await db.Set<IdentityPermission>().SingleAsync(x => x.Capability == "scope-probe:export")).Id;
        foreach (var keepExport in new[] { true, false })
        {
            using var changed = await AuthorizationTests.SendAsync(d, HttpMethod.Put, $"/api/v1/roles/{role}/permissions", new { permissionIds = keepExport ? new[] { export } : Array.Empty<Guid>() });
            Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/session")).StatusCode);
            // Each re-login uses a fresh TOTP step and a fresh CSRF issuer window.
            d.Advance(TimeSpan.FromMinutes(1));
            using var login = await ScopeRaceTests.SendAsync(client, HttpMethod.Post, "/api/v1/auth/login", new { username = "scope-user", password = MfaTests.Password });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            using var mfa = await ScopeRaceTests.SendAsync(client, HttpMethod.Post, "/api/v1/auth/mfa/challenge", new { code = MfaTests.Code(secret, d.Clock.GetUtcNow()) });
            Assert.Equal(HttpStatusCode.OK, mfa.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(f.Path(f.SiblingSite))).StatusCode);
            using var response = await ScopeRaceTests.SendAsync(client, HttpMethod.Post, f.Path(f.Site) + "/export-simulation", new { });
            Assert.Equal(keepExport ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("restrictedNote", text); Assert.DoesNotContain("private-restricted", text);
            if (keepExport) Assert.Equal(1, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rowCount").GetInt32());
            using var sibling = await ScopeRaceTests.SendAsync(client, HttpMethod.Post, f.Path(f.SiblingSite) + "/export-simulation", new { });
            Assert.Equal(HttpStatusCode.Forbidden, sibling.StatusCode);
        }
    }

    [Fact]
    public async Task Production_does_not_map_or_document_exports()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var f = await ScopeFixture.CreateAsync(d);
        await using var factory = new ApiFactory(d.Database.ConnectionString, "Production", d.Clock, keys.Settings);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443") });
        var token = await IdentityTestDriver.TokenAsync(client);
        foreach (var key in new[] { f.Workspace, f.Project, f.Site })
        {
            using var request = IdentityTestDriver.Mutation(token, f.Path(key) + "/export-simulation");
            request.Content = JsonContent.Create(new { });
            Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(request)).StatusCode);
        }
        Assert.DoesNotContain("export-simulation", await client.GetStringAsync("/api/openapi/v1.json"));
    }

    internal static async Task SeedAsync(ScopeFixture f, ScopeKey key, int count, DateTimeOffset start)
    {
        await using var db = f.Driver.Database.CreateContext();
        for (var i = 0; i < count; i++) db.Add(new ScopeProbeRecord
        {
            Id = Guid.NewGuid(),
            WorkspaceId = key.WorkspaceId,
            ProjectId = key.ProjectId,
            SiteId = key.SiteId,
            Note = "public-" + i,
            RestrictedNote = "restricted-" + i,
            CreatedAtUtc = start.AddSeconds(i),
            CreatedBy = f.UserId
        });
        await db.SaveChangesAsync();
    }
}
