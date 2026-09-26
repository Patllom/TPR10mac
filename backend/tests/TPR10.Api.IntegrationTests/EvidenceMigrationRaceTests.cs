using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Attendance.Storage;
using TPR10.Api.Data;
using TPR10.Api.Identity.Data;
using TPR10.Api.Scopes;
using static TPR10.Api.IntegrationTests.EvidenceMigrationTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database"), UnsupportedOSPlatform("windows")]
public sealed class EvidenceMigrationRaceTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_workers_cannot_cut_over_the_same_item_and_expired_worker_is_fenced(bool expire)
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        var job = await StartAsync(f, await RequestAsync(f));
        using var scope = f.D.Factory.Services.CreateScope();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = Runtime(f, scope.ServiceProvider, inner => new HookAdapter(inner, async () => { reached.TrySetResult(); await resume.Task; }));
        var worker = ActivatorUtilities.CreateInstance<MigrationWorker>(scope.ServiceProvider, runtime);
        var pending = worker.RunBatchAsync(job.Id, 1, default);
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (expire) f.D.Advance(TimeSpan.FromSeconds(60));
            Assert.Equal(expire ? 2 : 1, await RunAsync(f, job.Id));
            using var read = await f.Owner.GetAsync(f.Url);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }
        finally { resume.TrySetResult(); }
        Assert.Equal(expire ? 0 : 1, await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        await RunAsync(f, job.Id);
        await using var db = f.D.Database.CreateContext();
        Assert.Equal("Completed", (await db.Set<MigrationJob>().SingleAsync()).Status);
        Assert.Equal(2, await db.AuditEvents.CountAsync(x => x.EventType == "attendance.storage.migration-cutover"));
        Assert.Equal(4, await db.Set<EvidenceLocation>().CountAsync());
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("disable")]
    [InlineData("version")]
    public async Task Authority_and_storage_versions_are_rechecked_after_native_copy(string mutation)
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        var request = await RequestAsync(f);
        var job = await StartAsync(f, request);
        using var scope = f.D.Factory.Services.CreateScope();
        var runtime = Runtime(f, scope.ServiceProvider, inner => new HookAdapter(inner, async () =>
        {
            await using var db = f.D.Database.CreateContext();
            await using var tx = await ScopeOperation.BeginAsync(db, default);
            if (mutation == "revoke")
                db.RemoveRange(await db.Set<RolePermission>().Where(x => x.PermissionId == AttendanceIdentityTests.Permission(19)).ToArrayAsync());
            else if (mutation == "disable")
                (await db.Set<IdentityUser>().SingleAsync(x => x.Id == f.Admin)).IsActive = false;
            else (await db.Set<StorageLocation>().SingleAsync(x => x.Id == request.SourceId)).Version++;
            await db.SaveChangesAsync(); await tx.CommitAsync();
        }));
        Assert.Equal(0, await ActivatorUtilities.CreateInstance<MigrationWorker>(scope.ServiceProvider, runtime).RunBatchAsync(job.Id, 1, default));
        await using var check = f.D.Database.CreateContext();
        Assert.Equal(mutation == "version" ? "Running" : "Blocked", (await check.Set<MigrationJob>().SingleAsync()).Status);
        Assert.Equal(2, await check.Set<EvidenceLocation>().CountAsync(x => x.StorageId == request.SourceId && x.State == CopyState.Active));
        Assert.Empty(await check.Set<EvidenceLocation>().Where(x => x.StorageId == request.TargetId).ToArrayAsync());
        Assert.NotEmpty(System.IO.Directory.GetFiles(Path.Combine(f.Storage.Root, "volume-1"), "*.jpg", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("before-copy")]
    [InlineData("after-copy")]
    [InlineData("audit")]
    public async Task Crash_or_audit_failure_recovers_with_same_manifest_and_adopts_durable_copy(string point)
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        var job = await StartAsync(f, await RequestAsync(f));
        await using var db = f.D.Database.CreateContext();
        using var scope = f.D.Factory.Services.CreateScope();
        if (point == "audit") await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_migration_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
            IF NEW.event_type='attendance.storage.migration-cutover' THEN RAISE EXCEPTION 'test audit failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER fail_migration_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION fail_migration_audit();
            """);
        var runtime = Runtime(f, scope.ServiceProvider, inner => new HookAdapter(inner,
            () => point == "after-copy" ? throw new OperationCanceledException("simulated process exit") : Task.CompletedTask,
            point == "before-copy" ? () => throw new OperationCanceledException("simulated process exit") : null));
        var worker = ActivatorUtilities.CreateInstance<MigrationWorker>(scope.ServiceProvider, runtime);
        if (point == "audit") await Assert.ThrowsAsync<DbUpdateException>(() => worker.RunBatchAsync(job.Id, 1, default));
        else await Assert.ThrowsAsync<OperationCanceledException>(() => worker.RunBatchAsync(job.Id, 1, default));
        Assert.Equal(2, await db.Set<EvidenceLocation>().CountAsync());
        Assert.Equal(2, await db.Set<EvidenceLocation>().CountAsync(x => x.State == CopyState.Active));
        var paths = System.IO.Directory.GetFiles(Path.Combine(f.Storage.Root, "volume-1"), "*.jpg", SearchOption.AllDirectories);
        if (point == "before-copy") Assert.Empty(paths); else Assert.Single(paths);
        var bytes = paths.Length == 0 ? null : await File.ReadAllBytesAsync(paths[0]);
        if (point == "audit") await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_migration_audit ON audit_events; DROP FUNCTION fail_migration_audit();");
        f.D.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(2, await RunAsync(f, job.Id));
        Assert.Equal("Completed", (await db.Set<MigrationJob>().SingleAsync()).Status);
        Assert.Equal(2, await db.Set<MigrationItem>().CountAsync());
        if (bytes is not null) Assert.Equal(bytes, await File.ReadAllBytesAsync(paths[0]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sealed_source_waits_for_old_reservation_and_reconciles_late_prepared_without_duplicate_manifest(bool expire)
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString, "Reserved");
        var request = await RequestAsync(f);
        var job = await StartAsync(f, request);
        Assert.Equal(0, await RunAsync(f, job.Id));
        await using var db = f.D.Database.CreateContext();
        Assert.NotEqual("Completed", (await db.Set<MigrationJob>().AsNoTracking().SingleAsync()).Status);
        if (expire)
        {
            f.D.Advance(TimeSpan.FromSeconds(60)); await RunAsync(f, job.Id);
            Assert.Equal("Blocked", (await db.Set<MigrationJob>().AsNoTracking().SingleAsync()).Status);
        }
        using (var scope = f.D.Factory.Services.CreateScope())
        {
            await EvidenceRevocationTests.SetSession(scope.ServiceProvider, f.Subject.EmployeeId);
            await scope.ServiceProvider.GetRequiredService<EvidenceWriter>().PrepareAsync(f.Pin, new MemoryStream(EvidencePublicationTests.Photo()),
                new(f.Subject.OccurredAtUtc, EvidenceAction.CheckIn), default);
        }
        if (expire)
        {
            foreach (var location in await db.Set<StorageLocation>().AsNoTracking().ToArrayAsync())
                await StorageTestFixture.ProbeAsync(f.D, new(location.Id, location.Alias, location.Kind, location.Version, location.AcceptWrites, location.Health, location.CheckedAtUtc));
            var current = await db.Set<MigrationJob>().AsNoTracking().SingleAsync();
            using var response = await f.D.PostAsync(Route + $"/{job.Id}/resume", new ResumeMigration(current.Version, "ทำรายการค้างแล้ว"));
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
        Assert.Equal(2, await RunAsync(f, job.Id));
        Assert.Equal(0, await RunAsync(f, job.Id));
        Assert.Equal("Completed", (await db.Set<MigrationJob>().AsNoTracking().SingleAsync()).Status);
        Assert.Equal(2, await db.Set<MigrationItem>().CountAsync());
    }

    [Fact]
    public async Task Retry_backoff_is_bounded_and_requires_explicit_resume_after_exhaustion()
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        var job = await StartAsync(f, await RequestAsync(f));
        using var scope = f.D.Factory.Services.CreateScope();
        var runtime = Runtime(f, scope.ServiceProvider, inner => new HookAdapter(inner, () => Task.CompletedTask,
            () => throw new IOException("simulated disconnected volume")));
        var worker = ActivatorUtilities.CreateInstance<MigrationWorker>(scope.ServiceProvider, runtime);
        await using var db = f.D.Database.CreateContext();
        foreach (var delay in new[] { 1, 5, 30, 120, 300 })
        {
            Assert.Equal(0, await worker.RunBatchAsync(job.Id, 100, default));
            var items = await db.Set<MigrationItem>().AsNoTracking().ToArrayAsync();
            Assert.All(items, x => Assert.Equal(f.D.Clock.GetUtcNow().AddSeconds(delay), x.NextAttemptAtUtc));
            var attempts = items.Sum(x => x.Attempts);
            Assert.Equal(0, await worker.RunBatchAsync(job.Id, 100, default));
            Assert.Equal(attempts, await db.Set<MigrationItem>().SumAsync(x => x.Attempts));
            f.D.Advance(TimeSpan.FromSeconds(delay));
        }
        await worker.RunBatchAsync(job.Id, 100, default);
        Assert.Equal("Blocked", (await db.Set<MigrationJob>().SingleAsync()).Status);
        Assert.Equal(0, await RunAsync(f, job.Id));
    }

    private static StorageRuntime Runtime(EvidenceFixture f, IServiceProvider services, Func<IStorageAdapter, IStorageAdapter> wrap) =>
        new(services.GetRequiredService<IOptionsMonitor<StorageOptions>>(), f.D.Clock, (id, definition) => wrap(new FolderStorageAdapter(id, definition, f.D.Clock)));

    [Fact]
    public async Task Reader_prepared_before_cutover_can_deliver_retained_verified_source_after_cutover()
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        var job = await StartAsync(f, await RequestAsync(f));
        using var scope = f.D.Factory.Services.CreateScope();
        await EvidenceRevocationTests.SetSession(scope.ServiceProvider, f.Subject.EmployeeId);
        var result = await scope.ServiceProvider.GetRequiredService<EvidenceReader>().ReadAsync(f.Pin.EvidenceId, "full", false, default);
        Assert.Equal(2, await RunAsync(f, job.Id));
        var context = EvidenceRevocationTests.Context(scope.ServiceProvider);
        await result.ExecuteAsync(context);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.True(context.Response.Body.Length > 0);
        await using var db = f.D.Database.CreateContext();
        Assert.Equal(2, await db.Set<EvidenceLocation>().CountAsync(x => x.State == CopyState.Fallback));
    }

    [Fact]
    public async Task Completion_audit_failure_leaves_running_job_and_next_process_reconciles_without_recopy()
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        var job = await StartAsync(f, await RequestAsync(f));
        await using var db = f.D.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_complete_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
            IF NEW.event_type='attendance.storage.migration-completed' THEN RAISE EXCEPTION 'test audit failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER fail_complete_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION fail_complete_audit();
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => RunAsync(f, job.Id));
        Assert.Equal("Running", (await db.Set<MigrationJob>().AsNoTracking().SingleAsync()).Status);
        Assert.Equal(2, await db.Set<MigrationItem>().CountAsync(x => x.Status == "Completed"));
        var attempts = await db.Set<MigrationItem>().SumAsync(x => x.Attempts);
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_complete_audit ON audit_events; DROP FUNCTION fail_complete_audit();");
        Assert.Equal(0, await RunAsync(f, job.Id));
        Assert.Equal("Completed", (await db.Set<MigrationJob>().AsNoTracking().SingleAsync()).Status);
        Assert.Equal(attempts, await db.Set<MigrationItem>().SumAsync(x => x.Attempts));
    }

    private sealed class HookAdapter(IStorageAdapter inner, Func<Task> afterWrite, Func<Task>? beforeWrite = null) : IStorageAdapter
    {
        public Task<StorageHealthView> ProbeAsync(CancellationToken ct) => inner.ProbeAsync(ct);
        public Task<byte[]> ReadVerifiedAsync(string key, string sha256, long length, CancellationToken ct) => inner.ReadVerifiedAsync(key, sha256, length, ct);
        public async Task<StoredCopy> WriteImmutableAsync(string key, ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            if (beforeWrite is not null) await beforeWrite();
            var result = await inner.WriteImmutableAsync(key, bytes, ct);
            await afterWrite(); return result;
        }
    }
}
