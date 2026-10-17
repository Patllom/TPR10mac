using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Attendance.Recording;
using TPR10.Api.Attendance.Storage;

namespace TPR10.Api.IntegrationTests;

[Collection("database"), UnsupportedOSPlatform("windows")]
public sealed class AttendanceRecoveryExitTests(PostgresFixture postgres)
{
    // Catches readers incorrectly resolving old evidence against the current write target,
    // or recovery losing either event's bytes/binding. Switch API authorization has its own suite.
    [Fact]
    public async Task Closed_pair_on_two_local_targets_survives_database_and_file_restore()
    {
        await using var f = await RecordingHttpFixture.CreateAsync(postgres.ConnectionString);
        using var entered = await f.Upload(await f.Metadata());
        Assert.Equal(HttpStatusCode.Created, entered.StatusCode);
        var entry = (await entered.Content.ReadFromJsonAsync<CaptureReceipt>())!;
        var runtime = f.Factory.Services.GetRequiredService<StorageRuntime>();
        var definition = runtime.Find("local-1")!;
        var target = new StorageLocation
        {
            Id = Guid.NewGuid(),
            Alias = definition.Alias,
            Kind = definition.Kind,
            ConfigFingerprint = StorageRuntime.Fingerprint(definition),
            AcceptWrites = true,
            CreatedAtUtc = f.Recording.Capture.Clock.GetUtcNow()
        };
        await using (var setup = f.Recording.Capture.Data.Database.CreateContext())
        {
            setup.Add(target);
            var pointer = await setup.Set<StorageWriteTarget>().SingleAsync();
            pointer.StorageId = target.Id; pointer.Version++;
            await setup.SaveChangesAsync();
        }
        runtime.Remember(target, await runtime.Resolve(target).ProbeAsync(default));
        f.Recording.Capture.Clock.Advance(TimeSpan.FromSeconds(1));
        using var left = await f.Upload(await f.Metadata("CheckOut"), "check-outs");
        Assert.Equal(HttpStatusCode.Created, left.StatusCode);
        var exit = (await left.Content.ReadFromJsonAsync<CaptureReceipt>())!;
        Assert.Equal(entry.DayId, exit.DayId);
        await using var db = f.Recording.Capture.Data.Database.CreateContext();
        var day = await db.Set<AttendanceDay>().AsNoTracking().SingleAsync();
        Assert.Equal(new DateOnly(2026, 9, 26), day.WorkDate);
        Assert.NotNull(day.EffectiveOutUtc);
        Assert.Equal(2, await db.Set<AttendanceEvent>().CountAsync());
        Assert.Equal(2, await db.Set<EvidenceBinding>().CountAsync());
        Assert.Equal(f.Recording.Location.Id, (await db.Set<EvidenceObject>().SingleAsync(x => x.Id == entry.EvidenceId)).StorageId);
        Assert.Equal(target.Id, (await db.Set<EvidenceObject>().SingleAsync(x => x.Id == exit.EvidenceId)).StorageId);
        var expected = new Dictionary<string, byte[]>();
        foreach (var id in new[] { entry.EvidenceId, exit.EvidenceId })
            foreach (var suffix in new[] { "", "/thumbnail", "/download" })
            {
                var url = $"/api/v1/attendance/evidence/{id}{suffix}";
                using var read = await f.Client.GetAsync(url);
                Assert.Equal(HttpStatusCode.OK, read.StatusCode);
                expected[url] = await read.Content.ReadAsByteArrayAsync();
            }
        var copies = await db.Set<EvidenceLocation>().AsNoTracking().ToArrayAsync();
        Assert.Equal(4, copies.Length);
        // All files belong to this disposable fixture; never touch a user storage root.
        await f.Factory.DisposeAsync();
        await using var restored = new IdentityDatabase(postgres.ConnectionString);
        await restored.InitializeAsync();
        await postgres.BackupRestoreAsync(f.Recording.Capture.Data.Database.ConnectionString, restored.ConnectionString);
        foreach (var copy in copies)
        {
            var volume = copy.StorageId == target.Id ? "volume-1" : "volume-0";
            var path = Path.Combine(f.Recording.Storage.Root, volume, copy.ObjectKey);
            var backup = path + ".recovery-test";
            File.Copy(path, backup);
            File.Delete(path);
            File.Copy(backup, path);
            File.Delete(backup);
            Assert.Equal(copy.Sha256, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))));
        }
        await using var host = new ApiFactory(restored.ConnectionString, "Production", f.Recording.Capture.Clock, f.Recording.Storage.Settings);
        using var owner = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443") });
        owner.DefaultRequestHeaders.Add("Cookie", f.Client.DefaultRequestHeaders.GetValues("Cookie"));
        using var anonymous = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443") });
        foreach (var (url, bytes) in expected)
        {
            using var read = await owner.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal(bytes, await read.Content.ReadAsByteArrayAsync());
            Assert.True(read.Headers.CacheControl?.NoStore);
            using var denied = await anonymous.GetAsync(url);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        await using var restoredDb = restored.CreateContext();
        var recovered = await restoredDb.Set<AttendanceDay>().SingleAsync();
        Assert.Equal(day.Id, recovered.Id);
        Assert.Equal(day.Version, recovered.Version);
        Assert.Equal(day.EffectiveInUtc, recovered.EffectiveInUtc);
        Assert.Equal(day.EffectiveOutUtc, recovered.EffectiveOutUtc);
        Assert.Equal(2, await restoredDb.Set<AttendanceEvent>().CountAsync());
        Assert.Equal(2, await restoredDb.Set<EvidenceBinding>().CountAsync());
    }
}
