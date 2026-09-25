using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Storage;
using static TPR10.Api.IntegrationTests.StorageTestFixture;

namespace TPR10.Api.IntegrationTests;

[Collection("database"), UnsupportedOSPlatform("windows")]
public sealed class StorageRegistryTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Switching_requires_fresh_readiness_and_expected_target_version()
    {
        using var f = new StorageTestFixture();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, f.Settings);
        await OperatorAsync(d);
        var row = await RegisterAsync(d);
        using var notReady = await d.PostAsync(ApiRoot + "/write-target", new { storageId = row.Id, expectedVersion = 1, reason = "ยังไม่ตรวจ" });
        Assert.Equal(HttpStatusCode.Conflict, notReady.StatusCode);
        row = await ProbeAsync(d, row);
        using var switched = await d.PostAsync(ApiRoot + "/write-target", new { storageId = row.Id, expectedVersion = 1, reason = "เปลี่ยนที่เก็บ" });
        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);
        Assert.Equal(new StorageTargetView(row.Id, 2), await switched.Content.ReadFromJsonAsync<StorageTargetView>());
        using var stale = await d.PostAsync(ApiRoot + "/write-target", new { storageId = row.Id, expectedVersion = 1, reason = "รุ่นเก่า" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        d.Advance(TimeSpan.FromSeconds(60));
        using var expired = await d.PostAsync(ApiRoot + "/write-target", new { storageId = row.Id, expectedVersion = 2, reason = "readiness เก่า" });
        Assert.Equal(HttpStatusCode.Conflict, expired.StatusCode);
        using var health = await d.Client.GetAsync(ApiRoot + "/health");
        Assert.Equal("unknown", Assert.Single((await health.Content.ReadFromJsonAsync<Page<StorageHealthView>>())!.Items).Status);
    }

    [Fact]
    public async Task Simultaneous_switches_have_one_winner_and_one_committed_audit()
    {
        using var f = new StorageTestFixture();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, f.Settings);
        await OperatorAsync(d);
        var a = await ProbeAsync(d, await RegisterAsync(d));
        var b = await ProbeAsync(d, await RegisterAsync(d, "local-1"));
        var token = await IdentityTestDriver.TokenAsync(d.Client);
        using var first = IdentityTestDriver.Mutation(token, ApiRoot + "/write-target");
        first.Content = JsonContent.Create(new { storageId = a.Id, expectedVersion = 1, reason = "งานหนึ่ง" });
        using var second = IdentityTestDriver.Mutation(token, ApiRoot + "/write-target");
        second.Content = JsonContent.Create(new { storageId = b.Id, expectedVersion = 1, reason = "งานสอง" });
        var results = await Task.WhenAll(d.Client.SendAsync(first), d.Client.SendAsync(second));
        try
        {
            Assert.Single(results, x => x.StatusCode == HttpStatusCode.OK);
            Assert.Single(results, x => x.StatusCode == HttpStatusCode.Conflict);
            await using var db = d.Database.CreateContext();
            Assert.Equal(2, (await db.Set<StorageWriteTarget>().SingleAsync()).Version);
            Assert.Equal(1, await db.AuditEvents.CountAsync(x => x.EventType == "attendance.storage.switch" && x.Outcome == "success"));
        }
        finally { foreach (var result in results) result.Dispose(); }
    }

    [Fact]
    public async Task Missing_mount_marker_fails_probe_without_repair_or_implicit_fallback()
    {
        using var f = new StorageTestFixture();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, f.Settings);
        await OperatorAsync(d);
        var row = await RegisterAsync(d);
        var marker = Path.Combine(f.Root, "volume-0", ".tpr10-storage-id");
        File.Delete(marker);
        using var response = await d.PostAsync(ApiRoot + $"/locations/{row.Id}/probe", new { expectedVersion = row.Version, reason = "ที่เก็บหาย" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(File.Exists(marker));
        Assert.DoesNotContain(f.Root, await response.Content.ReadAsStringAsync());
        await using var db = d.Database.CreateContext();
        Assert.Equal("unavailable", (await db.Set<StorageLocation>().SingleAsync()).Health);
        Assert.Empty(await db.Set<StorageWriteTarget>().ToArrayAsync());
        var health = Assert.Single((await d.Client.GetFromJsonAsync<Page<StorageHealthView>>(ApiRoot + "/health"))!.Items);
        Assert.Equal("storage-unavailable", health.ErrorCode);
    }
}
