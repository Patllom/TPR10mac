using System.Runtime.Versioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Attendance.Storage;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Attendance.Recording;
using TPR10.Api.Auditing;

namespace TPR10.Api.IntegrationTests;

[Collection("database"), UnsupportedOSPlatform("windows")]
public sealed class AttendanceRecordingFailureTests(PostgresFixture postgres)
{
    [Fact]
    public async Task AuditFailurePublishesNothing()
    {
        await using var f = await OnlineRecordingFixture.CreateAsync(postgres.ConnectionString);
        await using var db = f.Capture.Data.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_record_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
              IF NEW.event_type='attendance.record.commit' THEN RAISE EXCEPTION 'test failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER fail_record_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION fail_record_audit();
            """);
        var result = await f.Record(EvidenceAction.CheckIn, await f.Capture.Metadata());
        Assert.Equal(503, result.Status); await AssertNothingPublished(f);
        Assert.Equal(EvidenceState.Prepared, (await db.Set<EvidenceObject>().SingleAsync()).State);
        await using var scope = await f.Capture.ScopeAsync();
        var reader = scope.ServiceProvider.GetRequiredService<EvidenceReader>();
        foreach (var variant in new[] { "full", "thumbnail" })
        {
            var denied = await reader.ReadAsync((await db.Set<EvidenceObject>().SingleAsync()).Id, variant, false, default);
            Assert.Equal(404, Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(denied).StatusCode);
        }
    }
    [Theory]
    [InlineData(60, 409)]
    [InlineData(120, 409)]
    public async Task ExpiredLeaseCannotCommit(int seconds, int expected)
    {
        await using var f = await OnlineRecordingFixture.CreateAsync(postgres.ConnectionString);
        f.Images.Before = () => { f.Capture.Clock.Advance(TimeSpan.FromSeconds(seconds)); return Task.CompletedTask; };
        var result = await f.Record(EvidenceAction.CheckIn, await f.Capture.Metadata());
        Assert.Equal(expected, result.Status); await AssertNothingPublished(f);
    }
    [Theory]
    [InlineData("session", 401)]
    [InlineData("assignment", 404)]
    public async Task RevocationDuringPreparationPublishesNothing(string target, int status)
    {
        await using var f = await OnlineRecordingFixture.CreateAsync(postgres.ConnectionString);
        f.Images.Before = async () =>
        {
            await using var db = f.Capture.Data.Database.CreateContext();
            if (target == "session") await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sessions SET revoked_at_utc={f.Capture.Clock.GetUtcNow()}");
            else await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE user_scope_assignments SET revoked_at_utc={f.Capture.Clock.GetUtcNow()},revoked_by={CaptureFixture.Employee},revocation_reason='ทดสอบ',version=version+1");
        };
        Assert.Equal(status, (await f.Record(EvidenceAction.CheckIn, await f.Capture.Metadata())).Status);
        await AssertNothingPublished(f);
    }
    [Theory]
    [InlineData("prepare")]
    [InlineData("commit")]
    public async Task RetryAfterPreparationFailurePreservesPinAndPublishesOnce(string stage)
    {
        await using var f = await OnlineRecordingFixture.CreateAsync(postgres.ConnectionString);
        await using var db = f.Capture.Data.Database.CreateContext();
        // Fault at a real database boundary, not a replacement recorder/publication service.
        var action = stage == "prepare" ? "attendance.evidence.prepare" : "attendance.record.commit";
        // A fixture-local table makes the failure visible to every recorder connection.
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE test_fault(action text NOT NULL)");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO test_fault VALUES({action})");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE OR REPLACE FUNCTION fail_boundary_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
              IF EXISTS(SELECT 1 FROM test_fault WHERE action=NEW.event_type) THEN RAISE EXCEPTION 'test failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER fail_boundary_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION fail_boundary_audit();
            """);
        var m = await f.Capture.Metadata(); var original = f.Capture.Clock.GetUtcNow();
        Assert.Equal(503, (await f.Record(EvidenceAction.CheckIn, m)).Status); await AssertNothingPublished(f);
        var reserved = await db.Set<EvidenceObject>().AsNoTracking().SingleAsync();
        Assert.Equal(stage == "prepare" ? EvidenceState.Reserved : EvidenceState.Prepared, reserved.State);
        Assert.NotEmpty(System.IO.Directory.GetFiles(f.Storage.Root, "*.jpg", SearchOption.AllDirectories));
        await db.Database.ExecuteSqlRawAsync("DELETE FROM test_fault");
        f.Capture.Clock.Advance(TimeSpan.FromSeconds(61)); await f.RefreshAsync();
        var result = await f.Record(EvidenceAction.CheckIn, m);
        Assert.Equal(201, result.Status); Assert.Equal(original, result.Value!.OccurredAtUtc); Assert.Equal(reserved.Id, result.Value.EvidenceId);
        await AttendanceRecordingTests.AssertCommitted(f, 1);
    }
    [Fact]
    public async Task StorageOutageCannotPublishOrSilentlySwitchDestination()
    {
        await using var f = await OnlineRecordingFixture.CreateAsync(postgres.ConnectionString);
        var root = f.Storage.Settings["AttendanceStorage:Locations:0:RootPath"]!;
        System.IO.Directory.Move(root, root + "-offline");
        var m = await f.Capture.Metadata();
        try { Assert.Equal(503, (await f.Record(EvidenceAction.CheckIn, m)).Status); await AssertNothingPublished(f); }
        finally { System.IO.Directory.Move(root + "-offline", root); }
        await using var db = f.Capture.Data.Database.CreateContext();
        Assert.Equal(f.Location.Id, (await db.Set<EvidenceObject>().SingleAsync()).StorageId);
        f.Capture.Clock.Advance(TimeSpan.FromSeconds(61)); await f.RefreshAsync();
        Assert.Equal(201, (await f.Record(EvidenceAction.CheckIn, m)).Status);
        await AttendanceRecordingTests.AssertCommitted(f, 1);
    }
    [Fact]
    public async Task MembershipTransferDuringPreparationKeepsOriginalSnapshot()
    {
        await using var f = await OnlineRecordingFixture.CreateAsync(postgres.ConnectionString);
        f.Images.Before = async () =>
        {
            f.Capture.Clock.Advance(TimeSpan.FromSeconds(1));
            await using var db = f.Capture.Data.Database.CreateContext();
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE employee_memberships SET valid_to_utc='2026-09-25T17:00:01Z',version=version+1 WHERE user_id='00000000-0000-0000-0000-000000000031';
                INSERT INTO employee_memberships(id,user_id,workspace_id,department_id,valid_from_utc,created_by,reason)
                VALUES (gen_random_uuid(),'00000000-0000-0000-0000-000000000031','00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000012',
                  '2026-09-25T17:00:01Z','00000000-0000-0000-0000-000000000031','ทดสอบย้ายสังกัด');
                """);
        };
        Assert.Equal(201, (await f.Record(EvidenceAction.CheckIn, await f.Capture.Metadata())).Status);
        await using var db = f.Capture.Data.Database.CreateContext();
        var day = await db.Set<AttendanceDay>().SingleAsync(); var image = await db.Set<EvidenceObject>().SingleAsync();
        Assert.Equal(CaptureFixture.Site.WorkspaceId, day.WorkspaceId); Assert.Equal(day.MembershipId, image.MembershipId);
        Assert.Equal(DateTimeOffset.Parse("2026-09-25T17:00:00Z"), day.SnapshotAtUtc);
    }
    [Theory]
    [InlineData("oversize", 413)]
    [InlineData("invalid", 415)]
    public async Task InvalidImageNeverPublishes(string kind, int status)
    {
        await using var f = await OnlineRecordingFixture.CreateAsync(postgres.ConnectionString);
        var bytes = kind == "oversize" ? new byte[ImageLimits.MaxBytes + 1] : new byte[] { 1, 2, 3 };
        Assert.Equal(status, (await f.Record(EvidenceAction.CheckIn, await f.Capture.Metadata(), bytes)).Status);
        await AssertNothingPublished(f);
    }
    [Theory]
    [InlineData(61, 409)]
    [InlineData(120, 400)]
    public async Task PreparedImageCannotCommitAfterOperationDeadline(int seconds, int expected)
    {
        await using var f = await OnlineRecordingFixture.CreateAsync(postgres.ConnectionString, services =>
            services.AddScoped<IAuditEventWriter>(s => new DeadlineAudit(s.GetRequiredService<TPR10.Api.Data.Tpr10DbContext>(),
                s.GetRequiredService<TPR10.Api.Correlation.ICorrelationContext>(), s.GetRequiredService<TimeProvider>(), seconds)));
        Assert.Equal(expected, (await f.Record(EvidenceAction.CheckIn, await f.Capture.Metadata())).Status);
        await AssertNothingPublished(f);
        await using var db = f.Capture.Data.Database.CreateContext();
        Assert.Equal(EvidenceState.Prepared, (await db.Set<EvidenceObject>().SingleAsync()).State);
    }
    private sealed class DeadlineAudit(TPR10.Api.Data.Tpr10DbContext db, TPR10.Api.Correlation.ICorrelationContext correlation,
        TimeProvider clock, int seconds) : IAuditEventWriter
    {
        private readonly AuditEventWriter inner = new(db, correlation, clock);
        public Task WriteAsync(string eventType, Guid targetId, IReadOnlyDictionary<string, string> metadata, CancellationToken ct, Guid? actorId = null) =>
            inner.WriteAsync(eventType, targetId, metadata, ct, actorId);
        public async Task WriteAsync(SecurityAuditRequest request, CancellationToken ct)
        {
            await inner.WriteAsync(request, ct);
            if (request.Action == "attendance.evidence.prepare") ((ManualTimeProvider)clock).Advance(TimeSpan.FromSeconds(seconds));
        }
    }
    internal static async Task AssertNothingPublished(OnlineRecordingFixture f)
    {
        await using var db = f.Capture.Data.Database.CreateContext();
        Assert.Empty(await db.Set<AttendanceDay>().ToArrayAsync()); Assert.Empty(await db.Set<AttendanceEvent>().ToArrayAsync());
        Assert.Empty(await db.Set<EvidenceBinding>().ToArrayAsync());
        Assert.False(await db.Set<EvidenceObject>().AnyAsync(x => x.State == EvidenceState.Published));
        Assert.False(await db.Set<AttendanceOperation>().AnyAsync(x => x.State == AttendanceOperationState.Committed));
        Assert.False(await db.Set<CaptureChallenge>().AnyAsync(x => x.ConsumedOperationId != null));
        Assert.False(await db.AuditEvents.AnyAsync(x => x.EventType == "attendance.record.commit"));
    }
}
