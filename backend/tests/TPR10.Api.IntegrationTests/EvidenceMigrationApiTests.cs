using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TPR10.Api.Attendance.Storage;
using static TPR10.Api.IntegrationTests.EvidenceMigrationTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database"), UnsupportedOSPlatform("windows")]
public sealed class EvidenceMigrationApiTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("same-storage", 400)]
    [InlineData("empty-id", 400)]
    [InlineData("reason", 400)]
    [InlineData("stale-source", 409)]
    [InlineData("stale-target", 409)]
    [InlineData("active-source", 409)]
    [InlineData("stale-probe", 409)]
    [InlineData("missing", 404)]
    [InlineData("mfa", 403)]
    public async Task Start_rejects_invalid_or_stale_requests_without_sealing_or_manifest(string fault, int status)
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        var request = await RequestAsync(f);
        request = fault switch
        {
            "same-storage" => request with { TargetId = request.SourceId },
            "empty-id" => request with { RequestId = Guid.Empty },
            "reason" => request with { Reason = "" },
            "stale-source" => request with { ExpectedSourceVersion = request.ExpectedSourceVersion - 1 },
            "stale-target" => request with { ExpectedTargetVersion = request.ExpectedTargetVersion - 1 },
            "missing" => request with { SourceId = Guid.NewGuid() },
            _ => request
        };
        if (fault == "active-source") await StoragePinTests.Switch(f.D, request.SourceId, 3);
        if (fault == "stale-probe") f.D.Advance(TimeSpan.FromSeconds(60));
        if (fault == "mfa") f.D.Advance(TimeSpan.FromMinutes(16));
        using var response = await f.D.PostAsync(Route, request);
        Assert.Equal(status, (int)response.StatusCode);
        await using var db = f.D.Database.CreateContext();
        Assert.Empty(await db.Set<MigrationJob>().ToArrayAsync());
        Assert.Empty(await db.Set<MigrationItem>().ToArrayAsync());
        Assert.True((await db.Set<StorageLocation>().SingleAsync(x => x.Id == f.Pin.StorageId)).AcceptWrites);
    }

    [Fact]
    public async Task Start_audit_failure_rolls_back_source_seal_job_and_manifest()
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        var request = await RequestAsync(f);
        await using var db = f.D.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_start_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
            IF NEW.event_type='attendance.storage.migration-start' THEN RAISE EXCEPTION 'test audit failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER fail_start_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION fail_start_audit();
            """);
        using var response = await f.D.PostAsync(Route, request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(await db.Set<MigrationJob>().ToArrayAsync());
        Assert.Empty(await db.Set<MigrationItem>().ToArrayAsync());
        Assert.True((await db.Set<StorageLocation>().SingleAsync(x => x.Id == request.SourceId)).AcceptWrites);
    }

    [Fact]
    public async Task Management_routes_require_current_grants_and_reject_unknown_body_fields()
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        var request = await RequestAsync(f);
        var job = await StartAsync(f, request);
        foreach (var path in new[] { Route, Route + "/" + job.Id })
        {
            using var denied = await f.Owner.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            using var allowed = await f.D.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            Assert.DoesNotContain(f.Storage.Root, await allowed.Content.ReadAsStringAsync());
        }
        using var unknown = await f.D.PostAsync(Route, new
        {
            request.RequestId,
            request.SourceId,
            request.TargetId,
            request.ExpectedSourceVersion,
            request.ExpectedTargetVersion,
            request.Reason,
            rootPath = "/unexpected"
        });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        using var invalidPage = await f.D.Client.GetAsync(Route + "?limit=101");
        Assert.Equal(HttpStatusCode.BadRequest, invalidPage.StatusCode);
        var page = await f.D.Client.GetFromJsonAsync<Page<MigrationView>>(Route + "?limit=1");
        Assert.Equal(job.Id, Assert.Single(page!.Items).Id);
    }

    [Fact]
    public async Task Scheduler_processes_persisted_job_only_when_enabled()
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        var job = await StartAsync(f, await RequestAsync(f));
        var scheduler = f.D.Factory.Services.GetRequiredService<MigrationScheduler>();
        var options = f.D.Factory.Services.GetRequiredService<IOptionsMonitor<StorageOptions>>().CurrentValue;
        options.MigrationWorkerEnabled = false;
        await scheduler.ProcessNextAsync(default);
        await using var db = f.D.Database.CreateContext();
        Assert.Equal("Pending", (await db.Set<MigrationJob>().AsNoTracking().SingleAsync()).Status);
        options.MigrationWorkerEnabled = true;
        await scheduler.ProcessNextAsync(default);
        Assert.Equal("Completed", (await db.Set<MigrationJob>().AsNoTracking().SingleAsync()).Status);
        Assert.Equal(job.Id, (await db.Set<MigrationJob>().SingleAsync()).Id);
    }

    [Fact]
    public async Task Resume_requires_fresh_versions_readiness_and_recent_mfa_after_restart()
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString, "Reserved");
        var job = await StartAsync(f, await RequestAsync(f));
        f.D.Advance(TimeSpan.FromSeconds(60)); await RunAsync(f, job.Id);
        await using var db = f.D.Database.CreateContext();
        var blocked = await db.Set<MigrationJob>().AsNoTracking().SingleAsync();
        Assert.Equal("Blocked", blocked.Status);
        using var stale = await f.D.PostAsync(Route + $"/{job.Id}/resume", new ResumeMigration(job.Version, "รุ่นเก่า"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var unknown = await f.D.PostAsync(Route + $"/{job.Id}/resume", new ResumeMigration(blocked.Version, "readiness หลังเริ่มใหม่"));
        Assert.Equal(HttpStatusCode.Conflict, unknown.StatusCode);
        foreach (var location in await db.Set<StorageLocation>().AsNoTracking().ToArrayAsync())
            await StorageTestFixture.ProbeAsync(f.D, new(location.Id, location.Alias, location.Kind, location.Version, location.AcceptWrites, location.Health, location.CheckedAtUtc));
        f.D.Advance(TimeSpan.FromMinutes(16));
        using var expired = await f.D.PostAsync(Route + $"/{job.Id}/resume", new ResumeMigration(blocked.Version, "MFA หมดอายุ"));
        Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);
        Assert.Equal(blocked.Version, (await db.Set<MigrationJob>().AsNoTracking().SingleAsync()).Version);
    }
}
