using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Attendance.Recording;
using TPR10.Api.Data;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Scopes;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class CaptureChallengeTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(1199999990L, 200)]
    [InlineData(1200000000L, 400)]
    [InlineData(1200000010L, 400)]
    public async Task ChallengeExpiresAt120Seconds(long elapsedTicks, int expected)
    {
        await using var f = await CaptureFixture.CreateAsync(postgres.ConnectionString);
        var key = Guid.NewGuid(); var first = await f.Issue(key);
        Assert.Equal(201, first.Status);
        Assert.Equal(AttendanceRecordingFixture.Start.AddSeconds(120), first.Value!.ExpiresAtUtc);
        f.Clock.Advance(TimeSpan.FromTicks(elapsedTicks));
        var retry = await f.Issue(key);
        Assert.Equal(expected, retry.Status);
        if (expected == 200) { Assert.Equal(first.Value.Id, retry.Value!.Id); Assert.Equal(first.Value.ExpiresAtUtc, retry.Value.ExpiresAtUtc); }
        await using var db = f.Data.Database.CreateContext();
        Assert.Single(await db.Set<CaptureChallenge>().ToArrayAsync());
    }

    [Fact]
    public async Task ChallengeBindsSessionActionAndExactSite()
    {
        await using var f = await CaptureFixture.CreateAsync(postgres.ConnectionString);
        var key = Guid.NewGuid(); var result = await f.Issue(key, EvidenceAction.CheckOut);
        Assert.Equal(201, result.Status);
        await using var db = f.Data.Database.CreateContext();
        var row = await db.Set<CaptureChallenge>().SingleAsync();
        Assert.Equal(CaptureFixture.Employee, row.ActorId); Assert.Equal(CaptureFixture.Session, row.SessionId);
        Assert.Equal(EvidenceAction.CheckOut, row.Action); Assert.Equal(key, row.IdempotencyKey);
        Assert.Equal(CaptureFixture.Site, new ScopeKey(row.WorkspaceId, row.ProjectId, row.SiteId));
        Assert.Null(row.ConsumedOperationId);
        Assert.Empty(await db.Set<AttendanceEvent>().ToArrayAsync());
    }

    [Theory]
    [InlineData("workspace", 400)]
    [InlineData("project", 400)]
    [InlineData("empty-key", 400)]
    [InlineData("action", 400)]
    [InlineData("sibling", 404)]
    [InlineData("parent-grant", 404)]
    [InlineData("capability", 403)]
    [InlineData("membership", 403)]
    [InlineData("inactive-site", 404)]
    [InlineData("anonymous", 401)]
    [InlineData("revoked", 401)]
    public async Task InvalidOrUnauthorizedChallengeCreatesNothing(string fault, int status)
    {
        await using var f = await CaptureFixture.CreateAsync(postgres.ConnectionString);
        var site = CaptureFixture.Site; var key = Guid.NewGuid(); var action = EvidenceAction.CheckIn;
        await using var db = f.Data.Database.CreateContext();
        if (fault == "workspace") site = new(site.WorkspaceId);
        if (fault == "project") site = site with { SiteId = null };
        if (fault == "empty-key") key = Guid.Empty;
        if (fault == "action") action = (EvidenceAction)99;
        if (fault == "sibling") site = site with { SiteId = Guid.NewGuid() };
        if (fault == "parent-grant") await db.Database.ExecuteSqlRawAsync("UPDATE user_scope_assignments SET site_id=NULL,version=version+1");
        if (fault == "capability") await db.Database.ExecuteSqlRawAsync("DELETE FROM role_permissions WHERE permission_id='20000000-0000-0000-0000-000000000013'");
        if (fault == "membership") await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE employee_memberships SET valid_to_utc={f.Clock.GetUtcNow()},ended_by={CaptureFixture.Employee},version=version+1");
        if (fault == "inactive-site") await db.Database.ExecuteSqlRawAsync("UPDATE sites SET is_active=false,version=version+1");
        if (fault == "revoked") await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sessions SET revoked_at_utc={f.Clock.GetUtcNow()}");
        await using var scope = await f.ScopeAsync();
        if (fault == "anonymous") scope.ServiceProvider.GetRequiredService<RequestSession>().Entity = null;
        var result = await CaptureFixture.Call<RecordingResult<CaptureChallengeView>>(scope.ServiceProvider, "CaptureChallengeService", "IssueAsync",
            new CaptureChallengeRequest(action, site, key), CancellationToken.None);
        Assert.Equal(status, result.Status);
        Assert.Empty(await db.Set<CaptureChallenge>().ToArrayAsync());
    }

    [Theory]
    [InlineData(-600000000L, true)]
    [InlineData(-600000010L, false)]
    [InlineData(0L, true)]
    [InlineData(10L, false)]
    public void GpsAge60SecondsAcceptedButOlderRejected(long delta, bool valid)
    {
        var now = AttendanceRecordingFixture.Start;
        var metadata = ValidMetadata(now) with { Gps = new(13, 100, 10000, now.AddTicks(delta)) };
        Assert.Equal(valid, Validate(metadata, now) is null); // Low accuracy warns in UI, never geofences.
    }

    [Theory]
    [InlineData(double.NaN, 100, 10)]
    [InlineData(13, double.PositiveInfinity, 10)]
    [InlineData(13, 100, double.NaN)]
    [InlineData(13, 100, double.PositiveInfinity)]
    [InlineData(-91, 100, 10)]
    [InlineData(91, 100, 10)]
    [InlineData(13, -181, 10)]
    [InlineData(13, 181, 10)]
    [InlineData(13, 100, 0)]
    [InlineData(13, 100, -1)]
    public void FutureOrNonfiniteGpsRejected(double lat, double lon, double accuracy)
    {
        var now = AttendanceRecordingFixture.Start;
        Assert.NotNull(Validate(ValidMetadata(now) with { Gps = new(lat, lon, accuracy, now) }, now));
    }

    internal static CaptureMetadata ValidMetadata(DateTimeOffset now) => new(Guid.NewGuid(), Guid.NewGuid(), CaptureFixture.Site,
        now, new(13, 100, 10, now), false, null);
    internal static string? Validate(CaptureMetadata metadata, DateTimeOffset now) =>
        (string?)CaptureFixture.ServiceType("CaptureValidation").GetMethod("Validate")!.Invoke(null, [metadata, now]);

    [Theory]
    [InlineData(false, 404)]
    [InlineData(true, 401)]
    public async Task RevocationWhileWaitingForIdentityLockIsRechecked(bool revokeSession, int expected)
    {
        await using var f = await CaptureFixture.CreateAsync(postgres.ConnectionString);
        await using var gate = f.Data.Database.CreateContext();
        await using var tx = await ScopeOperation.BeginAsync(gate, default);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = f.Issue(Guid.NewGuid());
        try
        {
            await f.WaitForIdentityWaiter(deadline.Token);
            if (revokeSession) await gate.Database.ExecuteSqlInterpolatedAsync($"UPDATE sessions SET revoked_at_utc={f.Clock.GetUtcNow()}");
            else await gate.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE user_scope_assignments SET revoked_at_utc={f.Clock.GetUtcNow()},revoked_by={CaptureFixture.Employee},revocation_reason='ทดสอบ',version=version+1
                """);
        }
        finally { await tx.CommitAsync(); }
        Assert.Equal(expected, (await pending.WaitAsync(deadline.Token)).Status);
        Assert.Empty(await gate.Set<CaptureChallenge>().ToArrayAsync());
    }

    [Fact]
    public async Task ConcurrentIssueWithSameKeyCreatesOneChallengeAndOneAudit()
    {
        await using var f = await CaptureFixture.CreateAsync(postgres.ConnectionString);
        var key = Guid.NewGuid();
        var results = await Task.WhenAll(f.Issue(key), f.Issue(key));
        Assert.Equal(new[] { 200, 201 }, results.Select(x => x.Status).Order());
        Assert.Equal(results[0].Value!.Id, results[1].Value!.Id);
        await using var db = f.Data.Database.CreateContext();
        Assert.Single(await db.Set<CaptureChallenge>().ToArrayAsync());
        Assert.Single(await db.AuditEvents.Where(x => x.EventType == "attendance.capture.challenge").ToArrayAsync());
    }

    [Fact]
    public async Task AuditFailureRollsBackChallenge()
    {
        await using var f = await CaptureFixture.CreateAsync(postgres.ConnectionString);
        await using var db = f.Data.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_capture_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'test audit failure'; END $$;
            CREATE TRIGGER fail_capture_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION fail_capture_audit();
            """);
        Assert.Equal(503, (await f.Issue(Guid.NewGuid())).Status);
        Assert.Empty(await db.Set<CaptureChallenge>().ToArrayAsync());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("gps")]
    [InlineData("site")]
    [InlineData("challenge")]
    [InlineData("key")]
    [InlineData("version")]
    [InlineData("future-device")]
    [InlineData("min-device")]
    [InlineData("max-device")]
    public void MalformedCaptureIsRejected(string fault)
    {
        var now = AttendanceRecordingFixture.Start;
        var metadata = ValidMetadata(now);
        metadata = fault switch
        {
            "null" => null!,
            "gps" => metadata with { Gps = null! },
            "site" => metadata with { Site = null! },
            "challenge" => metadata with { ChallengeId = Guid.Empty },
            "key" => metadata with { IdempotencyKey = Guid.Empty },
            "version" => metadata with { ExpectedDayVersion = 0 },
            "future-device" => metadata with { DeviceCapturedAtUtc = now.AddTicks(10) },
            "min-device" => metadata with { DeviceCapturedAtUtc = DateTimeOffset.MinValue },
            _ => metadata with { DeviceCapturedAtUtc = DateTimeOffset.MaxValue }
        };
        Assert.NotNull(Validate(metadata, now));
    }

    [Theory]
    [InlineData(-90, -180)]
    [InlineData(90, 180)]
    public void InclusiveGpsBoundsAreAllowed(double lat, double lon)
    {
        var now = AttendanceRecordingFixture.Start;
        Assert.Null(Validate(ValidMetadata(now) with { Gps = new(lat, lon, 10000, now) }, now));
    }
}
