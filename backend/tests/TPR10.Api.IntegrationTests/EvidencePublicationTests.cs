using System.Runtime.Versioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Data;
using TPR10.Api.Scopes;
using Npgsql;
using TPR10.Api.Attendance.Storage;
using Microsoft.Extensions.Options;
using static TPR10.Api.IntegrationTests.StorageTestFixture;

namespace TPR10.Api.IntegrationTests;

[Collection("database"), UnsupportedOSPlatform("windows")]
public sealed class EvidencePublicationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Partial_filesystem_failure_stays_private_and_matching_retry_recovers_without_overwrite()
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString, "Reserved");
        using var scope = f.D.Factory.Services.CreateScope();
        await EvidenceRevocationTests.SetSession(scope.ServiceProvider, f.Subject.EmployeeId);
        var fail = true;
        var runtime = new StorageRuntime(scope.ServiceProvider.GetRequiredService<IOptionsMonitor<StorageOptions>>(), f.D.Clock,
            (id, definition) => new ThumbnailFailure(new FolderStorageAdapter(id, definition, f.D.Clock), () => fail));
        var writer = ActivatorUtilities.CreateInstance<EvidenceWriter>(scope.ServiceProvider, runtime);
        var stamp = new StampRequest(f.Subject.OccurredAtUtc, EvidenceAction.CheckIn);
        await Assert.ThrowsAsync<IOException>(() => writer.PrepareAsync(f.Pin, new MemoryStream(Photo()), stamp, default));
        var first = Assert.Single(System.IO.Directory.GetFiles(f.Storage.Root, "*.jpg", SearchOption.AllDirectories));
        var bytes = await File.ReadAllBytesAsync(first);
        using (var response = await f.Owner.GetAsync(f.Url)) Assert.Equal(404, (int)response.StatusCode);
        fail = false; f.D.Advance(TimeSpan.FromMinutes(1));
        await writer.PrepareAsync(f.Pin, new MemoryStream(Photo()), stamp, default);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(first));
        await using var check = f.D.Database.CreateContext();
        Assert.Equal(EvidenceState.Prepared, (await check.Set<EvidenceObject>().SingleAsync()).State);
        Assert.Empty(await check.Set<EvidenceBinding>().ToArrayAsync());
    }

    [Fact]
    public async Task Publication_audit_failure_rolls_back_binding_and_state()
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString, "Prepared");
        using var scope = f.D.Factory.Services.CreateScope();
        await EvidenceRevocationTests.SetSession(scope.ServiceProvider, f.Subject.EmployeeId);
        var db = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_publish_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
            IF NEW.event_type='attendance.evidence.publish' THEN RAISE EXCEPTION 'test audit failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER fail_publish_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION fail_publish_audit();
            """);
        await using (var tx = await ScopeOperation.BeginAsync(db, default))
        {
            await scope.ServiceProvider.GetRequiredService<EvidenceWriter>().StagePublicationAsync(new(f.Pin.EvidenceId, f.Pin.OperationId, Guid.NewGuid(), f.Subject,
                new(f.Subject.OccurredAtUtc, EvidenceAction.CheckIn)), default);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            await tx.RollbackAsync();
        }
        db.ChangeTracker.Clear();
        Assert.Empty(await db.Set<EvidenceBinding>().ToArrayAsync());
        Assert.Equal(EvidenceState.Prepared, (await db.Set<EvidenceObject>().SingleAsync()).State);
    }

    private sealed class ThumbnailFailure(IStorageAdapter inner, Func<bool> fail) : IStorageAdapter
    {
        public Task<byte[]> ReadVerifiedAsync(string key, string sha256, long length, CancellationToken ct) => inner.ReadVerifiedAsync(key, sha256, length, ct);
        public Task<StorageHealthView> ProbeAsync(CancellationToken ct) => inner.ProbeAsync(ct);
        public Task<StoredCopy> WriteImmutableAsync(string key, ReadOnlyMemory<byte> bytes, CancellationToken ct) =>
            key.EndsWith("thumbnail.jpg", StringComparison.Ordinal) && fail() ? throw new IOException("simulated thumbnail storage failure") : inner.WriteImmutableAsync(key, bytes, ct);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_prepare_is_fenced_and_stale_worker_cannot_publish_metadata(bool expire)
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString, "Reserved");
        using var firstScope = f.D.Factory.Services.CreateScope();
        using var otherScope = f.D.Factory.Services.CreateScope();
        await EvidenceRevocationTests.SetSession(firstScope.ServiceProvider, f.Subject.EmployeeId);
        await EvidenceRevocationTests.SetSession(otherScope.ServiceProvider, f.Subject.EmployeeId);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var images = new PausedStamp(new ImageStampService(f.D.Clock), reached, resume);
        var first = ActivatorUtilities.CreateInstance<EvidenceWriter>(firstScope.ServiceProvider, images);
        var stamp = new StampRequest(f.Subject.OccurredAtUtc, EvidenceAction.CheckIn);
        var pending = first.PrepareAsync(f.Pin, new MemoryStream(Photo()), stamp, default);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = otherScope.ServiceProvider.GetRequiredService<EvidenceWriter>();
        var conflict = await Assert.ThrowsAsync<StorageOperationException>(() => second.PrepareAsync(f.Pin, new MemoryStream(Photo()), stamp, default));
        Assert.Equal(409, conflict.Status);
        if (expire)
        {
            await using var db = f.D.Database.CreateContext();
            var row = await db.Set<EvidenceObject>().SingleAsync();
            row.LeaseUntilUtc = f.D.Clock.GetUtcNow().AddSeconds(-1); row.FencingVersion++; row.Version++;
            await db.SaveChangesAsync();
        }
        resume.SetResult();
        if (expire) Assert.Equal(409, (await Assert.ThrowsAsync<StorageOperationException>(() => pending)).Status);
        else await pending;
        await using var check = f.D.Database.CreateContext();
        Assert.Equal(expire ? EvidenceState.Reserved : EvidenceState.Prepared, (await check.Set<EvidenceObject>().SingleAsync()).State);
        if (expire)
        {
            var retry = await second.PrepareAsync(f.Pin, new MemoryStream(Photo()), stamp, default);
            Assert.Equal(f.Pin.EvidenceId, retry.EvidenceId); // Immutable files already exist; matching retry completes metadata.
        }
    }

    private sealed class PausedStamp(IImageStampService inner, TaskCompletionSource reached, TaskCompletionSource resume) : IImageStampService
    {
        public async Task<StampedImage> StampAsync(Stream source, StampRequest stamp, CancellationToken ct)
        { reached.SetResult(); await resume.Task.WaitAsync(ct); return await inner.StampAsync(source, stamp, ct); }
        public StampedImage Thumbnail(StampedImage full) => inner.Thumbnail(full);
    }
    [Theory]
    [InlineData("operation")]
    [InlineData("owner")]
    [InlineData("snapshot")]
    [InlineData("stamp")]
    [InlineData("event")]
    [InlineData("no-transaction")]
    public async Task Publication_rejects_forged_binding_and_never_commits_for_caller(string fault)
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString, "Prepared");
        using var scope = f.D.Factory.Services.CreateScope();
        await EvidenceRevocationTests.SetSession(scope.ServiceProvider, f.Subject.EmployeeId);
        var writer = scope.ServiceProvider.GetRequiredService<EvidenceWriter>();
        var db = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>();
        var request = new EvidencePublication(f.Pin.EvidenceId, f.Pin.OperationId, Guid.NewGuid(), f.Subject, new(f.Subject.OccurredAtUtc, EvidenceAction.CheckIn));
        request = fault switch
        {
            "operation" => request with { OperationId = Guid.NewGuid() },
            "owner" => request with { Subject = f.Subject with { EmployeeId = f.Admin } },
            "snapshot" => request with { Subject = f.Subject with { OccurredAtUtc = f.Subject.OccurredAtUtc.AddSeconds(-1) } },
            "stamp" => request with { Stamp = request.Stamp with { Action = EvidenceAction.CheckOut } },
            "event" => request with { EventId = Guid.Empty },
            _ => request
        };
        await using var tx = fault == "no-transaction" ? null : await ScopeOperation.BeginAsync(db, default);
        var error = await Record.ExceptionAsync(() => writer.StagePublicationAsync(request, default));
        if (fault == "no-transaction") Assert.IsType<InvalidOperationException>(error);
        else Assert.Equal(409, Assert.IsType<TPR10.Api.Attendance.Storage.StorageOperationException>(error).Status);
        Assert.Empty(await db.Set<EvidenceBinding>().ToArrayAsync());
        Assert.Equal(EvidenceState.Prepared, (await db.Set<EvidenceObject>().AsNoTracking().SingleAsync()).State);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("storage")]
    [InlineData("key")]
    [InlineData("stamp")]
    [InlineData("live-lease")]
    public async Task Prepare_rejects_modified_reservation_before_creating_files(string fault)
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString, "Reserved");
        using var scope = f.D.Factory.Services.CreateScope();
        await EvidenceRevocationTests.SetSession(scope.ServiceProvider, f.Subject.EmployeeId);
        var db = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>();
        var pin = f.Pin;
        var stamp = new StampRequest(f.Subject.OccurredAtUtc, EvidenceAction.CheckIn);
        if (fault == "operation") pin = pin with { OperationId = Guid.NewGuid() };
        if (fault == "storage") pin = pin with { StorageId = Guid.NewGuid() };
        if (fault == "key") pin = pin with { ObjectKey = "../escape" };
        if (fault == "stamp") stamp = stamp with { Action = EvidenceAction.CheckOut };
        if (fault == "live-lease") { var row = await db.Set<EvidenceObject>().SingleAsync(); row.LeaseUntilUtc = f.D.Clock.GetUtcNow().AddMinutes(1); row.Version++; await db.SaveChangesAsync(); }
        var error = await Assert.ThrowsAsync<TPR10.Api.Attendance.Storage.StorageOperationException>(() => scope.ServiceProvider.GetRequiredService<EvidenceWriter>()
            .PrepareAsync(pin, new MemoryStream(Photo()), stamp, default));
        Assert.Equal(409, error.Status);
        Assert.Empty(System.IO.Directory.GetFiles(f.Storage.Root, "*.jpg", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task Input_digest_cannot_be_rewritten_or_downgraded_away()
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        await using var db = f.D.Database.CreateContext();
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE evidence_objects SET input_sha256=repeat('c',64),version=version+1"));
        var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync("20260925031404_AddAttendanceEvidenceStorage"));
    }

    [Fact]
    public async Task Reconcile_reports_only_expired_unbound_reservations_without_deleting_files()
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString, "Prepared");
        f.D.Advance(TimeSpan.FromHours(24));
        using var scope = f.D.Factory.Services.CreateScope();
        var type = typeof(EvidenceObject).Assembly.GetType("TPR10.Api.Attendance.Evidence.EvidenceReconciler");
        Assert.NotNull(type);
        var reconciler = scope.ServiceProvider.GetRequiredService(type);
        var count = await (Task<int>)type.GetMethod("ReportAsync")!.Invoke(reconciler, [CancellationToken.None])!;
        Assert.Equal(1, count);
        Assert.Equal(2, System.IO.Directory.GetFiles(f.Storage.Root, "*.jpg", SearchOption.AllDirectories).Length);
        await using var db = f.D.Database.CreateContext();
        Assert.Equal(EvidenceState.Prepared, (await db.Set<EvidenceObject>().SingleAsync()).State);
    }
    [Fact]
    public async Task Prepare_is_durable_idempotent_and_publication_belongs_to_caller_transaction()
    {
        using var f = new StorageTestFixture();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, f.Settings);
        var admin = await OperatorAsync(d);
        var storage = await ProbeAsync(d, await RegisterAsync(d));
        await StoragePinTests.Switch(d, storage.Id, 1);
        var subject = await StoragePinTests.Employee(d, admin);
        var stamp = new StampRequest(subject.OccurredAtUtc, EvidenceAction.CheckIn);
        var pin = await StoragePinTests.Pin(d, subject.EmployeeId, Guid.NewGuid(), subject, stamp);
        using var scope = d.Factory.Services.CreateScope();
        await AttendanceAccessTests.SetSessionAsync(scope.ServiceProvider, subject.EmployeeId);
        var writerType = typeof(EvidenceObject).Assembly.GetType("TPR10.Api.Attendance.Evidence.EvidenceWriter");
        Assert.NotNull(writerType);
        var writer = scope.ServiceProvider.GetRequiredService(writerType);
        var method = writerType.GetMethod("PrepareAsync")!;
        var bytes = Photo();
        async Task<PreparedEvidence> PrepareAsync() => await (Task<PreparedEvidence>)method.Invoke(writer, [pin, new MemoryStream(bytes), stamp, CancellationToken.None])!;
        var first = await PrepareAsync();
        Assert.Equal(first, await PrepareAsync());
        bytes = [.. bytes, 0]; // Same decoded pixels, different request bytes must conflict.
        var conflict = await Record.ExceptionAsync(PrepareAsync);
        Assert.NotNull(conflict);
        Assert.Equal(409, (int)conflict.GetType().GetProperty("Status")!.GetValue(conflict)!);
        var db = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>();
        Assert.Equal(EvidenceState.Prepared, (await db.Set<EvidenceObject>().AsNoTracking().SingleAsync()).State);
        Assert.Equal(2, await db.Set<EvidenceLocation>().CountAsync());
        var publication = new EvidencePublication(pin.EvidenceId, pin.OperationId, Guid.NewGuid(), subject, stamp);
        var publish = writerType.GetMethod("StagePublicationAsync")!;
        await using (var tx = await ScopeOperation.BeginAsync(db, CancellationToken.None))
        {
            await (Task)publish.Invoke(writer, [publication, CancellationToken.None])!;
            await db.SaveChangesAsync();
            await tx.RollbackAsync();
        }
        db.ChangeTracker.Clear();
        Assert.Empty(await db.Set<EvidenceBinding>().ToArrayAsync());
        Assert.Equal(EvidenceState.Prepared, (await db.Set<EvidenceObject>().SingleAsync()).State);
        await using (var tx = await ScopeOperation.BeginAsync(db, CancellationToken.None))
        {
            await (Task)publish.Invoke(writer, [publication, CancellationToken.None])!;
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        Assert.Equal(EvidenceState.Published, (await db.Set<EvidenceObject>().AsNoTracking().SingleAsync()).State);
        Assert.Equal(publication.EventId, (await db.Set<EvidenceBinding>().SingleAsync()).EventId);
    }

    internal static byte[] Photo()
    {
        using var bitmap = new SKBitmap(800, 600);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}
