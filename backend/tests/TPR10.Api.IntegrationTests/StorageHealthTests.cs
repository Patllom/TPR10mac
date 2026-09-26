using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using TPR10.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TPR10.Api.Attendance.Storage;
using TPR10.Api.Attendance.Evidence;
using static TPR10.Api.IntegrationTests.StorageTestFixture;

namespace TPR10.Api.IntegrationTests;

[Collection("database"), UnsupportedOSPlatform("windows")]
public sealed class StorageHealthTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sealed_migration_source_still_reports_corrupt_active_or_fallback_copy(bool cutover)
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        var job = await EvidenceMigrationTests.StartAsync(f, await EvidenceMigrationTests.RequestAsync(f));
        if (cutover) Assert.Equal(2, await EvidenceMigrationTests.RunAsync(f, job.Id));
        await using var db = f.D.Database.CreateContext();
        var source = await db.Set<StorageLocation>().AsNoTracking().SingleAsync(x => x.Id == f.Pin.StorageId);
        Assert.False(source.AcceptWrites);
        var copy = await db.Set<EvidenceLocation>().SingleAsync(x => x.StorageId == source.Id && x.Variant == "full");
        Assert.Equal(cutover ? CopyState.Fallback : CopyState.Active, copy.State);
        await File.WriteAllBytesAsync(Path.Combine(f.Storage.Root, "volume-0", copy.ObjectKey), new byte[] { 1, 2, 3 });
        await Scan(f.D);
        source = await db.Set<StorageLocation>().AsNoTracking().SingleAsync(x => x.Id == source.Id);
        var runtime = f.D.Factory.Services.GetRequiredService<StorageRuntime>();
        Assert.False(runtime.Ready(source)); // Integrity scanning must not reopen writes.
        Assert.Equal(1, runtime.Health(source).MissingObjects);
        Assert.Equal("manifest-copy-unverified", runtime.Health(source).ErrorCode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Unknown_capacity_must_preserve_orphans_and_never_be_promoted_to_write_ready(int unknownAt)
    {
        using var f = new StorageTestFixture();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, f.Settings);
        var actor = await OperatorAsync(d); var storage = await RegisterAsync(d);
        var subject = await StoragePinTests.Employee(d, actor); var id = Guid.NewGuid();
        await using (var db = d.Database.CreateContext())
        {
            var row = new EvidenceObject
            {
                Id = id,
                OperationId = Guid.NewGuid(),
                OwnerId = subject.EmployeeId,
                MembershipId = subject.MembershipId,
                WorkspaceId = subject.WorkspaceId,
                DepartmentId = subject.DepartmentId,
                SnapshotAtUtc = subject.OccurredAtUtc,
                OccurredAtUtc = subject.OccurredAtUtc,
                StorageId = storage.Id,
                StorageVersion = storage.Version,
                ObjectKey = StorageObjectKey.Create(id, "full")[..^9],
                CreatedAtUtc = d.Clock.GetUtcNow()
            };
            db.Add(row); await db.SaveChangesAsync(); row.State = EvidenceState.Orphan; row.Version++; await db.SaveChangesAsync();
        }
        var runtime = new StorageRuntime(d.Factory.Services.GetRequiredService<IOptionsMonitor<StorageOptions>>(), d.Clock,
            (key, def) => new LostCapacityAdapter(new FolderStorageAdapter(key, def, d.Clock), unknownAt));
        using var scope = d.Factory.Services.CreateScope();
        await ActivatorUtilities.CreateInstance<StorageHealthScanner>(scope.ServiceProvider, runtime).ScanAsync(default);
        await using var check = d.Database.CreateContext();
        var current = await check.Set<StorageLocation>().SingleAsync();
        if (runtime.Ready(current))
        {
            var scanner = ActivatorUtilities.CreateInstance<StorageHealthScanner>(scope.ServiceProvider, runtime);
            var refresh = typeof(StorageHealthScanner).GetMethod("RefreshAsync"); Assert.NotNull(refresh);
            await (Task)refresh.Invoke(scanner, new object[] { CancellationToken.None })!;
            await check.Entry(current).ReloadAsync();
        }
        Assert.Equal("unknown", current.Health); Assert.False(runtime.Ready(current));
        Assert.Equal(1, runtime.Health(current).OrphanObjects);
        Assert.Equal("capacity-unknown", runtime.Health(current).ErrorCode);
    }

    private sealed class LostCapacityAdapter(IStorageAdapter inner, int unknownAt) : IStorageAdapter
    {
        private int calls;
        public Task<StoredCopy> WriteImmutableAsync(string key, ReadOnlyMemory<byte> bytes, CancellationToken ct) => inner.WriteImmutableAsync(key, bytes, ct);
        public Task<byte[]> ReadVerifiedAsync(string key, string sha256, long length, CancellationToken ct) => inner.ReadVerifiedAsync(key, sha256, length, ct);
        public async Task<StorageHealthView> ProbeAsync(CancellationToken ct)
        {
            var health = await inner.ProbeAsync(ct);
            return ++calls >= unknownAt ? health with { Status = "unknown", FreeBytes = null, TotalBytes = null } : health;
        }
    }

    [Theory]
    [InlineData("baseline")]
    [InlineData("manual-probe")]
    [InlineData("new-cycle")]
    [InlineData("slow")]
    public async Task Manifest_scan_is_bounded_and_preserves_integrity_independently_of_readiness(string mode)
    {
        using var f = new StorageTestFixture();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, f.Settings);
        var actor = await OperatorAsync(d); var storage = await ProbeAsync(d, await RegisterAsync(d));
        var subject = await StoragePinTests.Employee(d, actor);
        await using var db = d.Database.CreateContext();
        var rows = Enumerable.Range(0, 101).Select(_ =>
        {
            var id = Guid.NewGuid();
            return new EvidenceObject
            {
                Id = id,
                OperationId = Guid.NewGuid(),
                OwnerId = subject.EmployeeId,
                MembershipId = subject.MembershipId,
                WorkspaceId = subject.WorkspaceId,
                DepartmentId = subject.DepartmentId,
                SnapshotAtUtc = subject.OccurredAtUtc,
                OccurredAtUtc = subject.OccurredAtUtc,
                Action = EvidenceAction.CheckIn,
                StorageId = storage.Id,
                StorageVersion = storage.Version,
                ObjectKey = StorageObjectKey.Create(id, "full")[..^9],
                CreatedAtUtc = d.Clock.GetUtcNow()
            };
        }).ToArray();
        db.AddRange(rows); await db.SaveChangesAsync();
        foreach (var row in rows)
        {
            row.State = EvidenceState.Prepared; row.Version++;
            row.Sha256 = row.ThumbnailSha256 = Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3 })); row.Length = row.ThumbnailLength = 3;
            row.Width = row.Height = row.ThumbnailWidth = row.ThumbnailHeight = 1;
        }
        await db.SaveChangesAsync();
        db.AddRange(rows.Select(row => new EvidenceLocation
        {
            Id = Guid.NewGuid(),
            EvidenceId = row.Id,
            StorageId = storage.Id,
            ObjectKey = StorageObjectKey.Create(row.Id, "full"),
            Sha256 = row.Sha256!,
            Length = 3,
            State = CopyState.Verified,
            VerifiedAtUtc = d.Clock.GetUtcNow(),
            CreatedAtUtc = d.Clock.GetUtcNow()
        }));
        await db.SaveChangesAsync();
        if (mode == "slow")
        {
            var other = await ProbeAsync(d, await RegisterAsync(d, "local-1"));
            await StoragePinTests.Switch(d, other.Id, 1);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var runtime = new StorageRuntime(d.Factory.Services.GetRequiredService<IOptionsMonitor<StorageOptions>>(), d.Clock,
                (key, def) => new PausedReadAdapter(new FolderStorageAdapter(key, def, d.Clock), entered, release));
            using var scanScope = d.Factory.Services.CreateScope();
            var scan = ActivatorUtilities.CreateInstance<StorageHealthScanner>(scanScope.ServiceProvider, runtime).ScanAsync(default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                // More than one TTL passes while manifest I/O remains suspended. Readiness must be independent.
                for (var i = 0; i < 2; i++)
                {
                    d.Advance(TimeSpan.FromSeconds(35));
                    using var refreshScope = d.Factory.Services.CreateScope();
                    var scanner = ActivatorUtilities.CreateInstance<StorageHealthScanner>(refreshScope.ServiceProvider, runtime);
                    var refresh = typeof(StorageHealthScanner).GetMethod("RefreshAsync"); Assert.NotNull(refresh);
                    await ((Task)refresh.Invoke(scanner, new object[] { CancellationToken.None })!).WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.All(await db.Set<StorageLocation>().AsNoTracking().ToArrayAsync(), row => Assert.True(runtime.Ready(row)));
                }
                using var pinScope = d.Factory.Services.CreateScope();
                await AttendanceAccessTests.SetSessionAsync(pinScope.ServiceProvider, subject.EmployeeId);
                var reservation = await ActivatorUtilities.CreateInstance<StorageRegistry>(pinScope.ServiceProvider, runtime)
                    .PinAsync(Guid.NewGuid(), subject, new(subject.OccurredAtUtc, EvidenceAction.CheckIn), default);
                Assert.Equal(other.Id, reservation.StorageId);
            }
            finally { release.TrySetResult(); await scan.WaitAsync(TimeSpan.FromSeconds(10)); }
            Assert.Equal(1, runtime.Scans[storage.Id].Missing);
            Assert.NotNull(runtime.Scans[storage.Id].After);
            return;
        }
        await Scan(d);
        var first = Assert.Single((await d.Client.GetFromJsonAsync<Page<StorageHealthView>>(ApiRoot + "/health"))!.Items);
        Assert.Equal(100, first.MissingObjects); Assert.Equal("warning", first.Status);
        await Scan(d);
        var second = Assert.Single((await d.Client.GetFromJsonAsync<Page<StorageHealthView>>(ApiRoot + "/health"))!.Items);
        Assert.Equal(101, second.MissingObjects);
        if (mode == "manual-probe")
        {
            var current = await db.Set<StorageLocation>().AsNoTracking().SingleAsync();
            using var probe = await d.PostAsync(ApiRoot + $"/locations/{storage.Id}/probe", new { expectedVersion = current.Version, reason = "ตรวจพร้อมโดยไม่ล้างผลรูปเสีย" });
            probe.EnsureSuccessStatusCode();
            Assert.Equal(101, (await probe.Content.ReadFromJsonAsync<StorageHealthView>())!.MissingObjects);
            var after = Assert.Single((await d.Client.GetFromJsonAsync<Page<StorageHealthView>>(ApiRoot + "/health"))!.Items);
            Assert.Equal(101, after.MissingObjects); Assert.Equal("warning", after.Status);
        }
        if (mode == "new-cycle")
        {
            var current = await db.Set<StorageLocation>().AsNoTracking().SingleAsync();
            var adapter = d.Factory.Services.GetRequiredService<StorageRuntime>().Resolve(current);
            foreach (var copy in await db.Set<EvidenceLocation>().OrderBy(x => x.Id).Take(100).ToArrayAsync())
                await adapter.WriteImmutableAsync(copy.ObjectKey, new byte[] { 1, 2, 3 }, default);
            await Scan(d);
            Assert.Equal(101, Assert.Single((await d.Client.GetFromJsonAsync<Page<StorageHealthView>>(ApiRoot + "/health"))!.Items).MissingObjects);
            await Scan(d);
            Assert.Equal(1, Assert.Single((await d.Client.GetFromJsonAsync<Page<StorageHealthView>>(ApiRoot + "/health"))!.Items).MissingObjects);
        }
        Assert.Equal(101, await db.Set<EvidenceLocation>().CountAsync());
        Assert.All(await db.Set<EvidenceObject>().AsNoTracking().ToArrayAsync(), row => Assert.Equal(EvidenceState.Prepared, row.State));
    }

    private sealed class PausedReadAdapter(IStorageAdapter inner, TaskCompletionSource entered, TaskCompletionSource release) : IStorageAdapter
    {
        public Task<StoredCopy> WriteImmutableAsync(string key, ReadOnlyMemory<byte> bytes, CancellationToken ct) => inner.WriteImmutableAsync(key, bytes, ct);
        public Task<StorageHealthView> ProbeAsync(CancellationToken ct) => inner.ProbeAsync(ct);
        public async Task<byte[]> ReadVerifiedAsync(string key, string sha256, long length, CancellationToken ct)
        { entered.TrySetResult(); await release.Task.WaitAsync(ct); return await inner.ReadVerifiedAsync(key, sha256, length, ct); }
    }

    [Fact]
    public async Task Scheduled_scan_refreshes_health_but_never_selects_a_target_or_repairs_a_missing_mount()
    {
        using var f = new StorageTestFixture();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, f.Settings);
        await OperatorAsync(d);
        var row = await RegisterAsync(d);
        await Scan(d);
        await using (var db = d.Database.CreateContext())
        {
            var current = await db.Set<StorageLocation>().SingleAsync();
            Assert.Contains(current.Health, new[] { "ready", "warning" });
            Assert.Equal(2, current.Version);
            Assert.Empty(await db.Set<StorageWriteTarget>().ToArrayAsync());
        }
        File.Delete(Path.Combine(f.Root, "volume-0", ".tpr10-storage-id"));
        d.Advance(TimeSpan.FromSeconds(60));
        await Scan(d);
        var health = Assert.Single((await d.Client.GetFromJsonAsync<Page<StorageHealthView>>(ApiRoot + "/health"))!.Items);
        Assert.Equal("unavailable", health.Status);
        Assert.NotNull(typeof(StorageHealthView).GetProperty("ErrorCode")?.GetValue(health));
        Assert.False(File.Exists(Path.Combine(f.Root, "volume-0", ".tpr10-storage-id")));
        Assert.Equal(row.Id, health.StorageId);
    }

    internal static async Task Scan(IdentityTestDriver d)
    {
        var type = typeof(StorageRegistry).Assembly.GetType("TPR10.Api.Attendance.Storage.StorageHealthScanner");
        Assert.NotNull(type);
        using var scope = d.Factory.Services.CreateScope();
        var scanner = scope.ServiceProvider.GetRequiredService(type);
        await (Task)type.GetMethod("ScanAsync")!.Invoke(scanner, [CancellationToken.None])!;
    }
}
