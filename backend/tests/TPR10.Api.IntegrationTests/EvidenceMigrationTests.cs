using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Attendance.Storage;
using TPR10.Api.Data;
using TPR10.Api.Scopes;

namespace TPR10.Api.IntegrationTests;

[Collection("database"), UnsupportedOSPlatform("windows")]
public sealed class EvidenceMigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Start_seals_source_and_records_both_variants_idempotently_without_changing_evidence()
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        var request = await RequestAsync(f);
        using var response = await f.D.PostAsync(Route, request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = (await response.Content.ReadFromJsonAsync<MigrationView>())!;
        using var repeated = await f.D.PostAsync(Route, request);
        Assert.Equal(HttpStatusCode.Accepted, repeated.StatusCode);
        Assert.Equal(job.Id, (await repeated.Content.ReadFromJsonAsync<MigrationView>())!.Id);
        using var different = await f.D.PostAsync(Route, request with { Reason = "อีกเหตุผล" });
        Assert.Equal(HttpStatusCode.Conflict, different.StatusCode);
        using var duplicate = await f.D.PostAsync(Route, request with { RequestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        await using var db = f.D.Database.CreateContext();
        Assert.False((await db.Set<StorageLocation>().SingleAsync(x => x.Id == request.SourceId)).AcceptWrites);
        Assert.Equal(2, await db.Set<MigrationItem>().CountAsync(x => x.JobId == job.Id));
        Assert.Equal(2, await db.Set<EvidenceLocation>().CountAsync(x => x.State == CopyState.Active));
        Assert.Equal(1, await db.AuditEvents.CountAsync(x => x.EventType == "attendance.storage.migration-start"));
    }

    internal const string Route = StorageTestFixture.ApiRoot + "/migrations";
    [Theory]
    [InlineData("Published")]
    [InlineData("Prepared")]
    public async Task Migration_preserves_bytes_identity_and_stamp_and_late_publication_keeps_new_active_copy(string state)
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString, state);
        var request = await RequestAsync(f);
        var job = await StartAsync(f, request);
        await using var db = f.D.Database.CreateContext();
        var original = await db.Set<EvidenceObject>().AsNoTracking().SingleAsync();
        var binding = await db.Set<EvidenceBinding>().AsNoTracking().SingleOrDefaultAsync();
        Assert.Equal(2, await RunAsync(f, job.Id));
        Assert.Equal(0, await RunAsync(f, job.Id));
        var after = await db.Set<EvidenceObject>().AsNoTracking().SingleAsync();
        Assert.Equal(original.Id, after.Id); Assert.Equal(original.Sha256, after.Sha256);
        Assert.Equal(original.ThumbnailSha256, after.ThumbnailSha256); Assert.Equal(original.OccurredAtUtc, after.OccurredAtUtc);
        Assert.Equal(original.Action, after.Action); Assert.Equal(original.State, after.State);
        Assert.Equal("Completed", (await db.Set<MigrationJob>().SingleAsync()).Status);
        Assert.Equal(2, await db.Set<MigrationItem>().CountAsync());
        Assert.Equal(2, await db.Set<EvidenceLocation>().CountAsync(x => x.StorageId == request.SourceId && x.State == CopyState.Fallback));
        Assert.Equal(2, await db.Set<EvidenceLocation>().CountAsync(x => x.StorageId == request.TargetId && x.State == CopyState.Active));
        foreach (var copy in await db.Set<EvidenceLocation>().Where(x => x.StorageId == request.SourceId).ToArrayAsync())
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(f.Storage.Root, "volume-0", copy.ObjectKey)),
                await File.ReadAllBytesAsync(Path.Combine(f.Storage.Root, "volume-1", copy.ObjectKey)));
        if (state == "Prepared")
        {
            using var scope = f.D.Factory.Services.CreateScope();
            await EvidenceRevocationTests.SetSession(scope.ServiceProvider, f.Subject.EmployeeId);
            var scopedDb = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>();
            await using var tx = await ScopeOperation.BeginAsync(scopedDb, default);
            await scope.ServiceProvider.GetRequiredService<EvidenceWriter>().StagePublicationAsync(new(f.Pin.EvidenceId, f.Pin.OperationId, Guid.NewGuid(), f.Subject,
                new(f.Subject.OccurredAtUtc, EvidenceAction.CheckIn)), default);
            await scopedDb.SaveChangesAsync(); await tx.CommitAsync();
        }
        else Assert.Equal(binding!.EventId, (await db.Set<EvidenceBinding>().SingleAsync()).EventId);
        Assert.Equal(2, await db.Set<EvidenceLocation>().CountAsync(x => x.StorageId == request.TargetId && x.State == CopyState.Active));
        using var read = await f.Owner.GetAsync(f.Url);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task Corrupt_destination_blocks_without_overwriting_or_retrying()
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        var request = await RequestAsync(f);
        var job = await StartAsync(f, request);
        await using var db = f.D.Database.CreateContext();
        var target = await db.Set<StorageLocation>().SingleAsync(x => x.Id == request.TargetId);
        var key = StorageObjectKey.Create(f.Pin.EvidenceId, "full");
        await f.D.Factory.Services.GetRequiredService<StorageRuntime>().Resolve(target).WriteImmutableAsync(key, new byte[] { 1, 2, 3 }, default);
        await RunAsync(f, job.Id);
        Assert.Equal("Blocked", (await db.Set<MigrationJob>().SingleAsync()).Status);
        Assert.Equal(CopyState.Active, (await db.Set<EvidenceLocation>().SingleAsync(x => x.StorageId == request.SourceId && x.Variant == "full")).State);
        Assert.Equal("checksum-mismatch", (await db.Set<MigrationItem>().SingleAsync(x => x.Variant == "full")).ErrorCode);
        var attempts = await db.Set<MigrationItem>().SumAsync(x => x.Attempts);
        f.D.Advance(TimeSpan.FromHours(1)); await RunAsync(f, job.Id);
        Assert.Equal(attempts, await db.Set<MigrationItem>().SumAsync(x => x.Attempts));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(f.Storage.Root, "volume-1", key)));
    }

    [Fact]
    public async Task Durable_worker_survives_web_session_expiry()
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        var job = await StartAsync(f, await RequestAsync(f));
        f.D.Advance(TimeSpan.FromHours(1));
        Assert.Equal(2, await RunAsync(f, job.Id));
        await using var db = f.D.Database.CreateContext();
        Assert.Equal("Completed", (await db.Set<MigrationJob>().SingleAsync()).Status);
    }

    internal static async Task<MigrationView> StartAsync(EvidenceFixture f, StartMigration request)
    {
        using var response = await f.D.PostAsync(Route, request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MigrationView>())!;
    }

    internal static async Task<int> RunAsync(EvidenceFixture f, Guid id, int limit = 100)
    {
        using var scope = f.D.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MigrationWorker>().RunBatchAsync(id, limit, default);
    }
    internal static async Task<StartMigration> RequestAsync(EvidenceFixture f)
    {
        f.D.Advance(TimeSpan.FromMinutes(1)); // A new CSRF issuance window after the authentication fixture.
        await using var db = f.D.Database.CreateContext();
        var old = await db.Set<StorageLocation>().AsNoTracking().SingleAsync(x => x.Id == f.Pin.StorageId);
        var source = await StorageTestFixture.ProbeAsync(f.D, new(old.Id, old.Alias, old.Kind, old.Version, old.AcceptWrites, old.Health, old.CheckedAtUtc));
        var target = await StorageTestFixture.ProbeAsync(f.D, await StorageTestFixture.RegisterAsync(f.D, "local-1"));
        await StoragePinTests.Switch(f.D, target.Id, 2);
        return new(Guid.NewGuid(), source.Id, target.Id, source.Version, target.Version, "ย้ายรูปทดสอบโดยเก็บต้นทาง");
    }
}
