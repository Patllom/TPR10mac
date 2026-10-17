using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Attendance.Corrections;
using TPR10.Api.Attendance.Recording;
using TPR10.Api.Identity.Data;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class CorrectionApprovalTests(PostgresFixture postgres)
{
    internal static readonly Guid Head = Guid.Parse("00000000-0000-0000-0000-000000000032");
    internal static readonly Guid HeadSession = Guid.Parse("00000000-0000-0000-0000-000000000073");
    internal static readonly Guid HrSession = Guid.Parse("00000000-0000-0000-0000-000000000072");

    [Fact]
    public async Task Two_distinct_approvals_apply_missing_day_without_fabricating_events_and_replay_once()
    {
        await using var f = await Fixture(); var pending = await Pending(f);
        var headCommand = Command(pending);
        var hr = (await Decide(f, pending, "supervisor-approve", command: headCommand)).Value!;
        Assert.Equal(CorrectionState.PendingHr, hr.State);
        Assert.Equal(200, (await Decide(f, pending, "supervisor-approve", command: headCommand)).Status);
        var command = Command(hr); var result = await Decide(f, hr, "hr-approve", command: command);
        Assert.Equal(200, result.Status); Assert.Equal(CorrectionState.Approved, result.Value!.State);
        Assert.Equal(200, (await Decide(f, hr, "hr-approve", command: command)).Status);
        Assert.Equal(409, (await Decide(f, hr, "hr-approve", command: command with { Reason = "different" })).Status);
        await using var db = f.Data.Database.CreateContext();
        Assert.Equal(2, await db.Set<CorrectionApprovalAction>().CountAsync());
        var adjustment = await db.Set<AttendanceAdjustment>().SingleAsync();
        Assert.Null(adjustment.BeforeVersion); Assert.Equal(1, adjustment.AfterVersion);
        var day = await db.Set<AttendanceDay>().SingleAsync();
        Assert.Equal(pending.Proposal.InUtc, day.EffectiveInUtc); Assert.Null(day.EffectiveOutUtc);
        Assert.Empty(await db.Set<AttendanceEvent>().ToArrayAsync());
        Assert.Equal(4, await db.Set<CorrectionCommandReceipt>().CountAsync());
        Assert.Equal(1, await db.AuditEvents.CountAsync(x => x.EventType == "attendance.correction.hr-approve"));
    }

    [Theory]
    [InlineData("hr-before-head", 403)]
    [InlineData("self", 403)]
    [InlineData("same-person", 403)]
    [InlineData("stale", 409)]
    [InlineData("revision", 409)]
    [InlineData("empty-reason", 400)]
    [InlineData("long-reason", 400)]
    [InlineData("invalid-action", 400)]
    [InlineData("old-mfa", 403)]
    public async Task Invalid_decisions_do_not_change_effective_time(string fault, int expected)
    {
        await using var f = await Fixture(); var pending = await Pending(f);
        var action = "supervisor-approve"; Guid? session = null; var command = Command(pending);
        if (fault == "hr-before-head") action = "hr-approve";
        if (fault == "self") session = CaptureFixture.Session;
        if (fault == "same-person")
        {
            pending = (await Decide(f, pending, action)).Value!; command = Command(pending);
            action = "hr-approve"; session = HeadSession;
        }
        if (fault == "stale") command = command with { ExpectedVersion = 1 };
        if (fault == "revision") command = command with { Revision = 99 };
        if (fault is "empty-reason" or "long-reason") { action = "return"; command = command with { Reason = fault == "empty-reason" ? "  " : new string('ก', 2001) }; }
        if (fault == "invalid-action") action = "reject";
        await using var db = f.Data.Database.CreateContext();
        if (fault == "old-mfa") await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sessions SET mfa_verified_at_utc={f.Clock.GetUtcNow().AddMinutes(-15)} WHERE id={HeadSession}");
        Assert.Equal(expected, (await Decide(f, pending, action, session, command)).Status);
        Assert.Equal(pending.Version, (await db.Set<CorrectionRequest>().SingleAsync()).Version);
        Assert.Empty(await db.Set<AttendanceAdjustment>().ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Return_resets_seven_day_window_and_resubmission_restarts_head(bool fromHr)
    {
        await using var f = await Fixture(); var pending = await Pending(f);
        if (fromHr) pending = (await Decide(f, pending, "supervisor-approve")).Value!;
        await CorrectionRoutingTests.MoveClock(f, DateTimeOffset.Parse("2026-10-10T17:00:00Z"));
        var returned = await Decide(f, pending, "return", fromHr ? HrSession : HeadSession);
        Assert.Equal(200, returned.Status); Assert.Equal(CorrectionState.Returned, returned.Value!.State);
        Assert.Equal(DateTimeOffset.Parse("2026-10-18T17:00:00Z"), returned.Value.DeadlineUtc);
        var resubmitted = await Submit(f, returned.Value);
        Assert.Equal(CorrectionState.PendingSupervisor, resubmitted.State); Assert.Equal(2, resubmitted.CurrentRevision);
        Assert.Equal(403, (await Decide(f, resubmitted, "hr-approve")).Status);
    }

    [Theory]
    [InlineData("audit")]
    [InlineData("adjustment")]
    public async Task Database_failure_rolls_back_hr_action_day_receipt_and_audit(string fault)
    {
        await using var f = await Fixture(); var pending = await Pending(f);
        pending = (await Decide(f, pending, "supervisor-approve")).Value!;
        await using var db = f.Data.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION test_approval_fail() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic'; END $$;");
        if (fault == "audit") await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER test_fail BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION test_approval_fail()");
        else await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER test_fail BEFORE INSERT ON attendance_adjustments FOR EACH ROW EXECUTE FUNCTION test_approval_fail()");
        Assert.Equal(503, (await Decide(f, pending, "hr-approve")).Status);
        Assert.Equal(CorrectionState.PendingHr, (await db.Set<CorrectionRequest>().SingleAsync()).State);
        Assert.Single(await db.Set<CorrectionApprovalAction>().ToArrayAsync());
        Assert.Empty(await db.Set<AttendanceAdjustment>().ToArrayAsync()); Assert.Empty(await db.Set<AttendanceDay>().ToArrayAsync());
        Assert.Equal(3, await db.Set<CorrectionCommandReceipt>().CountAsync());
        Assert.Equal(0, await db.AuditEvents.CountAsync(x => x.EventType == "attendance.correction.hr-approve"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disabled_actor_after_lock_wait_cannot_approve_or_replay(bool replay)
    {
        await using var f = await Fixture(); var pending = await Pending(f); var command = Command(pending);
        if (replay) Assert.Equal(200, (await Decide(f, pending, "supervisor-approve", command: command)).Status);
        await using var db = f.Data.Database.CreateContext(); await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        var waiting = Decide(f, pending, "supervisor-approve", command: command);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await f.WaitForIdentityWaiter(timeout.Token);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE users SET is_active=false,security_version=security_version+1 WHERE id={Head}");
        await tx.CommitAsync(); Assert.Equal(401, (await waiting).Status);
        Assert.Equal(replay ? 1 : 0, await db.Set<CorrectionApprovalAction>().CountAsync());
    }

    [Theory]
    [InlineData("head-capability")]
    [InlineData("hr-capability")]
    [InlineData("hr-unit")]
    [InlineData("reporting-ended")]
    [InlineData("historical-site")]
    public async Task Final_approval_rechecks_current_route_and_historical_site(string fault)
    {
        await using var f = await Fixture(); var v = await Pending(f);
        v = (await Decide(f, v, "supervisor-approve")).Value!;
        await using var db = f.Data.Database.CreateContext();
        if (fault == "head-capability") await db.Database.ExecuteSqlRawAsync("DELETE FROM role_permissions WHERE permission_id='20000000-0000-0000-0000-000000000016'");
        if (fault == "hr-capability") await db.Database.ExecuteSqlRawAsync("DELETE FROM role_permissions WHERE permission_id='20000000-0000-0000-0000-000000000017'");
        if (fault == "hr-unit") await db.Database.ExecuteSqlRawAsync("UPDATE hr_assignments SET valid_to_utc='2026-09-25T17:00:00Z',ended_by=user_id,version=version+1");
        if (fault == "reporting-ended") await db.Database.ExecuteSqlRawAsync("UPDATE reporting_lines SET valid_to_utc='2026-09-25T17:00:00Z',ended_by=employee_user_id,version=version+1");
        if (fault == "historical-site") await db.Database.ExecuteSqlRawAsync("UPDATE user_scope_assignments SET revoked_at_utc='2026-09-25T00:00:00Z',revoked_by=user_id,revocation_reason='ตรวจประวัติ',version=version+1");
        Assert.Equal(409, (await Decide(f, v, "hr-approve")).Status);
        Assert.Equal(CorrectionState.PendingHr, (await db.Set<CorrectionRequest>().SingleAsync()).State);
        Assert.Empty(await db.Set<AttendanceAdjustment>().ToArrayAsync());
        Assert.Single(await db.Set<CorrectionApprovalAction>().ToArrayAsync());
    }

    [Fact]
    public async Task Parallel_final_decisions_have_one_winner_and_no_duplicate_adjustment()
    {
        await using var f = await Fixture(); var v = await Pending(f);
        v = (await Decide(f, v, "supervisor-approve")).Value!;
        var result = await Task.WhenAll(Decide(f, v, "hr-approve"), Decide(f, v, "hr-approve"));
        Assert.Equal(new[] { 200, 409 }, result.Select(x => x.Status).Order());
        await using var db = f.Data.Database.CreateContext(); Assert.Single(await db.Set<AttendanceAdjustment>().ToArrayAsync());
    }

    [Fact]
    public async Task Pending_can_be_approved_after_employee_submission_deadline()
    {
        await using var f = await Fixture(); var v = await Pending(f);
        await CorrectionRoutingTests.MoveClock(f, DateTimeOffset.Parse("2026-10-10T17:00:00Z"));
        v = (await Decide(f, v, "supervisor-approve")).Value!;
        Assert.Equal(200, (await Decide(f, v, "hr-approve")).Status);
    }

    [Fact]
    public async Task Adjustment_writer_requires_caller_transaction()
    {
        await using var f = await Fixture(); await using var scope = await f.ScopeAsync();
        var writer = ActivatorUtilities.CreateInstance<CorrectionAdjustmentWriter>(scope.ServiceProvider);
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.ApplyAsync(Guid.NewGuid(), 1, default));
    }

    internal async Task<CaptureFixture> Fixture()
    {
        var f = await new CorrectionRoutingTests(postgres).Fixture(); await AddHeadSession(f); return f;
    }
    internal static async Task AddHeadSession(CaptureFixture f)
    {
        await using var db = f.Data.Database.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO sessions(id,user_id,token_hash,stage,created_at_utc,last_seen_at_utc,expires_at_utc,mfa_verified_at_utc,security_version)
            SELECT {HeadSession},{Head},decode(repeat('cd',32),'hex'),stage,created_at_utc,last_seen_at_utc,expires_at_utc,mfa_verified_at_utc,security_version FROM sessions WHERE id={HrSession};
            """);
        db.Add(new MfaFactor { Id = Guid.NewGuid(), UserId = Head, ProtectedSecret = "synthetic-test-only", CreatedAtUtc = f.Clock.GetUtcNow(), ConfirmedAtUtc = f.Clock.GetUtcNow() });
        await db.SaveChangesAsync();
    }
    internal static CorrectionCommand Command(CorrectionView v) => new(Guid.NewGuid(), v.Version, v.CurrentRevision, "ตรวจสอบเวลาแล้ว");
    internal static async Task<CorrectionView> Pending(CaptureFixture f) => await Submit(f, await CorrectionRoutingTests.Draft(f));
    internal static async Task<CorrectionView> Submit(CaptureFixture f, CorrectionView v)
    {
        await using var scope = await f.ScopeAsync();
        var result = await ActivatorUtilities.CreateInstance<CorrectionSubmission>(scope.ServiceProvider).SubmitAsync(v.Id, Command(v), default);
        Assert.Equal(200, result.Status); return result.Value!;
    }
    internal static async Task<CorrectionResult<CorrectionView>> Decide(CaptureFixture f, CorrectionView v, string decision, Guid? session = null, CorrectionCommand? command = null)
    {
        var type = typeof(CorrectionRequest).Assembly.GetType("TPR10.Api.Attendance.Corrections.CorrectionApproval"); Assert.NotNull(type);
        await using var scope = await f.ScopeAsync(session ?? (decision == "hr-approve" ? HrSession : HeadSession));
        var service = ActivatorUtilities.CreateInstance(scope.ServiceProvider, type);
        return await (Task<CorrectionResult<CorrectionView>>)type.GetMethod("DecideAsync")!.Invoke(service, [v.Id, command ?? Command(v), decision, CancellationToken.None])!;
    }
}
