using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Attendance.Corrections;
using TPR10.Api.Data;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class CorrectionDraftTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Timely_changed_date_keeps_returned_window_and_can_complete_both_approvals()
    {
        await using var f = await new CorrectionApprovalTests(postgres).Fixture();
        var draft = await CorrectionRoutingTests.Draft(f);
        var changed = new SaveCorrection(null, draft.OriginalWorkDate, null, draft.Proposal with
        { WorkDate = new(2026, 9, 24), InUtc = DateTimeOffset.Parse("2026-09-24T01:00:00Z") });
        await using (var scope = await f.ScopeAsync())
        {
            var saved = await ActivatorUtilities.CreateInstance<CorrectionDraftService>(scope.ServiceProvider).SaveAsync(draft.Id, changed, CorrectionApprovalTests.Command(draft), default);
            Assert.Equal(200, saved.Status); draft = saved.Value!;
        }
        var pending = await CorrectionApprovalTests.Submit(f, draft);
        await CorrectionRoutingTests.MoveClock(f, DateTimeOffset.Parse("2026-10-10T17:00:00Z"));
        var returned = await CorrectionApprovalTests.Decide(f, pending, "return");
        Assert.Equal(200, returned.Status);
        await using (var scope = await f.ScopeAsync())
        {
            var saved = await ActivatorUtilities.CreateInstance<CorrectionDraftService>(scope.ServiceProvider).SaveAsync(draft.Id, changed, CorrectionApprovalTests.Command(returned.Value!), default);
            Assert.Equal(200, saved.Status); draft = saved.Value!;
        }
        pending = await CorrectionApprovalTests.Submit(f, draft);
        pending = (await CorrectionApprovalTests.Decide(f, pending, "supervisor-approve")).Value!;
        Assert.Equal(200, (await CorrectionApprovalTests.Decide(f, pending, "hr-approve")).Status);
        await using var db = f.Data.Database.CreateContext();
        Assert.Equal(new DateOnly(2026, 9, 24), (await db.Set<AttendanceAdjustment>().SingleAsync()).AfterWorkDate);
    }

    [Fact]
    public async Task Target_day_deadline_is_rechecked_at_submission_not_reserved_by_saving()
    {
        await using var f = await new CorrectionApprovalTests(postgres).Fixture();
        var draft = await CorrectionRoutingTests.Draft(f);
        var changed = new SaveCorrection(null, draft.OriginalWorkDate, null, draft.Proposal with
        { WorkDate = new(2026, 9, 24), InUtc = DateTimeOffset.Parse("2026-09-24T01:00:00Z") });
        await using (var scope = await f.ScopeAsync())
        {
            var saved = await ActivatorUtilities.CreateInstance<CorrectionDraftService>(scope.ServiceProvider).SaveAsync(draft.Id, changed, CorrectionApprovalTests.Command(draft), default);
            Assert.Equal(200, saved.Status); draft = saved.Value!;
        }
        await CorrectionRoutingTests.MoveClock(f, DateTimeOffset.Parse("2026-10-01T17:00:00Z"));
        await using var sending = await f.ScopeAsync();
        var result = await ActivatorUtilities.CreateInstance<CorrectionSubmission>(sending.ServiceProvider).SubmitAsync(draft.Id, CorrectionApprovalTests.Command(draft), default);
        Assert.Equal(400, result.Status);
        await using var db = f.Data.Database.CreateContext();
        Assert.Empty(await db.Set<CorrectionRouteSnapshot>().ToArrayAsync());
    }

    [Fact]
    public async Task Saving_another_day_cannot_bypass_an_expired_target_request()
    {
        await using var f = await Fixture();
        var old = Proposal() with { OriginalWorkDate = new(2026, 9, 24), Proposal = Proposal().Proposal with { WorkDate = new(2026, 9, 24), InUtc = DateTimeOffset.Parse("2026-09-24T01:00:00Z") } };
        Assert.Equal(201, Status(await Call(f, "CreateAsync", old, Guid.NewGuid(), CancellationToken.None)));
        await using var db = f.Data.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("UPDATE correction_requests SET state='Expired',version=version+1");
        Assert.Equal(201, Status(await Call(f, "CreateAsync", Proposal(), Guid.NewGuid(), CancellationToken.None)));
        var fresh = await db.Set<CorrectionRequest>().SingleAsync(x => x.State == CorrectionState.Draft);
        var changed = Proposal() with { Proposal = old.Proposal };
        Assert.Equal(409, Status(await Call(f, "SaveAsync", fresh.Id, changed, new CorrectionCommand(Guid.NewGuid(), fresh.Version, 1, null), CancellationToken.None)));
        Assert.Equal(2, await db.Set<CorrectionRevision>().CountAsync());
    }

    [Fact]
    public async Task Changing_to_an_older_day_does_not_borrow_the_newer_days_deadline()
    {
        await using var f = await Fixture();
        await CorrectionRoutingTests.MoveClock(f, DateTimeOffset.Parse("2026-10-01T17:00:00Z"));
        var created = await Call(f, "CreateAsync", Proposal(), Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(201, Status(created));
        await using var db = f.Data.Database.CreateContext();
        var row = await db.Set<CorrectionRequest>().SingleAsync();
        var changed = Proposal() with { Proposal = Proposal().Proposal with { WorkDate = new(2026, 9, 24), InUtc = DateTimeOffset.Parse("2026-09-24T01:00:00Z") } };
        Assert.Equal(400, Status(await Call(f, "SaveAsync", row.Id, changed, new CorrectionCommand(Guid.NewGuid(), row.Version, 1, null), CancellationToken.None)));
        Assert.Single(await db.Set<CorrectionRevision>().ToArrayAsync());
    }

    [Theory]
    [InlineData("2026-09-25", "2026-10-02T17:00:00Z")]
    [InlineData("2026-09-26", "2026-10-03T17:00:00Z")]
    public void First_deadline_counts_calendar_days(string date, string expected)
    {
        var result = Type("CorrectionDeadline").GetMethod("First")!.Invoke(null, [DateOnly.Parse(date, CultureInfo.InvariantCulture)]);
        Assert.Equal(DateTimeOffset.Parse(expected), result);
    }

    [Theory]
    [InlineData("2026-09-25T16:59:59Z", "2026-10-02T17:00:00Z")]
    [InlineData("2026-09-25T17:00:00Z", "2026-10-03T17:00:00Z")]
    public void Returned_deadline_uses_Thai_date(string at, string expected)
    {
        Assert.Equal(DateTimeOffset.Parse(expected), Type("CorrectionDeadline").GetMethod("Returned")!.Invoke(null, [DateTimeOffset.Parse(at)]));
    }

    [Fact]
    public async Task Missing_both_creates_owned_revision_and_idempotent_receipt_without_live_evidence()
    {
        await using var f = await Fixture();
        var key = Guid.NewGuid();
        var first = await Call(f, "CreateAsync", Proposal(), key, CancellationToken.None);
        Assert.Equal(201, Status(first));
        var again = await Call(f, "CreateAsync", Proposal(), key, CancellationToken.None);
        Assert.Equal(first.GetRawText(), again.GetRawText());
        await using var db = f.Data.Database.CreateContext();
        var request = await db.Set<CorrectionRequest>().SingleAsync();
        Assert.Equal(CaptureFixture.Employee, request.EmployeeId);
        Assert.Null(request.DayId);
        Assert.Equal(1, request.CurrentRevision);
        Assert.Equal(2, request.Version);
        Assert.Single(await db.Set<CorrectionRevision>().ToArrayAsync());
        Assert.Single(await db.Set<CorrectionCommandReceipt>().ToArrayAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM attendance_events").SingleAsync());
        Assert.Equal(409, Status(await Call(f, "CreateAsync", Proposal() with { Proposal = Proposal().Proposal with { Reason = "ต่างกัน" } }, key, CancellationToken.None)));
    }

    [Fact]
    public async Task Save_appends_revision_without_moving_original_deadline()
    {
        await using var f = await Fixture();
        Assert.Equal(201, Status(await Call(f, "CreateAsync", Proposal(), Guid.NewGuid(), CancellationToken.None)));
        await using var db = f.Data.Database.CreateContext();
        var old = await db.Set<CorrectionRequest>().AsNoTracking().SingleAsync();
        var changed = Proposal() with { Proposal = Proposal().Proposal with { Reason = "แก้วัน", WorkDate = new(2026, 9, 24), InUtc = DateTimeOffset.Parse("2026-09-24T01:00:00Z") } };
        Assert.Equal(200, Status(await Call(f, "SaveAsync", old.Id, changed, new CorrectionCommand(Guid.NewGuid(), old.Version, 1, null), CancellationToken.None)));
        var saved = await db.Set<CorrectionRequest>().AsNoTracking().SingleAsync();
        Assert.Equal(old.DeadlineUtc, saved.DeadlineUtc);
        Assert.Equal(old.OriginalWorkDate, saved.OriginalWorkDate);
        Assert.Equal(2, saved.CurrentRevision);
        Assert.Equal("ลืมลงเวลา", (await db.Set<CorrectionRevision>().SingleAsync(x => x.Revision == 1)).Reason);
        Assert.Equal(409, Status(await Call(f, "SaveAsync", old.Id, changed, new CorrectionCommand(Guid.NewGuid(), old.Version, 1, null), CancellationToken.None)));
    }

    [Theory]
    [InlineData(-1L, 201)]
    [InlineData(0L, 400)]
    [InlineData(1L, 400)]
    public async Task Deadline_boundary_is_exclusive(long ticks, int expected)
    {
        await using var f = await Fixture();
        var at = DateTimeOffset.Parse("2026-10-02T17:00:00Z").AddTicks(ticks);
        f.Clock.Advance(at - f.Clock.GetUtcNow());
        await using var db = f.Data.Database.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sessions SET last_seen_at_utc={at},expires_at_utc={at.AddDays(1)}");
        Assert.Equal(expected, Status(await Call(f, "CreateAsync", Proposal(), Guid.NewGuid(), CancellationToken.None)));
    }

    [Theory]
    [InlineData("future", 400)]
    [InlineData("order", 400)]
    [InlineData("reason", 400)]
    [InlineData("out-site", 400)]
    [InlineData("date", 400)]
    [InlineData("history", 409)]
    [InlineData("membership", 409)]
    [InlineData("revoked-session", 401)]
    [InlineData("duplicate", 409)]
    [InlineData("expired", 409)]
    public async Task Invalid_or_unproven_drafts_cannot_create_revision(string fault, int expected)
    {
        await using var f = await Fixture();
        var input = Proposal();
        await using var db = f.Data.Database.CreateContext();
        if (fault == "future") input = input with { Proposal = input.Proposal with { InUtc = f.Clock.GetUtcNow().AddHours(1) } };
        if (fault == "order") input = input with { Proposal = input.Proposal with { OutUtc = input.Proposal.InUtc, OutSite = input.Proposal.InSite } };
        if (fault == "reason") input = input with { Proposal = input.Proposal with { Reason = "   " } };
        if (fault == "out-site") input = input with { Proposal = input.Proposal with { OutSite = input.Proposal.InSite } };
        if (fault == "date") input = input with { Proposal = input.Proposal with { WorkDate = new(2026, 9, 24) } };
        if (fault == "history") await db.Database.ExecuteSqlRawAsync("UPDATE user_scope_assignments SET created_at_utc='2026-09-25T16:00:00Z',version=version+1");
        if (fault == "membership") await db.Database.ExecuteSqlRawAsync("""
            UPDATE employee_memberships SET valid_to_utc='2026-09-25T00:00:00Z',ended_by=user_id,version=version+1;
            INSERT INTO employee_memberships(id,user_id,workspace_id,department_id,valid_from_utc,created_by,reason)
            SELECT gen_random_uuid(),user_id,workspace_id,department_id,'2026-09-25T02:00:00Z',created_by,'กลับเข้าทำงาน' FROM employee_memberships;
            """);
        if (fault == "revoked-session") await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sessions SET revoked_at_utc={f.Clock.GetUtcNow()}");
        if (fault is "duplicate" or "expired")
        {
            Assert.Equal(201, Status(await Call(f, "CreateAsync", input, Guid.NewGuid(), CancellationToken.None)));
            if (fault == "expired") await db.Database.ExecuteSqlRawAsync("UPDATE correction_requests SET state='Expired',version=version+1");
        }
        Assert.Equal(expected, Status(await Call(f, "CreateAsync", input, Guid.NewGuid(), CancellationToken.None)));
        Assert.Equal(fault is "duplicate" or "expired" ? 1 : 0, await db.Set<CorrectionRevision>().CountAsync());
    }

    private static SaveCorrection Proposal() => new(null, new(2026, 9, 25), null,
        new(new(2026, 9, 25), DateTimeOffset.Parse("2026-09-25T01:00:00Z"), null,
            new(CaptureFixture.Site.WorkspaceId, CaptureFixture.Site.ProjectId!.Value, CaptureFixture.Site.SiteId!.Value), null, "ลืมลงเวลา"));

    [Fact]
    public async Task Missing_day_cannot_claim_a_newer_original_date_to_extend_deadline()
    {
        await using var f = await Fixture();
        Assert.Equal(400, Status(await Call(f, "CreateAsync", Proposal() with { OriginalWorkDate = new(2026, 9, 26) }, Guid.NewGuid(), CancellationToken.None)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_is_readonly_and_returned_save_does_not_renew_deadline(bool expired)
    {
        await using var f = await Fixture();
        Assert.Equal(201, Status(await Call(f, "CreateAsync", Proposal(), Guid.NewGuid(), CancellationToken.None)));
        await using var db = f.Data.Database.CreateContext();
        var row = await db.Set<CorrectionRequest>().AsNoTracking().SingleAsync();
        await db.Database.ExecuteSqlRawAsync("UPDATE correction_requests SET state='PendingSupervisor',version=version+1");
        Assert.Equal(409, Status(await Call(f, "SaveAsync", row.Id, Proposal(), new CorrectionCommand(Guid.NewGuid(), 3, 1, null), CancellationToken.None)));
        // Task 3/4 are not implemented: seed a legal returned route using the real schema guards.
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO users(id,username,normalized_username) VALUES ('00000000-0000-0000-0000-000000000033','hr','HR');
            INSERT INTO correction_route_snapshots(request_id,revision,supervisor_id,hr_candidate_ids,membership_version,reporting_version,created_at_utc)
            SELECT id,1,'00000000-0000-0000-0000-000000000032',ARRAY['00000000-0000-0000-0000-000000000033'::uuid],1,1,'2026-09-25T17:00:00Z' FROM correction_requests;
            INSERT INTO correction_approval_actions(request_id,revision,step,actor_id,outcome,reason,occurred_at_utc)
            SELECT id,1,'Supervisor','00000000-0000-0000-0000-000000000032','Returned','ตรวจเวลาใหม่','2026-09-25T17:00:00Z' FROM correction_requests;
            UPDATE correction_requests SET state='Returned',deadline_utc='2026-10-03T17:00:00Z',version=version+1;
            """);
        if (expired)
        {
            var at = DateTimeOffset.Parse("2026-10-03T17:00:00Z");
            f.Clock.Advance(at - f.Clock.GetUtcNow());
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sessions SET last_seen_at_utc={at},expires_at_utc={at.AddDays(1)}");
        }
        Assert.Equal(expired ? 400 : 200, Status(await Call(f, "SaveAsync", row.Id, Proposal(), new CorrectionCommand(Guid.NewGuid(), 4, 1, null), CancellationToken.None)));
        var saved = await db.Set<CorrectionRequest>().AsNoTracking().SingleAsync();
        Assert.Equal(DateTimeOffset.Parse("2026-10-03T17:00:00Z"), saved.DeadlineUtc);
        Assert.Equal(expired ? CorrectionState.Expired : CorrectionState.Returned, saved.State);
        Assert.Equal(expired ? 1 : 2, await db.Set<CorrectionRevision>().CountAsync());
    }

    [Fact]
    public async Task Actor_in_untrusted_body_does_not_change_owner()
    {
        await using var f = await Fixture();
        var json = JsonSerializer.Serialize(Proposal());
        var input = JsonSerializer.Deserialize<SaveCorrection>(json.Insert(1, "\"EmployeeId\":\"00000000-0000-0000-0000-000000000032\","))!;
        Assert.Equal(201, Status(await Call(f, "CreateAsync", input, Guid.NewGuid(), CancellationToken.None)));
        await using var db = f.Data.Database.CreateContext();
        Assert.Equal(CaptureFixture.Employee, (await db.Set<CorrectionRequest>().SingleAsync()).EmployeeId);
    }

    [Theory]
    [InlineData("2026-09-25T00:59:59Z", 409)]
    [InlineData("2026-09-25T01:00:00Z", 409)]
    [InlineData("2026-09-25T01:00:01Z", 201)]
    public async Task Retained_revoked_assignment_proves_only_its_historical_interval(string revoked, int expected)
    {
        await using var f = await Fixture();
        await using var db = f.Data.Database.CreateContext();
        // A new grant keeps current access, but must not replace evidence of the old interval.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE user_scope_assignments SET revoked_at_utc={DateTimeOffset.Parse(revoked)},revoked_by={CaptureFixture.Employee},revocation_reason='เปลี่ยนสิทธิ์',version=version+1");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO user_scope_assignments(id,user_id,workspace_id,project_id,site_id,role_id,created_at_utc,created_by,reason)
            SELECT gen_random_uuid(),user_id,workspace_id,project_id,site_id,role_id,'2026-09-25T16:00:00Z',created_by,reason FROM user_scope_assignments;
            """);
        Assert.Equal(expected, Status(await Call(f, "CreateAsync", Proposal(), Guid.NewGuid(), CancellationToken.None)));
    }
    private async Task<CaptureFixture> Fixture()
    {
        var f = await CaptureFixture.CreateAsync(postgres.ConnectionString);
        await using var db = f.Data.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("UPDATE user_scope_assignments SET created_at_utc='2026-09-01T00:00:00Z',version=version+1");
        return f;
    }

    [Theory]
    [InlineData(1L, 201)]
    [InlineData(2L, 409)]
    public async Task Existing_day_requires_matching_version_and_keeps_original_snapshot(long version, int expected)
    {
        await using var f = await Fixture();
        var day = await f.Data.CreateOpenDayAsync(CaptureFixture.Employee, Proposal().Proposal.InUtc);
        var input = Proposal() with { DayId = day, ExpectedDayVersion = version };
        Assert.Equal(expected, Status(await Call(f, "CreateAsync", input, Guid.NewGuid(), CancellationToken.None)));
        await using var db = f.Data.Database.CreateContext();
        if (expected == 201)
        {
            Assert.Equal(day, (await db.Set<CorrectionRequest>().SingleAsync()).DayId);
            Assert.Equal(1, (await db.Set<CorrectionRevision>().SingleAsync()).BaselineDayVersion);
        }
        Assert.Equal(1, await db.Database.SqlQueryRaw<long>("SELECT version AS \"Value\" FROM attendance_days").SingleAsync());
    }

    [Theory]
    [InlineData(false, 201)]
    [InlineData(true, 409)]
    public async Task Out_site_must_have_its_own_historical_grant(bool missing, int expected)
    {
        await using var f = await Fixture();
        await using var db = f.Data.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO sites(id,workspace_id,project_id,code,name,created_by)
            SELECT '00000000-0000-0000-0000-000000000052',workspace_id,project_id,'S2','สอง',created_by FROM sites;
            """);
        if (!missing) await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO user_scope_assignments(id,user_id,workspace_id,project_id,site_id,role_id,created_at_utc,created_by,reason)
            SELECT gen_random_uuid(),user_id,workspace_id,project_id,'00000000-0000-0000-0000-000000000052',role_id,created_at_utc,created_by,reason FROM user_scope_assignments;
            """);
        var p = Proposal();
        p = p with { Proposal = p.Proposal with { OutUtc = p.Proposal.InUtc.AddHours(8), OutSite = p.Proposal.InSite with { SiteId = Guid.Parse("00000000-0000-0000-0000-000000000052") } } };
        Assert.Equal(expected, Status(await Call(f, "CreateAsync", p, Guid.NewGuid(), CancellationToken.None)));
    }
    private static Type Type(string name)
    {
        var type = typeof(CorrectionRequest).Assembly.GetType("TPR10.Api.Attendance.Corrections." + name);
        Assert.NotNull(type);
        return type;
    }

    [Fact]
    public async Task Audit_failure_rolls_back_request_revision_and_receipt()
    {
        await using var f = await Fixture();
        await using var db = f.Data.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION test_correction_audit_failure() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'synthetic audit failure'; END $$;
            CREATE TRIGGER test_correction_audit_failure BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION test_correction_audit_failure();
            """);
        Assert.Equal(503, Status(await Call(f, "CreateAsync", Proposal(), Guid.NewGuid(), CancellationToken.None)));
        Assert.Empty(await db.Set<CorrectionRequest>().ToArrayAsync());
        Assert.Empty(await db.Set<CorrectionRevision>().ToArrayAsync());
        Assert.Empty(await db.Set<CorrectionCommandReceipt>().ToArrayAsync());
    }

    [Fact]
    public async Task Thai_offset_input_is_the_same_instant_and_returns_UTC()
    {
        await using var f = await Fixture();
        var input = Proposal();
        input = input with
        {
            Proposal = input.Proposal with
            {
                InUtc = DateTimeOffset.Parse("2026-09-25T08:00:00+07:00"),
                OutUtc = DateTimeOffset.Parse("2026-09-25T16:00:00+07:00"),
                OutSite = input.Proposal.InSite
            }
        };
        var result = await Call(f, "CreateAsync", input, Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(201, Status(result));
        Assert.Equal(TimeSpan.Zero, result.GetProperty("Value").GetProperty("Proposal").GetProperty("InUtc").GetDateTimeOffset().Offset);
        await using var db = f.Data.Database.CreateContext();
        var revision = await db.Set<CorrectionRevision>().SingleAsync();
        Assert.Equal(DateTimeOffset.Parse("2026-09-25T01:00:00Z"), revision.InUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-09-25T09:00:00Z"), revision.OutUtc);
    }

    [Theory]
    [InlineData(false, 403)]
    [InlineData(true, 401)]
    public async Task Revocation_while_waiting_is_rechecked_before_draft_write(bool session, int expected)
    {
        await using var f = await Fixture();
        await using var blocker = f.Data.Database.CreateContext();
        await using var tx = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        var pending = Call(f, "CreateAsync", Proposal(), Guid.NewGuid(), CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await f.WaitForIdentityWaiter(deadline.Token);
        if (session) await blocker.Database.ExecuteSqlInterpolatedAsync($"UPDATE sessions SET revoked_at_utc={f.Clock.GetUtcNow()}");
        else await blocker.Database.ExecuteSqlRawAsync("DELETE FROM role_permissions WHERE permission_id='20000000-0000-0000-0000-000000000013'");
        await tx.CommitAsync();
        Assert.Equal(expected, Status(await pending));
        Assert.Empty(await blocker.Set<CorrectionRequest>().ToArrayAsync());
    }
    private static int Status(JsonElement result) => result.GetProperty("Status").GetInt32();
    private static async Task<JsonElement> Call(CaptureFixture f, string method, params object[] args)
    {
        await using var scope = await f.ScopeAsync();
        var service = ActivatorUtilities.CreateInstance(scope.ServiceProvider, Type("CorrectionDraftService"));
        var task = (Task)service.GetType().GetMethod(method)!.Invoke(service, args)!;
        await task;
        return JsonSerializer.SerializeToElement(task.GetType().GetProperty("Result")!.GetValue(task));
    }
}
