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
    public async Task Approval_cannot_reopen_a_day_with_an_immutable_live_checkout()
    {
        await using var f = await Fixture(); var entry = await f.CheckIn();
        await CorrectionRoutingTests.MoveClock(f.Capture, f.Capture.Clock.GetUtcNow().AddHours(1)); await f.RefreshAsync();
        var checkout = await f.Record(EvidenceAction.CheckOut, await f.Capture.Metadata(EvidenceAction.CheckOut));
        Assert.Equal(201, checkout.Status);
        await using var db = f.Capture.Data.Database.CreateContext();
        var original = JsonSerializer.Serialize(await db.Set<AttendanceEvent>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync());
        CorrectionView draft;
        await using (var scope = await f.Capture.ScopeAsync())
        {
            var site = new CorrectionSite(CaptureFixture.Site.WorkspaceId, CaptureFixture.Site.ProjectId!.Value, CaptureFixture.Site.SiteId!.Value);
            var result = await ActivatorUtilities.CreateInstance<CorrectionDraftService>(scope.ServiceProvider).CreateAsync(
                new(entry.DayId, entry.WorkDate, 2, new(entry.WorkDate, entry.OccurredAtUtc, null, site, null, "ขอเปิดรอบเดิม")), Guid.NewGuid(), default);
            Assert.Equal(201, result.Status); draft = result.Value!;
        }
        var pending = await Submit(f.Capture, draft);
        pending = (await Decide(f.Capture, pending, "supervisor-approve")).Value!;
        Assert.Equal(409, (await Decide(f.Capture, pending, "hr-approve")).Status);
        Assert.Equal(checkout.Value!.OccurredAtUtc, (await db.Set<AttendanceDay>().SingleAsync()).EffectiveOutUtc);
        Assert.Empty(await db.Set<AttendanceAdjustment>().ToArrayAsync());
        Assert.Equal(original, JsonSerializer.Serialize(await db.Set<AttendanceEvent>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync()));
        Assert.Equal(200, (await Decide(f.Capture, pending, "return", HrSession)).Status);
    }

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

    [Fact]
    public async Task Returned_missing_day_request_can_recover_against_later_live_day_without_rewriting_history()
    {
        await using var f = await Fixture();
        var draft = await CorrectionRoutingTests.Draft(f.Capture);
        await using var db = f.Capture.Data.Database.CreateContext();
        var originalRequest = await db.Set<CorrectionRequest>().AsNoTracking().SingleAsync();
        var originalRevision = JsonSerializer.Serialize(await db.Set<CorrectionRevision>().AsNoTracking().SingleAsync());
        var v = await Submit(f.Capture, draft);
        v = (await Decide(f.Capture, v, "supervisor-approve")).Value!;
        await f.Capture.Data.CreateOpenDayAsync(CaptureFixture.Employee, draft.Proposal.InUtc.AddMinutes(30));
        var day = await db.Set<AttendanceDay>().AsNoTracking().SingleAsync();
        Assert.Equal(409, (await Decide(f.Capture, v, "hr-approve")).Status);
        v = (await Decide(f.Capture, v, "return", HrSession)).Value!;
        var deadline = v.DeadlineUtc;
        var approvals = JsonSerializer.Serialize(await db.Set<CorrectionApprovalAction>().AsNoTracking().OrderBy(x => x.Step).ToArrayAsync());
        await using (var scope = await f.Capture.ScopeAsync())
        {
            var read = await ActivatorUtilities.CreateInstance<CorrectionQueries>(scope.ServiceProvider).GetAsync(v.Id, default);
            Assert.Equal(200, read.Status);
            Assert.Equal(day.Id, read.Value!.Comparison!.DayId);
            Assert.False(read.Value.Comparison.BaselineMatches);
            Assert.Equal(day.Version, read.Value.Comparison.Current!.Version);
            var saved = await ActivatorUtilities.CreateInstance<CorrectionDraftService>(scope.ServiceProvider)
                .SaveAsync(v.Id, new(day.Id, v.OriginalWorkDate, day.Version, v.Proposal), Command(v), default);
            Assert.Equal(200, saved.Status); v = saved.Value!;
        }
        Assert.Equal(deadline, v.DeadlineUtc);
        v = await Submit(f.Capture, v);
        Assert.Equal(CorrectionState.PendingSupervisor, v.State);
        v = (await Decide(f.Capture, v, "supervisor-approve")).Value!;
        Assert.Equal(200, (await Decide(f.Capture, v, "hr-approve")).Status);
        var request = await db.Set<CorrectionRequest>().AsNoTracking().SingleAsync();
        Assert.Null(request.DayId);
        Assert.Equal(originalRequest.SnapshotAtUtc, request.SnapshotAtUtc);
        Assert.Equal(originalRequest.DeadlineAnchorUtc, request.DeadlineAnchorUtc);
        Assert.Equal(deadline, request.DeadlineUtc);
        Assert.Equal(originalRevision, JsonSerializer.Serialize(await db.Set<CorrectionRevision>().AsNoTracking().SingleAsync(x => x.Revision == 1)));
        Assert.Equal(approvals, JsonSerializer.Serialize(await db.Set<CorrectionApprovalAction>().AsNoTracking().Where(x => x.Revision == 1).OrderBy(x => x.Step).ToArrayAsync()));
        var adjustment = await db.Set<AttendanceAdjustment>().SingleAsync();
        Assert.Equal(day.Id, adjustment.DayId); Assert.Equal(day.Version, adjustment.BeforeVersion);
        Assert.Equal(draft.Proposal.InUtc, (await db.Set<AttendanceDay>().AsNoTracking().SingleAsync()).EffectiveInUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_revision_preserves_live_evidence_and_requires_fresh_baseline(bool returned)
    {
        await using var f = await Fixture();
        await CorrectionRoutingTests.MoveClock(f.Capture, f.Capture.Clock.GetUtcNow().AddHours(2)); await f.RefreshAsync();
        CorrectionView v;
        await using (var scope = await f.Capture.ScopeAsync())
        {
            var now = f.Capture.Clock.GetUtcNow();
            var site = new CorrectionSite(CaptureFixture.Site.WorkspaceId, CaptureFixture.Site.ProjectId!.Value, CaptureFixture.Site.SiteId!.Value);
            var proposal = new CorrectionProposal(AttendancePairing.WorkDate(now), now.AddMinutes(-30), null, site, null, "ลืมลงเวลาเข้า");
            var result = await ActivatorUtilities.CreateInstance<CorrectionDraftService>(scope.ServiceProvider)
                .CreateAsync(new(null, proposal.WorkDate, null, proposal), Guid.NewGuid(), default);
            Assert.Equal(201, result.Status); v = result.Value!;
        }
        if (returned) { v = await Submit(f.Capture, v); v = (await Decide(f.Capture, v, "return", HeadSession)).Value!; }
        var entry = await f.CheckIn();
        await using var db = f.Capture.Data.Database.CreateContext();
        var events = JsonSerializer.Serialize(await db.Set<AttendanceEvent>().AsNoTracking().ToArrayAsync());
        var location = await db.Set<EvidenceLocation>().SingleAsync(x => x.EvidenceId == entry.EvidenceId && x.Variant == "full");
        var storage = f.Capture.Services.GetRequiredService<StorageRuntime>().Resolve(f.Location);
        var image = await storage.ReadVerifiedAsync(location.ObjectKey, location.Sha256, location.Length, default);
        var deadline = v.DeadlineUtc;
        var input = new SaveCorrection(entry.DayId, v.OriginalWorkDate, entry.DayVersion, v.Proposal);
        await using (var scope = await f.Capture.ScopeAsync())
        {
            var service = ActivatorUtilities.CreateInstance<CorrectionDraftService>(scope.ServiceProvider);
            Assert.Equal(409, (await service.SaveAsync(v.Id, input with { ExpectedDayVersion = entry.DayVersion + 1 }, Command(v), default)).Status);
            Assert.Equal(409, (await service.SaveAsync(v.Id, input with { OriginalWorkDate = v.OriginalWorkDate.AddDays(-1) }, Command(v), default)).Status);
            var command = Command(v);
            var saved = await service.SaveAsync(v.Id, input, command, default);
            Assert.Equal(200, saved.Status);
            Assert.Equal(JsonSerializer.Serialize(saved.Value), JsonSerializer.Serialize((await service.SaveAsync(v.Id, input, command, default)).Value));
            v = saved.Value!;
            Assert.Equal(409, (await service.SaveAsync(v.Id, input with { DayId = null, ExpectedDayVersion = null }, Command(v), default)).Status);
        }
        Assert.Equal(deadline, v.DeadlineUtc);
        v = await Submit(f.Capture, v);
        Assert.NotEqual(200, (await Decide(f.Capture, v, "hr-approve")).Status);
        v = (await Decide(f.Capture, v, "supervisor-approve")).Value!;
        Assert.Equal(200, (await Decide(f.Capture, v, "hr-approve")).Status);
        Assert.Equal(events, JsonSerializer.Serialize(await db.Set<AttendanceEvent>().AsNoTracking().ToArrayAsync()));
        Assert.Equal(image, await storage.ReadVerifiedAsync(location.ObjectKey, location.Sha256, location.Length, default));
        Assert.Equal(entry.DayId, (await db.Set<AttendanceAdjustment>().SingleAsync()).DayId);
    }

    [Fact]
    public async Task Recovery_cannot_extend_expired_return_deadline()
    {
        await using var f = await Fixture(); var v = await Pending(f.Capture);
        v = (await Decide(f.Capture, v, "return", HeadSession)).Value!;
        await f.Capture.Data.CreateOpenDayAsync(CaptureFixture.Employee, v.Proposal.InUtc);
        await using var db = f.Capture.Data.Database.CreateContext();
        var day = await db.Set<AttendanceDay>().SingleAsync();
        await CorrectionRoutingTests.MoveClock(f.Capture, v.DeadlineUtc);
        await using var scope = await f.Capture.ScopeAsync();
        var saved = await ActivatorUtilities.CreateInstance<CorrectionDraftService>(scope.ServiceProvider)
            .SaveAsync(v.Id, new(day.Id, v.OriginalWorkDate, day.Version, v.Proposal), Command(v), default);
        Assert.Equal(400, saved.Status);
        var request = await db.Set<CorrectionRequest>().SingleAsync();
        Assert.Equal(CorrectionState.Expired, request.State); Assert.Equal(v.DeadlineUtc, request.DeadlineUtc);
        Assert.Single(await db.Set<CorrectionRevision>().ToArrayAsync());
    }

    [Fact]
    public async Task Recovery_migration_roundtrips_and_refuses_to_discard_recovery_history()
    {
        await using var f = await Fixture(); await using var db = f.Capture.Data.Database.CreateContext();
        const string previous = "20261017173751_AllowCorrectedAttendanceCheckout";
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(previous); await migrator.MigrateAsync();
        var v = await CorrectionRoutingTests.Draft(f.Capture);
        await f.Capture.Data.CreateOpenDayAsync(CaptureFixture.Employee, v.Proposal.InUtc);
        var day = await db.Set<AttendanceDay>().SingleAsync();
        await using var scope = await f.Capture.ScopeAsync();
        Assert.Equal(200, (await ActivatorUtilities.CreateInstance<CorrectionDraftService>(scope.ServiceProvider)
            .SaveAsync(v.Id, new(day.Id, v.OriginalWorkDate, day.Version, v.Proposal), Command(v), default)).Status);
        var error = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync(previous));
        Assert.Equal("P0001", error.SqlState);
        Assert.Equal(2, await db.Set<CorrectionRevision>().CountAsync());
    }

    [Fact]
    public async Task Recovery_rollback_waits_for_inflight_revision_before_checking_retained_history()
    {
        await using var f = await Fixture(); var v = await CorrectionRoutingTests.Draft(f.Capture);
        await f.Capture.Data.CreateOpenDayAsync(CaptureFixture.Employee, v.Proposal.InUtc);
        await using var writer = f.Capture.Data.Database.CreateContext();
        var day = await writer.Set<AttendanceDay>().SingleAsync();
        var revision = await writer.Set<CorrectionRevision>().AsNoTracking().SingleAsync();
        await using var tx = await writer.Database.BeginTransactionAsync();
        revision.Revision = 2; revision.RecoveryDayId = day.Id; revision.BaselineDayVersion = day.Version;
        writer.Add(revision); await writer.SaveChangesAsync();
        await using var downgrade = f.Capture.Data.Database.CreateContext();
        await downgrade.Database.OpenConnectionAsync();
        var pid = ((NpgsqlConnection)downgrade.Database.GetDbConnection()).ProcessID;
        const string previous = "20261017173751_AllowCorrectedAttendanceCheckout";
        var pending = downgrade.GetService<IMigrator>().MigrateAsync(previous);
        await using var observer = f.Capture.Data.Database.CreateContext();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            // Observe the actual lock waiter, not a timing-based guess about migration progress.
            while (!await observer.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM pg_locks WHERE pid={pid} AND NOT granted")
                .AnyAsync(x => x > 0, timeout.Token))
            {
                if (pending.IsCompleted) await pending;
                await Task.Delay(20, timeout.Token);
            }
            await tx.CommitAsync();
            var error = await Assert.ThrowsAsync<PostgresException>(() => pending);
            Assert.Equal("P0001", error.SqlState);
            Assert.Equal(day.Id, (await observer.Set<CorrectionRevision>().AsNoTracking().SingleAsync(x => x.Revision == 2)).RecoveryDayId);
        }
        finally
        {
            if (writer.Database.CurrentTransaction is not null) await tx.RollbackAsync();
            try { await pending; } catch (PostgresException) { /* Asserted above; release all connections. */ }
        }
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
