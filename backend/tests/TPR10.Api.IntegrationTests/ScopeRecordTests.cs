using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Scopes;
using TPR10.Api.Scopes.Data;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class ScopeRecordTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Detail_cannot_load_foreign_record_by_known_id()
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var f = await ScopeFixture.CreateAsync(d);
        await f.GrantAsync(f.Site, "staff", "scope-probe:read");
        var foreign = await f.RecordAsync(f.OtherSite, "ข้อความข้ามพื้นที่", "ห้ามรั่ว");
        await d.LoginAsync("scope-user", MfaTests.Password);
        using var r = await d.Client.GetAsync(f.Path(f.Site) + "/" + foreign);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.DoesNotContain("ห้ามรั่ว", await r.Content.ReadAsStringAsync());
        var own = await f.RecordAsync(f.Site, "ของตนเอง");
        Assert.Equal(HttpStatusCode.OK, (await d.Client.GetAsync(f.Path(f.Site) + "/" + own)).StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Every_level_lists_counts_and_finds_only_its_exact_nullable_tuple(int level)
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var f = await ScopeFixture.CreateAsync(d);
        ScopeKey[] keys = [f.Workspace, f.Project, f.Site, f.SiblingSite, f.OtherSite];
        var target = keys[level];
        await f.GrantAsync(target, "staff", "scope-probe:read");
        var ids = new List<Guid>();
        foreach (var key in keys) ids.Add(await f.RecordAsync(key, "ข้อความ", "secret-marker"));
        var second = await f.RecordAsync(target, "สอง");
        await d.LoginAsync("scope-user", MfaTests.Password);
        var result = await d.Client.GetFromJsonAsync<JsonElement>(f.Path(target));
        Assert.Equal(2, result.GetProperty("total").GetInt32());
        Assert.Equal(25, result.GetProperty("pageSize").GetInt32());
        var items = result.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        Assert.All(items, x => Assert.False(x.TryGetProperty("restrictedNote", out _)));
        var page1 = await d.Client.GetFromJsonAsync<JsonElement>(f.Path(target) + "?pageSize=1");
        var page2 = await d.Client.GetFromJsonAsync<JsonElement>(f.Path(target) + "?pageSize=1&page=2");
        Assert.Equal(items[0].GetProperty("id").GetGuid(), Assert.Single(page1.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(items[1].GetProperty("id").GetGuid(), Assert.Single(page2.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Contains(second, items.Select(x => x.GetProperty("id").GetGuid()));
        foreach (var id in ids)
            Assert.Equal(id == ids[level] ? HttpStatusCode.OK : HttpStatusCode.NotFound,
                (await d.Client.GetAsync(f.Path(target) + "/" + id)).StatusCode);
        foreach (var key in keys.Where(x => x != target))
            Assert.Equal(HttpStatusCode.NotFound, (await d.Client.GetAsync(f.Path(key))).StatusCode);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task Create_update_use_server_scope_actor_and_version_without_leaking_write_only_data(int level, bool read)
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var f = await ScopeFixture.CreateAsync(d);
        var key = new[] { f.Workspace, f.Project, f.Site }[level];
        await f.GrantAsync(key, "staff", read ? ["scope-probe:write", "scope-probe:read"] : ["scope-probe:write"]);
        await d.LoginAsync("scope-user", MfaTests.Password);
        using var created = await d.PostAsync(f.Path(key), new { note = "บันทึก" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal(f.Path(key) + "/" + id, created.Headers.Location!.ToString());
        Assert.Equal(read, body.TryGetProperty("note", out _));
        Assert.False(body.TryGetProperty("restrictedNote", out _));
        Assert.Equal(1, body.GetProperty("version").GetInt64());
        using var updated = await AuthorizationTests.SendAsync(d, HttpMethod.Patch, f.Path(key) + "/" + id, new { note = "แก้ไข", expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var update = await updated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, update.GetProperty("version").GetInt64());
        Assert.Equal(read, update.TryGetProperty("note", out _));
        Assert.NotNull(updated.Headers.Location);
        Assert.Equal(HttpStatusCode.Conflict, (await AuthorizationTests.SendAsync(d, HttpMethod.Patch, f.Path(key) + "/" + id, new { note = "stale", expectedVersion = 1 })).StatusCode);
        await using var db = d.Database.CreateContext();
        var row = await db.Set<ScopeProbeRecord>().SingleAsync(x => x.Id == id);
        Assert.Equal(key, new ScopeKey(row.WorkspaceId, row.ProjectId, row.SiteId));
        Assert.Equal(f.UserId, row.CreatedBy); Assert.Equal(f.UserId, row.UpdatedBy);
        Assert.Equal("แก้ไข", row.Note); Assert.Equal(2, row.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restricted_field_presence_requires_permission_and_recent_mfa_for_all_responses(bool restricted)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var f = await ScopeFixture.CreateAsync(d);
        await f.GrantAsync(f.Site, "staff", restricted ? ["scope-probe:read", "scope-probe:write", "scope-probe:restricted-read"] : ["scope-probe:read", "scope-probe:write"]);
        await d.LoginAsync("scope-user", MfaTests.Password);
        await AuthorizationTests.ConfirmAsync(d);
        var id = await f.RecordAsync(f.Site, "public", "secret-marker");
        var detail = await d.Client.GetFromJsonAsync<JsonElement>(f.Path(f.Site) + "/" + id);
        Assert.Equal(restricted, detail.TryGetProperty("restrictedNote", out _));
        var list = await d.Client.GetFromJsonAsync<JsonElement>(f.Path(f.Site));
        Assert.Equal(restricted, Assert.Single(list.GetProperty("items").EnumerateArray()).TryGetProperty("restrictedNote", out _));
        using var absent = await AuthorizationTests.SendAsync(d, HttpMethod.Patch, f.Path(f.Site) + "/" + id, new { note = "changed", expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.OK, absent.StatusCode);
        await using var db = d.Database.CreateContext();
        Assert.Equal("secret-marker", (await db.Set<ScopeProbeRecord>().AsNoTracking().SingleAsync(x => x.Id == id)).RestrictedNote);
        foreach (var value in new object?[] { null, "replacement" })
        {
            var version = (await db.Set<ScopeProbeRecord>().AsNoTracking().SingleAsync(x => x.Id == id)).Version;
            using var response = await AuthorizationTests.SendAsync(d, HttpMethod.Patch, f.Path(f.Site) + "/" + id, new { note = "changed", restrictedNote = value, expectedVersion = version });
            Assert.Equal(restricted ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
            using var create = await d.PostAsync(f.Path(f.Site), new { note = "new", restrictedNote = value });
            Assert.Equal(restricted ? HttpStatusCode.Created : HttpStatusCode.Forbidden, create.StatusCode);
            if (restricted)
            {
                Assert.True((await create.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("restrictedNote", out _));
                Assert.Equal(value, (await db.Set<ScopeProbeRecord>().AsNoTracking().SingleAsync(x => x.Id == id)).RestrictedNote);
            }
        }
    }

}
