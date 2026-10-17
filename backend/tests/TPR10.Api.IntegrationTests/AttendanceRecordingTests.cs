using System.Runtime.Versioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Attendance.Recording;
using TPR10.Api.Attendance.Storage;

namespace TPR10.Api.IntegrationTests;

[Collection("database"), UnsupportedOSPlatform("windows")]
public sealed class AttendanceRecordingTests(PostgresFixture postgres)
{
    [Fact]
    public async Task ParallelCheckinsProduceOneDay()
    {
        await using var f = await OnlineRecordingFixture.CreateAsync(postgres.ConnectionString);
        var a = await f.Capture.Metadata(); var b = await f.Capture.Metadata();
        var results = await Task.WhenAll(f.Record(EvidenceAction.CheckIn, a), f.Record(EvidenceAction.CheckIn, b));
        Assert.Equal(new[] { 201, 409 }, results.Select(x => x.Status).Order());
        await AssertCommitted(f, 1);
    }
    [Fact]
    public async Task ParallelCheckoutsProduceOneEvent()
    {
        await using var f = await OnlineRecordingFixture.CreateAsync(postgres.ConnectionString); await f.CheckIn();
        f.Capture.Clock.Advance(TimeSpan.FromSeconds(5));
        var a = await f.Capture.Metadata(EvidenceAction.CheckOut); var b = await f.Capture.Metadata(EvidenceAction.CheckOut);
        var results = await Task.WhenAll(f.Record(EvidenceAction.CheckOut, a), f.Record(EvidenceAction.CheckOut, b));
        Assert.Equal(new[] { 201, 409 }, results.Select(x => x.Status).Order());
        await AssertCommitted(f, 2);
    }
    [Fact]
    public async Task RetryAfterLostResponseReturnsSameReceipt()
    {
        await using var f = await OnlineRecordingFixture.CreateAsync(postgres.ConnectionString);
        var m = await f.Capture.Metadata(); var first = await f.Record(EvidenceAction.CheckIn, m);
        Assert.Equal(201, first.Status); f.Capture.Clock.Advance(TimeSpan.FromMinutes(3));
        var retry = await f.Record(EvidenceAction.CheckIn, m);
        Assert.Equal(200, retry.Status); Assert.Equal(first.Value, retry.Value);
        Assert.Equal(first.Value, (await f.Capture.Get(m.IdempotencyKey)).Value!.Receipt);
        await AssertCommitted(f, 1);
    }
    [Fact]
    public async Task MidnightDuringImagePreparationKeepsOriginalInstant()
    {
        await using var f = await OnlineRecordingFixture.CreateAsync(postgres.ConnectionString);
        f.Capture.Clock.Advance(TimeSpan.FromSeconds(-1)); await f.RefreshAsync();
        var m = await f.Capture.Metadata();
        f.Images.Before = () => { f.Capture.Clock.Advance(TimeSpan.FromSeconds(2)); return Task.CompletedTask; };
        var result = await f.Record(EvidenceAction.CheckIn, m);
        Assert.Equal(201, result.Status); Assert.Equal(new DateOnly(2026, 9, 25), result.Value!.WorkDate);
        Assert.Equal(DateTimeOffset.Parse("2026-09-25T16:59:59Z"), result.Value.OccurredAtUtc);
        await using var db = f.Capture.Data.Database.CreateContext();
        var copy = await db.Set<EvidenceLocation>().SingleAsync(x => x.EvidenceId == result.Value.EvidenceId && x.Variant == "full");
        var bytes = await f.Capture.Services.GetRequiredService<StorageRuntime>().Resolve(f.Location).ReadVerifiedAsync(copy.ObjectKey, copy.Sha256, copy.Length, default);
        var expected = await f.Images.Inner.StampAsync(new MemoryStream(EvidencePublicationTests.Photo()), new(DateTimeOffset.Parse("2026-09-25T16:59:59Z"), EvidenceAction.CheckIn), default);
        Assert.Equal(expected.Jpeg, bytes);
    }
    [Theory]
    [InlineData(false, 409)]
    [InlineData(true, 201)]
    public async Task OvernightNeedsContinuousConfirmation(bool confirm, int expected)
    {
        await using var f = await OnlineRecordingFixture.CreateAsync(postgres.ConnectionString); var first = await f.CheckIn();
        f.Capture.Clock.Advance(TimeSpan.FromDays(1)); await f.RefreshAsync();
        var m = await f.Capture.Metadata(EvidenceAction.CheckOut);
        var result = await f.Record(EvidenceAction.CheckOut, m with { ConfirmContinuousWork = confirm });
        Assert.Equal(expected, result.Status);
        if (confirm)
        {
            Assert.Equal(first.DayId, result.Value!.DayId); Assert.Equal(first.WorkDate, result.Value.WorkDate);
            f.Capture.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.NotEqual(first.DayId, (await f.CheckIn()).DayId);
        }
    }
    [Fact]
    public async Task SameDayClosedPairCannotReopen()
    {
        await using var f = await OnlineRecordingFixture.CreateAsync(postgres.ConnectionString); await f.CheckIn();
        f.Capture.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(201, (await f.Record(EvidenceAction.CheckOut, await f.Capture.Metadata(EvidenceAction.CheckOut))).Status);
        Assert.Equal(409, (await f.Record(EvidenceAction.CheckIn, await f.Capture.Metadata())).Status);
    }
    [Theory]
    [InlineData(true, 201)]
    [InlineData(false, 404)]
    public async Task CrossWorkspaceCheckoutRequiresExactAssignment(bool exact, int expected)
    {
        await using var f = await OnlineRecordingFixture.CreateAsync(postgres.ConnectionString); var first = await f.CheckIn();
        f.Capture.Clock.Advance(TimeSpan.FromSeconds(1));
        await using var db = f.Capture.Data.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO sites(id,workspace_id,project_id,code,name,created_by)
            VALUES ('00000000-0000-0000-0000-000000000052','00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000022','S2','สอง','00000000-0000-0000-0000-000000000031');
            INSERT INTO user_scope_assignments(id,user_id,workspace_id,project_id,site_id,role_id,created_by,reason)
            VALUES ('00000000-0000-0000-0000-000000000092','00000000-0000-0000-0000-000000000031','00000000-0000-0000-0000-000000000002',
              '00000000-0000-0000-0000-000000000022','00000000-0000-0000-0000-000000000052','00000000-0000-0000-0000-000000000081','00000000-0000-0000-0000-000000000032','ทดสอบ');
            """);
        var site = new TPR10.Api.Scopes.ScopeKey(Guid.Parse("00000000-0000-0000-0000-000000000002"),
            Guid.Parse("00000000-0000-0000-0000-000000000022"), Guid.Parse("00000000-0000-0000-0000-000000000052"));
        var key = Guid.NewGuid(); var issued = await f.Capture.Issue(key, EvidenceAction.CheckOut, site); Assert.Equal(201, issued.Status);
        var m = new CaptureMetadata(issued.Value!.Id, key, site, f.Capture.Clock.GetUtcNow(), new(13, 100, 10, f.Capture.Clock.GetUtcNow()), false, 1);
        if (!exact) await db.Database.ExecuteSqlRawAsync("""
            UPDATE user_scope_assignments SET revoked_at_utc='2026-09-25T17:00:01Z',revoked_by=user_id,revocation_reason='ทดสอบ',version=version+1
            WHERE id='00000000-0000-0000-0000-000000000092';
            """);
        var result = await f.Record(EvidenceAction.CheckOut, m);
        Assert.Equal(expected, result.Status);
        var day = await db.Set<AttendanceDay>().SingleAsync();
        Assert.Equal(first.DayId, day.Id); Assert.Equal(CaptureFixture.Site.WorkspaceId, day.WorkspaceId);
        if (exact)
        {
            Assert.Equal(first.DayId, result.Value!.DayId);
            var recorded = await db.Set<AttendanceEvent>().SingleAsync(x => x.Action == EvidenceAction.CheckOut);
            Assert.Equal(site.WorkspaceId, recorded.WorkspaceId); Assert.Equal(site.SiteId, recorded.SiteId);
        }
        await AssertCommitted(f, exact ? 2 : 1);
    }
    [Theory]
    [InlineData("image")]
    [InlineData("gps")]
    public async Task SameKeyChangedPayloadCannotReplay(string change)
    {
        await using var f = await OnlineRecordingFixture.CreateAsync(postgres.ConnectionString);
        var m = await f.Capture.Metadata(); Assert.Equal(201, (await f.Record(EvidenceAction.CheckIn, m)).Status);
        var bytes = EvidencePublicationTests.Photo();
        if (change == "image") bytes = [.. bytes, 0];
        else m = m with { Gps = m.Gps with { Latitude = 14 } };
        Assert.Equal(409, (await f.Record(EvidenceAction.CheckIn, m, bytes)).Status); await AssertCommitted(f, 1);
    }
    [Fact]
    public async Task CheckoutRejectsStaleVersionAndEqualInstant()
    {
        await using var f = await OnlineRecordingFixture.CreateAsync(postgres.ConnectionString); await f.CheckIn();
        Assert.Equal(409, (await f.Record(EvidenceAction.CheckOut, await f.Capture.Metadata(EvidenceAction.CheckOut))).Status);
        f.Capture.Clock.Advance(TimeSpan.FromSeconds(1));
        var m = await f.Capture.Metadata(EvidenceAction.CheckOut);
        Assert.Equal(409, (await f.Record(EvidenceAction.CheckOut, m with { ExpectedDayVersion = 2 })).Status);
        await AssertCommitted(f, 1);
    }
    internal static async Task AssertCommitted(OnlineRecordingFixture f, int count)
    {
        await using var db = f.Capture.Data.Database.CreateContext();
        Assert.Single(await db.Set<AttendanceDay>().ToArrayAsync());
        Assert.Equal(count, await db.Set<AttendanceEvent>().CountAsync());
        Assert.Equal(count, await db.Set<EvidenceBinding>().CountAsync());
        Assert.Equal(count, await db.Set<EvidenceObject>().CountAsync(x => x.State == EvidenceState.Published));
        Assert.Equal(count, await db.Set<AttendanceOperation>().CountAsync(x => x.State == AttendanceOperationState.Committed && x.ResultJson != null));
        Assert.Equal(count, await db.Set<CaptureChallenge>().CountAsync(x => x.ConsumedOperationId != null));
        Assert.Equal(count, await db.AuditEvents.CountAsync(x => x.EventType == "attendance.record.commit"));
    }
}
