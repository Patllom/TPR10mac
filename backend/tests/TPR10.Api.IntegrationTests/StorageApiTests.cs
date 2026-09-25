using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Storage;
using static TPR10.Api.IntegrationTests.StorageTestFixture;

namespace TPR10.Api.IntegrationTests;

[Collection("database"), UnsupportedOSPlatform("windows")]
public sealed class StorageApiTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("anonymous", 401)]
    [InlineData("staff", 403)]
    [InlineData("admin", 403)]
    [InlineData("expired", 403)]
    public async Task All_storage_routes_require_explicit_permission_and_recent_mfa_before_body_validation(string actor, int status)
    {
        using var f = new StorageTestFixture();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, f.Settings);
        if (actor == "staff") { await d.SeedUserAsync("staff", MfaTests.Password, []); await d.LoginAsync("staff", MfaTests.Password); }
        if (actor == "admin") await RoleAuthorizationTests.AdminAsync(d);
        if (actor == "expired") { await OperatorAsync(d); d.Advance(TimeSpan.FromMinutes(15)); }
        foreach (var path in new[] { "/options", "/locations", "/write-target", "/health" })
        {
            using var response = await d.Client.GetAsync(ApiRoot + path);
            Assert.Equal(status, (int)response.StatusCode);
        }
        foreach (var path in new[] { "/locations", "/write-target", "/locations/00000000-0000-0000-0000-000000000099/probe" })
        {
            using var response = await d.PostAsync(ApiRoot + path, new { rootPath = "UNTRUSTED-ROOT" });
            Assert.Equal(status, (int)response.StatusCode);
        }
        await using var db = d.Database.CreateContext();
        Assert.Empty(await db.Set<StorageLocation>().ToArrayAsync());
    }

    [Fact]
    public async Task Registration_uses_configured_alias_without_exposing_paths_and_never_selects_target_implicitly()
    {
        using var f = new StorageTestFixture();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, f.Settings);
        await OperatorAsync(d);
        using var options = await d.Client.GetAsync(ApiRoot + "/options?offset=0&limit=1");
        Assert.Equal(HttpStatusCode.OK, options.StatusCode);
        var page = (await options.Content.ReadFromJsonAsync<Page<StorageOptionView>>())!;
        Assert.Equal("local-0", Assert.Single(page.Items).Alias);
        Assert.True(page.HasMore);
        Assert.True(options.Headers.CacheControl?.NoStore);
        var row = await RegisterAsync(d);
        Assert.Equal("unknown", row.Health);
        Assert.Equal(1, row.Version);
        using var list = await d.Client.GetAsync(ApiRoot + "/locations");
        Assert.DoesNotContain(f.Root, await list.Content.ReadAsStringAsync());
        using var target = await d.Client.GetAsync(ApiRoot + "/write-target");
        Assert.Equal(new StorageTargetView(null, 1), await target.Content.ReadFromJsonAsync<StorageTargetView>());
        using var duplicate = await d.PostAsync(ApiRoot + "/locations", new { alias = "local-0", reason = "ซ้ำ" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var missing = await d.PostAsync(ApiRoot + "/locations", new { alias = "not-configured", reason = "ไม่พบ" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Theory]
    [InlineData("/options?offset=-1")]
    [InlineData("/locations?limit=0")]
    [InlineData("/health?limit=101")]
    [InlineData("/options?offset=not-a-number")]
    public async Task Invalid_pagination_is_rejected_and_audited(string path)
    {
        using var f = new StorageTestFixture();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, f.Settings);
        var actor = await OperatorAsync(d);
        using var response = await d.Client.GetAsync(ApiRoot + path);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = d.Database.CreateContext();
        Assert.True(await db.AuditEvents.AnyAsync(x => x.ActorId == actor && x.EventType == "attendance.storage.denied"));
    }

    [Fact]
    public async Task Forged_root_and_missing_required_fields_are_not_ignored_or_logged()
    {
        using var f = new StorageTestFixture();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, f.Settings);
        await OperatorAsync(d);
        using var forged = await d.PostAsync(ApiRoot + "/locations", new { alias = "local-0", reason = "RAW-BODY-MARKER", rootPath = "RAW-PATH-MARKER" });
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        using var missing = await d.PostAsync(ApiRoot + "/locations", new { alias = "local-0" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        await using var db = d.Database.CreateContext();
        Assert.Empty(await db.Set<StorageLocation>().ToArrayAsync());
        Assert.DoesNotContain("RAW-", string.Join(" ", await db.AuditMetadata.Select(x => x.Value).ToArrayAsync()));
    }
}
