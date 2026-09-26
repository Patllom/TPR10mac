using System.Runtime.Versioning;
using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Attendance.Storage;
using TPR10.Api.Data;
using TPR10.Api.Scopes;
using static TPR10.Api.IntegrationTests.EvidenceMigrationTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database"), UnsupportedOSPlatform("windows")]
public sealed class EvidenceMigrationBoundsTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Resume_resets_all_unfinished_items_without_tracking_archive_or_changing_completed_items()
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        var request = await RequestAsync(f);
        await PrepareAsync(f, await ReserveAsync(f, 60));
        var job = await StartAsync(f, request);
        Assert.Equal(2, await RunAsync(f, job.Id, 2));
        await using var check = f.D.Database.CreateContext();
        var completed = await check.Set<MigrationItem>().AsNoTracking().Where(x => x.Status == "Completed").ToArrayAsync();
        var pending = await check.Set<MigrationItem>().Where(x => x.Status != "Completed").ToArrayAsync();
        Assert.Equal(120, pending.Length);
        foreach (var item in pending)
        {
            item.Status = "Copying"; item.Attempts = 3; item.ErrorCode = "storage-unavailable";
            item.LeaseOwner = Guid.NewGuid(); item.LeaseUntilUtc = f.D.Clock.GetUtcNow().AddSeconds(60);
            item.FencingVersion = 7; item.Version++; item.NextAttemptAtUtc = f.D.Clock.GetUtcNow().AddSeconds(30);
        }
        var blocked = await check.Set<MigrationJob>().SingleAsync();
        blocked.Status = "Blocked"; blocked.Version++;
        await check.SaveChangesAsync();
        var oldVersions = pending.ToDictionary(x => x.Id, x => x.Version);
        using var scope = f.D.Factory.Services.CreateScope();
        await EvidenceRevocationTests.SetSession(scope.ServiceProvider, f.Admin);
        foreach (var row in await check.Set<StorageLocation>().AsNoTracking().ToArrayAsync())
            await scope.ServiceProvider.GetRequiredService<StorageRegistry>().ProbeAsync(row.Id, new(row.Version, "ตรวจเพื่อ resume"), default);
        var result = await scope.ServiceProvider.GetRequiredService<MigrationService>().ResumeAsync(job.Id,
            new(blocked.Version, "ทดสอบ resume คลังใหญ่"), default);
        Assert.Equal(202, ((IStatusCodeHttpResult)result).StatusCode);
        var db = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>();
        Assert.InRange(db.ChangeTracker.Entries<MigrationItem>().Count(), 0, 100);
        var after = await check.Set<MigrationItem>().AsNoTracking().ToArrayAsync();
        Assert.Equal(122, after.Length);
        foreach (var item in after.Where(x => oldVersions.ContainsKey(x.Id)))
        {
            Assert.Equal("Pending", item.Status); Assert.Equal(0, item.Attempts); Assert.Null(item.ErrorCode);
            Assert.Null(item.LeaseOwner); Assert.Null(item.LeaseUntilUtc); Assert.Null(item.NextAttemptAtUtc);
            Assert.Equal(8, item.FencingVersion); Assert.Equal(oldVersions[item.Id] + 1, item.Version);
        }
        foreach (var old in completed)
        {
            var item = Assert.Single(after, x => x.Id == old.Id);
            Assert.Equal("Completed", item.Status); Assert.Equal(old.Version, item.Version);
            Assert.Equal(old.FencingVersion, item.FencingVersion); Assert.Equal(old.Attempts, item.Attempts);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Archive_manifest_does_not_materialize_entire_archive_and_late_reconcile_is_bounded(bool late)
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        var request = await RequestAsync(f);
        var rows = await ReserveAsync(f, 60);
        if (!late) await PrepareAsync(f, rows);
        using var scope = f.D.Factory.Services.CreateScope();
        await EvidenceRevocationTests.SetSession(scope.ServiceProvider, f.Admin);
        var result = await scope.ServiceProvider.GetRequiredService<MigrationService>().StartAsync(request, default);
        Assert.Equal(202, ((IStatusCodeHttpResult)result).StatusCode);
        var db = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>();
        Assert.InRange(db.ChangeTracker.Entries<MigrationItem>().Count(), 0, 100);
        var job = await db.Set<MigrationJob>().SingleAsync();
        if (!late) Assert.Equal(122, await db.Set<MigrationItem>().CountAsync());
        else
        {
            Assert.Equal(2, await db.Set<MigrationItem>().CountAsync());
            await PrepareAsync(f, rows);
            db.ChangeTracker.Clear();
            await using (var tx = await ScopeOperation.BeginAsync(db, default))
            {
                await MigrationManifest.ReconcileAsync(db, job, f.D.Clock.GetUtcNow(), default);
                Assert.InRange(db.ChangeTracker.Entries<MigrationItem>().Count(), 1, 100);
                await db.SaveChangesAsync(); await tx.CommitAsync();
            }
            Assert.Equal(102, await db.Set<MigrationItem>().CountAsync());
            db.ChangeTracker.Clear();
            await using (var tx = await ScopeOperation.BeginAsync(db, default))
            {
                await MigrationManifest.ReconcileAsync(db, job, f.D.Clock.GetUtcNow(), default);
                await db.SaveChangesAsync(); await tx.CommitAsync();
            }
            Assert.Equal(122, await db.Set<MigrationItem>().CountAsync());
        }
        // An actual multi-object batch, not just the single-object fixture. Other work can acquire the lock between batches.
        var queries = new ManifestQueries(); f.D.Factory.CommandInterceptor = queries;
        Assert.Equal(20, await RunAsync(f, job.Id, 20));
        Assert.InRange(queries.Count, 0, 2);
        await using var check = f.D.Database.CreateContext();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var independent = await ScopeOperation.BeginAsync(check, deadline.Token);
        Assert.Equal(20, await check.Set<MigrationItem>().CountAsync(x => x.Status == "Completed"));
        Assert.NotEqual("Completed", (await check.Set<MigrationJob>().SingleAsync()).Status);
    }

    private sealed class ManifestQueries : DbCommandInterceptor
    {
        public int Count { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("NOT EXISTS", StringComparison.Ordinal) && command.CommandText.Contains("evidence_migration_items", StringComparison.Ordinal)
                && command.CommandText.Contains("evidence_locations", StringComparison.Ordinal)) Count++;
            return ValueTask.FromResult(result);
        }
    }

    private static async Task<EvidenceObject[]> ReserveAsync(EvidenceFixture f, int count)
    {
        await using var db = f.D.Database.CreateContext();
        var original = await db.Set<EvidenceObject>().AsNoTracking().SingleAsync();
        var rows = Enumerable.Range(0, count).Select(_ =>
        {
            var id = Guid.NewGuid();
            return new EvidenceObject
            {
                Id = id,
                OperationId = Guid.NewGuid(),
                OwnerId = original.OwnerId,
                MembershipId = original.MembershipId,
                WorkspaceId = original.WorkspaceId,
                DepartmentId = original.DepartmentId,
                SnapshotAtUtc = original.SnapshotAtUtc,
                OccurredAtUtc = original.OccurredAtUtc,
                Action = original.Action,
                StorageId = original.StorageId,
                StorageVersion = original.StorageVersion,
                ObjectKey = "objects/" + id.ToString("N")[..2] + "/" + id.ToString("N"),
                CreatedAtUtc = original.CreatedAtUtc
            };
        }).ToArray();
        db.AddRange(rows); await db.SaveChangesAsync(); return rows;
    }

    private static async Task PrepareAsync(EvidenceFixture f, EvidenceObject[] rows)
    {
        await using var db = f.D.Database.CreateContext();
        var original = await db.Set<EvidenceObject>().AsNoTracking().SingleAsync(x => x.Id == f.Pin.EvidenceId);
        var location = await db.Set<StorageLocation>().SingleAsync(x => x.Id == f.Pin.StorageId);
        var adapter = f.D.Factory.Services.GetRequiredService<StorageRuntime>().Resolve(location);
        var full = await adapter.ReadVerifiedAsync(StorageObjectKey.Create(original.Id, "full"), original.Sha256!, original.Length!.Value, default);
        var thumb = await adapter.ReadVerifiedAsync(StorageObjectKey.Create(original.Id, "thumbnail"), original.ThumbnailSha256!, original.ThumbnailLength!.Value, default);
        foreach (var row in rows)
        {
            await adapter.WriteImmutableAsync(StorageObjectKey.Create(row.Id, "full"), full, default);
            await adapter.WriteImmutableAsync(StorageObjectKey.Create(row.Id, "thumbnail"), thumb, default);
            db.Attach(row);
            row.Sha256 = original.Sha256; row.Length = original.Length; row.Width = original.Width; row.Height = original.Height;
            row.ThumbnailSha256 = original.ThumbnailSha256; row.ThumbnailLength = original.ThumbnailLength;
            row.ThumbnailWidth = original.ThumbnailWidth; row.ThumbnailHeight = original.ThumbnailHeight;
            row.State = EvidenceState.Prepared; row.Version++;
            foreach (var variant in new[] { "full", "thumbnail" }) db.Add(new EvidenceLocation
            {
                Id = Guid.NewGuid(),
                EvidenceId = row.Id,
                StorageId = row.StorageId,
                Variant = variant,
                ObjectKey = StorageObjectKey.Create(row.Id, variant),
                Sha256 = variant == "full" ? row.Sha256! : row.ThumbnailSha256!,
                Length = (variant == "full" ? row.Length : row.ThumbnailLength)!.Value,
                State = CopyState.Verified,
                VerifiedAtUtc = f.D.Clock.GetUtcNow(),
                CreatedAtUtc = f.D.Clock.GetUtcNow()
            });
        }
        await db.SaveChangesAsync();
    }
}
