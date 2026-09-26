using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Attendance.Recording;
using TPR10.Api.Data;
using TPR10.Api.Scopes;
using System.Text.Json;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class CaptureOperationTests(PostgresFixture postgres)
{
    internal static readonly string Hash = new('a', 64);
    [Fact]
    public async Task SameKeyDifferentImageConflicts()
    {
        await using var f = await CaptureFixture.CreateAsync(postgres.ConnectionString);
        var metadata = await f.Metadata();
        Assert.Equal(201, (await f.Claim(metadata, Hash)).Status);
        Assert.Equal(409, (await f.Claim(metadata, new string('b', 64))).Status);
        await using var db = f.Data.Database.CreateContext();
        Assert.Single(await db.Set<AttendanceOperation>().ToArrayAsync());
        Assert.Empty(await db.Set<AttendanceEvent>().ToArrayAsync());
    }

    [Theory]
    [InlineData(599999990L, 409)]
    [InlineData(600000000L, 201)]
    [InlineData(600000010L, 201)]
    public async Task MatchingRetryReclaimsExpiredLeaseWithoutChangingOriginalPin(long elapsed, int status)
    {
        await using var f = await CaptureFixture.CreateAsync(postgres.ConnectionString);
        var metadata = await f.Metadata();
        var original = (await f.Claim(metadata, Hash)).Value!;
        f.Clock.Advance(TimeSpan.FromTicks(elapsed));
        var retry = await f.Claim(metadata, Hash);
        Assert.Equal(status, retry.Status);
        if (status == 409) Assert.Equal(2, retry.RetryAfterSeconds);
        else
        {
            Assert.Equal(original.Id, retry.Value!.Id); Assert.Equal(original.TargetDayId, retry.Value.TargetDayId);
            Assert.Equal(original.OccurredAtUtc, retry.Value.OccurredAtUtc); Assert.Equal(2, retry.Value.FencingVersion);
            Assert.Equal(f.Clock.GetUtcNow().AddSeconds(60), retry.Value.LeaseUntilUtc);
            await using var scope = await f.ScopeAsync();
            var db = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>();
            await using var tx = await ScopeOperation.BeginAsync(db, default);
            var stale = await CaptureFixture.Call<RecordingResult<AttendanceOperation>>(scope.ServiceProvider, "CaptureOperationStore", "ValidateForCommitAsync",
                original.Id, 1L, CancellationToken.None);
            Assert.Equal(409, stale.Status);
        }
    }

    [Fact]
    public async Task ChallengeExpiryPreventsReclaimAndAppearsExpiredInStatus()
    {
        await using var f = await CaptureFixture.CreateAsync(postgres.ConnectionString);
        var metadata = await f.Metadata();
        Assert.Equal(201, (await f.Claim(metadata, Hash)).Status);
        f.Clock.Advance(TimeSpan.FromSeconds(120));
        Assert.Equal(400, (await f.Claim(metadata, Hash)).Status);
        var result = await f.Get(metadata.IdempotencyKey);
        Assert.Equal(200, result.Status); Assert.Equal("Expired", result.Value!.State); Assert.Null(result.Value.Receipt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OtherActorCannotReadOperation(bool sameActorOtherSession)
    {
        await using var f = await CaptureFixture.CreateAsync(postgres.ConnectionString);
        var metadata = await f.Metadata(); await f.Claim(metadata, Hash);
        await using var db = f.Data.Database.CreateContext();
        var session = Guid.NewGuid();
        var actor = sameActorOtherSession ? CaptureFixture.Employee : Guid.Parse("00000000-0000-0000-0000-000000000032");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO sessions(id,user_id,token_hash,stage,created_at_utc,last_seen_at_utc,expires_at_utc,security_version)
            VALUES({session},{actor},decode(repeat('cd',32),'hex'),'Active',{f.Clock.GetUtcNow()},{f.Clock.GetUtcNow()},{f.Clock.GetUtcNow().AddHours(1)},1)
            """);
        var result = await f.Get(metadata.IdempotencyKey, session: session);
        Assert.Equal(404, result.Status); Assert.Null(result.Value);
        Assert.Equal(404, (await f.Get(metadata.IdempotencyKey, EvidenceAction.CheckOut)).Status);
    }

    [Fact]
    public void DigestNormalizesUtcOffsetsButIncludesEveryPayloadField()
    {
        var metadata = CaptureChallengeTests.ValidMetadata(AttendanceRecordingFixture.Start);
        var type = CaptureFixture.ServiceType("CaptureOperationStore");
        string Digest(CaptureMetadata m, string hash) => (string)type.GetMethod("ComputeDigest")!.Invoke(null, [m, hash])!;
        var original = Digest(metadata, Hash);
        Assert.Equal(original, Digest(metadata with
        {
            DeviceCapturedAtUtc = metadata.DeviceCapturedAtUtc.ToOffset(TimeSpan.FromHours(7)),
            Gps = metadata.Gps with { SampledAtUtc = metadata.Gps.SampledAtUtc.ToOffset(TimeSpan.FromHours(7)) }
        }, Hash.ToUpperInvariant()));
        foreach (var changed in new[] { metadata with { ChallengeId = Guid.NewGuid() }, metadata with { IdempotencyKey = Guid.NewGuid() },
            metadata with { Site = metadata.Site with { SiteId = Guid.NewGuid() } }, metadata with { DeviceCapturedAtUtc = metadata.DeviceCapturedAtUtc.AddTicks(-10) },
            metadata with { Gps = metadata.Gps with { Latitude = 14 } }, metadata with { Gps = metadata.Gps with { Longitude = 101 } },
            metadata with { Gps = metadata.Gps with { AccuracyMeters = 11 } }, metadata with { Gps = metadata.Gps with { SampledAtUtc = metadata.Gps.SampledAtUtc.AddTicks(-10) } },
            metadata with { ConfirmContinuousWork = true }, metadata with { ExpectedDayVersion = 1 } })
            Assert.NotEqual(original, Digest(changed, Hash));
        Assert.NotEqual(original, Digest(metadata, new string('b', 64)));
    }

    [Theory]
    [InlineData("gps")]
    [InlineData("time")]
    [InlineData("confirmation")]
    [InlineData("version")]
    public async Task SameKeyChangedMetadataConflicts(string field)
    {
        await using var f = await CaptureFixture.CreateAsync(postgres.ConnectionString);
        var metadata = await f.Metadata(); await f.Claim(metadata, Hash);
        var changed = field switch
        {
            "gps" => metadata with { Gps = metadata.Gps with { AccuracyMeters = 12 } },
            "time" => metadata with { DeviceCapturedAtUtc = metadata.DeviceCapturedAtUtc.AddTicks(-10) },
            "confirmation" => metadata with { ConfirmContinuousWork = true },
            _ => metadata with { ExpectedDayVersion = 1 }
        };
        Assert.Equal(409, (await f.Claim(changed, Hash)).Status);
    }

    [Theory]
    [InlineData(0L, 200)]
    [InlineData(599999990L, 200)]
    [InlineData(600000000L, 409)]
    [InlineData(1200000000L, 400)]
    public async Task FinalValidationChecksFenceLeaseAndChallenge(long ticks, int expected)
    {
        await using var f = await CaptureFixture.CreateAsync(postgres.ConnectionString);
        var metadata = await f.Metadata(); var op = (await f.Claim(metadata, Hash)).Value!;
        f.Clock.Advance(TimeSpan.FromTicks(ticks));
        Assert.Equal(expected, (await f.ValidateCommit(op.Id, 1)).Status);
        await using var db = f.Data.Database.CreateContext();
        Assert.Empty(await db.Set<AttendanceEvent>().ToArrayAsync());
        Assert.Null((await db.Set<CaptureChallenge>().SingleAsync()).ConsumedOperationId);
    }

    [Theory]
    [InlineData(false, 404)]
    [InlineData(true, 401)]
    public async Task CommitValidationRechecksRevocationAfterWaiting(bool revokeSession, int expected)
    {
        await using var f = await CaptureFixture.CreateAsync(postgres.ConnectionString);
        var metadata = await f.Metadata(); var op = (await f.Claim(metadata, Hash)).Value!;
        await using var db = f.Data.Database.CreateContext();
        await using var tx = await ScopeOperation.BeginAsync(db, default);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = f.ValidateCommit(op.Id, op.FencingVersion);
        try
        {
            await f.WaitForIdentityWaiter(deadline.Token);
            if (revokeSession) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sessions SET revoked_at_utc={f.Clock.GetUtcNow()}");
            else await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE user_scope_assignments SET revoked_at_utc={f.Clock.GetUtcNow()},revoked_by={CaptureFixture.Employee},revocation_reason='ทดสอบ',version=version+1
                """);
        }
        finally { await tx.CommitAsync(); }
        Assert.Equal(expected, (await pending.WaitAsync(deadline.Token)).Status);
    }

    [Fact]
    public async Task ClaimIsRolledBackWhenCallerDoesNotCommit()
    {
        await using var f = await CaptureFixture.CreateAsync(postgres.ConnectionString);
        var metadata = await f.Metadata();
        await using (var scope = await f.ScopeAsync())
        {
            var db = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>();
            await using var tx = await ScopeOperation.BeginAsync(db, default);
            var result = await CaptureFixture.Call<RecordingResult<AttendanceOperation>>(scope.ServiceProvider, "CaptureOperationStore", "ClaimAsync",
                EvidenceAction.CheckIn, metadata, Hash, f.Subject, Guid.NewGuid(), CancellationToken.None);
            Assert.Equal(201, result.Status); await db.SaveChangesAsync();
        }
        await using var observer = f.Data.Database.CreateContext();
        Assert.Empty(await observer.Set<AttendanceOperation>().ToArrayAsync());
        Assert.Equal(404, (await f.Get(metadata.IdempotencyKey)).Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    [InlineData(120)]
    public async Task StatusProjectionDoesNotMutateOperation(int seconds)
    {
        await using var f = await CaptureFixture.CreateAsync(postgres.ConnectionString);
        var metadata = await f.Metadata(); await f.Claim(metadata, Hash);
        f.Clock.Advance(TimeSpan.FromSeconds(seconds));
        var result = await f.Get(metadata.IdempotencyKey);
        Assert.Equal(200, result.Status); Assert.Null(result.Value!.Receipt);
        Assert.Equal(seconds < 120 ? "Processing" : "Expired", result.Value.State);
        await using var db = f.Data.Database.CreateContext();
        var op = await db.Set<AttendanceOperation>().SingleAsync();
        Assert.Equal(AttendanceOperationState.Processing, op.State); Assert.Equal(1, op.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedReceiptSurvivesChallengeExpiryButMalformedResultFailsClosed(bool malformed)
    {
        await using var f = await CaptureFixture.CreateAsync(postgres.ConnectionString);
        var metadata = await f.Metadata();
        var day = await f.Data.CreateOpenDayAsync(CaptureFixture.Employee, f.Clock.GetUtcNow());
        var op = (await f.Claim(metadata, Hash, day: day)).Value!;
        var eventId = await f.Data.AddEventAsync(day);
        await using var db = f.Data.Database.CreateContext();
        var ev = await db.Set<AttendanceEvent>().SingleAsync();
        var receipt = new CaptureReceipt(op.Id, day, eventId, new DateOnly(2026, 9, 26), op.OccurredAtUtc, ev.EvidenceId, 1);
        var json = malformed ? "{}" : JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE attendance_operations SET state='Committed',result_event_id={eventId},result_json={json}::jsonb,version=version+1 WHERE id={op.Id}
            """);
        f.Clock.Advance(TimeSpan.FromMinutes(3));
        var result = await f.Get(metadata.IdempotencyKey);
        Assert.Equal(malformed ? 503 : 200, result.Status);
        if (!malformed) { Assert.Equal("Committed", result.Value!.State); Assert.Equal(receipt, result.Value.Receipt); }
        else Assert.Null(result.Value);
        Assert.Equal(malformed ? 503 : 200, (await f.Claim(metadata, Hash)).Status);
    }

    [Fact]
    public async Task RetainedContextCannotReclaimAReplacementWorkersLiveLease()
    {
        await using var f = await CaptureFixture.CreateAsync(postgres.ConnectionString);
        var metadata = await f.Metadata();
        await using var scope = await f.ScopeAsync();
        var db = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>();
        async Task<RecordingResult<AttendanceOperation>> Claim() =>
            await CaptureFixture.Call<RecordingResult<AttendanceOperation>>(scope.ServiceProvider, "CaptureOperationStore", "ClaimAsync",
                EvidenceAction.CheckIn, metadata, Hash, f.Subject, Guid.NewGuid(), CancellationToken.None);
        await using (var initial = await ScopeOperation.BeginAsync(db, default))
        {
            Assert.Equal(201, (await Claim()).Status); await db.SaveChangesAsync(); await initial.CommitAsync();
        }
        f.Clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(201, (await f.Claim(metadata, Hash)).Status);
        await using var retry = await ScopeOperation.BeginAsync(db, default);
        Assert.Equal(409, (await Claim()).Status);
    }
}
