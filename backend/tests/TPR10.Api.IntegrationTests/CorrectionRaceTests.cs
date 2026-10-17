using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TPR10.Api.Attendance.Corrections;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Attendance.Recording;
using TPR10.Api.Attendance.Storage;
using static TPR10.Api.IntegrationTests.CorrectionApprovalTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database"), UnsupportedOSPlatform("windows")]
public sealed class CorrectionRaceTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Correction_only_open_day_accepts_real_checkout_without_fake_checkin()
    {
        await using var f = await Fixture(); var v = await Pending(f.Capture);
        v = (await Decide(f.Capture, v, "supervisor-approve")).Value!;
        Assert.Equal(200, (await Decide(f.Capture, v, "hr-approve")).Status);
        var metadata = await f.Capture.Metadata(EvidenceAction.CheckOut);
        var result = await f.Record(EvidenceAction.CheckOut, metadata with { ConfirmContinuousWork = true });
        Assert.Equal(201, result.Status);
        await using var db = f.Capture.Data.Database.CreateContext();
        var e = await db.Set<AttendanceEvent>().SingleAsync(); Assert.Equal(EvidenceAction.CheckOut, e.Action);
        Assert.Equal(2, (await db.Set<AttendanceDay>().SingleAsync()).Version);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Real_checkout_and_hr_approval_have_one_winner_and_preserve_original_evidence(bool approvalWins)
    {
        await using var f = await Fixture(); var checkin = await f.CheckIn();
        await CorrectionRoutingTests.MoveClock(f.Capture, f.Capture.Clock.GetUtcNow().AddHours(1)); await f.RefreshAsync();
        var v = await ExistingPending(f, checkin, f.Capture.Clock.GetUtcNow().AddMinutes(-5));
        v = (await Decide(f.Capture, v, "supervisor-approve")).Value!;
        await using var db = f.Capture.Data.Database.CreateContext();
        var original = JsonSerializer.Serialize(await db.Set<AttendanceEvent>().AsNoTracking().SingleAsync());
        var copy = await db.Set<EvidenceLocation>().SingleAsync(x => x.EvidenceId == checkin.EvidenceId && x.Variant == "full");
        var storage = f.Capture.Services.GetRequiredService<StorageRuntime>().Resolve(f.Location);
        var before = await storage.ReadVerifiedAsync(copy.ObjectKey, copy.Sha256, copy.Length, default);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Images.Before = async () => { started.SetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(15)); };
        var checkout = f.Record(EvidenceAction.CheckOut, await f.Capture.Metadata(EvidenceAction.CheckOut));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        CorrectionResult<CorrectionView> approval;
        if (approvalWins)
        {
            try { approval = await Decide(f.Capture, v, "hr-approve"); }
            finally { release.TrySetResult(); }
        }
        else
        {
            release.SetResult(); Assert.Equal(201, (await checkout).Status);
            approval = await Decide(f.Capture, v, "hr-approve");
        }
        Assert.Equal(approvalWins ? 200 : 409, approval.Status);
        Assert.Equal(approvalWins ? 409 : 201, (await checkout).Status);
        Assert.Equal(2, (await db.Set<AttendanceDay>().SingleAsync()).Version);
        Assert.Equal(approvalWins ? 1 : 0, await db.Set<AttendanceAdjustment>().CountAsync());
        Assert.Equal(original, JsonSerializer.Serialize(await db.Set<AttendanceEvent>().AsNoTracking().SingleAsync(x => x.Id == checkin.EventId)));
        Assert.Equal(before, await storage.ReadVerifiedAsync(copy.ObjectKey, copy.Sha256, copy.Length, default));
        if (!approvalWins) Assert.Equal(200, (await Decide(f.Capture, v, "return", HrSession)).Status);
    }

    [Fact]
    public async Task Correction_closes_previous_round_and_allows_new_day()
    {
        await using var f = await Fixture(); var checkin = await f.CheckIn();
        await CorrectionRoutingTests.MoveClock(f.Capture, f.Capture.Clock.GetUtcNow().AddDays(1)); await f.RefreshAsync();
        var v = await ExistingPending(f, checkin, f.Capture.Clock.GetUtcNow().AddMinutes(-1));
        v = (await Decide(f.Capture, v, "supervisor-approve")).Value!;
        Assert.Equal(200, (await Decide(f.Capture, v, "hr-approve")).Status);
        Assert.NotEqual(checkin.DayId, (await f.CheckIn()).DayId);
    }

    [Fact]
    public async Task Another_open_round_causes_conflict_and_can_be_returned()
    {
        await using var f = await Fixture(); var v = await Pending(f.Capture);
        v = (await Decide(f.Capture, v, "supervisor-approve")).Value!;
        await f.CheckIn();
        Assert.Equal(409, (await Decide(f.Capture, v, "hr-approve")).Status);
        Assert.Equal(200, (await Decide(f.Capture, v, "return", HrSession)).Status);
        await using var db = f.Capture.Data.Database.CreateContext(); Assert.Empty(await db.Set<AttendanceAdjustment>().ToArrayAsync());
    }

    [Fact]
    public async Task Checkout_guard_migration_roundtrips_empty_database_and_refuses_retained_adjustments()
    {
        await using var f = await Fixture(); await using var db = f.Capture.Data.Database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261017052301_AddAttendanceCorrections"); await migrator.MigrateAsync();
        var v = await Pending(f.Capture); v = (await Decide(f.Capture, v, "supervisor-approve")).Value!;
        Assert.Equal(200, (await Decide(f.Capture, v, "hr-approve")).Status);
        var error = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync("20261017052301_AddAttendanceCorrections"));
        Assert.Equal("P0001", error.SqlState);
        Assert.Single(await db.Set<AttendanceAdjustment>().ToArrayAsync());
    }

    [Fact]
    public async Task Missing_both_times_creates_closed_effective_pair_only()
    {
        await using var f = await Fixture(); var capture = f.Capture;
        var draft = await CorrectionRoutingTests.Draft(capture);
        await using var scope = await capture.ScopeAsync();
        var proposal = draft.Proposal with { OutUtc = draft.Proposal.InUtc.AddHours(8), OutSite = draft.Proposal.InSite };
        var saved = await ActivatorUtilities.CreateInstance<CorrectionDraftService>(scope.ServiceProvider)
            .SaveAsync(draft.Id, new(null, draft.OriginalWorkDate, null, proposal), Command(draft), default);
        Assert.Equal(200, saved.Status);
        var v = await Submit(capture, saved.Value!); v = (await Decide(capture, v, "supervisor-approve")).Value!;
        Assert.Equal(200, (await Decide(capture, v, "hr-approve")).Status);
        await using var db = capture.Data.Database.CreateContext(); var day = await db.Set<AttendanceDay>().SingleAsync();
        Assert.Equal(proposal.OutUtc, day.EffectiveOutUtc); Assert.Equal(proposal.InUtc, day.EffectiveInUtc);
        Assert.Empty(await db.Set<AttendanceEvent>().ToArrayAsync());
        Assert.Empty(await db.Set<EvidenceLocation>().ToArrayAsync());
    }

    [Fact]
    public async Task Approval_rechecks_unique_workdate_created_after_submission()
    {
        await using var f = await Fixture(); var v = await Pending(f.Capture);
        v = (await Decide(f.Capture, v, "supervisor-approve")).Value!;
        await f.Capture.Data.CreateOpenDayAsync(CaptureFixture.Employee, v.Proposal.InUtc);
        Assert.Equal(409, (await Decide(f.Capture, v, "hr-approve")).Status);
        await using var db = f.Capture.Data.Database.CreateContext(); Assert.Empty(await db.Set<AttendanceAdjustment>().ToArrayAsync());
        Assert.Equal(200, (await Decide(f.Capture, v, "return", HrSession)).Status);
    }

    private async Task<OnlineRecordingFixture> Fixture()
    {
        var f = await OnlineRecordingFixture.CreateAsync(postgres.ConnectionString);
        await CorrectionRoutingTests.SeedRoute(f.Capture); await AddHeadSession(f.Capture); return f;
    }
    private static async Task<CorrectionView> ExistingPending(OnlineRecordingFixture f, CaptureReceipt checkin, DateTimeOffset? end)
    {
        await using var scope = await f.Capture.ScopeAsync();
        var site = new CorrectionSite(CaptureFixture.Site.WorkspaceId, CaptureFixture.Site.ProjectId!.Value, CaptureFixture.Site.SiteId!.Value);
        var proposal = new CorrectionProposal(checkin.WorkDate, checkin.OccurredAtUtc, end, site, end is null ? null : site, "ขอแก้เวลาออก");
        var draft = await ActivatorUtilities.CreateInstance<CorrectionDraftService>(scope.ServiceProvider)
            .CreateAsync(new(checkin.DayId, checkin.WorkDate, checkin.DayVersion, proposal), Guid.NewGuid(), default);
        Assert.Equal(201, draft.Status); return await Submit(f.Capture, draft.Value!);
    }
}
