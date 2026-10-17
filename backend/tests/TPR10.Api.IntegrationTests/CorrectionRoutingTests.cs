using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Attendance.Corrections;
using TPR10.Api.Identity.Authorization;
using TPR10.Api.Identity.Data;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class CorrectionRoutingTests(PostgresFixture postgres)
{
    private static readonly Guid Hr = Guid.Parse("00000000-0000-0000-0000-000000000033");
    private static readonly Guid HrSession = Guid.Parse("00000000-0000-0000-0000-000000000072");
    private static readonly Guid Manager = Guid.Parse("00000000-0000-0000-0000-000000000032");

    [Fact]
    public async Task Submit_freezes_route_and_retry_does_not_duplicate_audit_or_receipt()
    {
        await using var f = await Fixture(); var draft = await Draft(f);
        var command = Command(draft);
        var result = await Call(f, draft.Id, command);
        Assert.Equal(200, result.Status); Assert.Equal(CorrectionState.PendingSupervisor, result.Value!.State);
        Assert.Equal(3, result.Value.Version); Assert.Equal(1, result.Value.CurrentRevision);
        Assert.Equal(result.Value.Id, (await Call(f, draft.Id, command)).Value!.Id);
        Assert.Equal(409, (await Call(f, draft.Id, command with { Reason = "เปลี่ยนข้อมูล" })).Status);
        await using var db = f.Data.Database.CreateContext();
        var route = await db.Set<CorrectionRouteSnapshot>().SingleAsync();
        Assert.Equal(Manager, route.SupervisorId); Assert.Equal(new[] { Hr }, route.HrCandidateIds);
        Assert.Equal(2, await db.Set<CorrectionCommandReceipt>().CountAsync());
        Assert.Equal(1, await db.AuditEvents.CountAsync(x => x.EventType == "attendance.correction.submit"));
    }

    [Theory]
    [InlineData("missing-line")]
    [InlineData("no-hr")]
    [InlineData("no-head-cap")]
    [InlineData("cycle")]
    [InlineData("moved")]
    public async Task Incomplete_or_changed_route_leaves_draft_untouched(string fault)
    {
        await using var f = await Fixture(); var draft = await Draft(f);
        await using var db = f.Data.Database.CreateContext();
        if (fault == "missing-line") await db.Database.ExecuteSqlRawAsync("UPDATE reporting_lines SET valid_to_utc='2026-09-25T16:00:00Z',ended_by=employee_user_id,version=version+1");
        if (fault == "no-hr") await db.Database.ExecuteSqlRawAsync("DELETE FROM role_permissions WHERE permission_id='20000000-0000-0000-0000-000000000017'");
        if (fault == "no-head-cap") await db.Database.ExecuteSqlRawAsync("DELETE FROM role_permissions WHERE permission_id='20000000-0000-0000-0000-000000000016'");
        if (fault == "cycle") await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO reporting_lines(id,employee_membership_id,employee_user_id,supervisor_user_id,valid_from_utc,created_by,reason)
            VALUES(gen_random_uuid(),'00000000-0000-0000-0000-000000000042','00000000-0000-0000-0000-000000000032','00000000-0000-0000-0000-000000000031','2026-09-01Z','00000000-0000-0000-0000-000000000032','วงทดสอบ');
            """);
        if (fault == "moved") await db.Database.ExecuteSqlRawAsync("""
            UPDATE employee_memberships SET valid_to_utc='2026-09-25T16:00:00Z',ended_by=user_id,version=version+1 WHERE user_id='00000000-0000-0000-0000-000000000031';
            INSERT INTO employee_memberships(id,user_id,workspace_id,department_id,valid_from_utc,created_by,reason)
            VALUES(gen_random_uuid(),'00000000-0000-0000-0000-000000000031','00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000012','2026-09-25T16:00:00Z','00000000-0000-0000-0000-000000000032','ย้ายหน่วย');
            """);
        Assert.Equal(409, (await Call(f, draft.Id, Command(draft))).Status);
        Assert.Empty(await db.Set<CorrectionRouteSnapshot>().ToArrayAsync());
        Assert.Equal(CorrectionState.Draft, (await db.Set<CorrectionRequest>().SingleAsync()).State);
    }

    [Theory]
    [InlineData(-1L, 200)]
    [InlineData(0L, 400)]
    [InlineData(1L, 400)]
    public async Task Actual_submit_deadline_is_exclusive(long ticks, int expected)
    {
        await using var f = await Fixture(); var draft = await Draft(f);
        await MoveClock(f, DateTimeOffset.Parse("2026-10-02T17:00:00Z").AddTicks(ticks));
        Assert.Equal(expected, (await Call(f, draft.Id, Command(draft))).Status);
    }

    [Theory]
    [InlineData("missing-grant")]
    [InlineData("other-unit")]
    [InlineData("expired-grant")]
    [InlineData("future-grant")]
    [InlineData("missing-capability")]
    [InlineData("old-mfa")]
    public async Task Reroute_requires_current_unit_assignment_capability_and_recent_mfa(string fault)
    {
        await using var f = await Fixture(); var draft = await Draft(f);
        var pending = (await Call(f, draft.Id, Command(draft))).Value!;
        await using var db = f.Data.Database.CreateContext();
        if (fault == "missing-grant") await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE hr_assignments SET user_id={Manager},version=version+1");
        if (fault == "other-unit") await db.Database.ExecuteSqlRawAsync("UPDATE hr_assignments SET workspace_id='00000000-0000-0000-0000-000000000002',department_id='00000000-0000-0000-0000-000000000012',version=version+1");
        if (fault == "expired-grant") await db.Database.ExecuteSqlRawAsync("UPDATE hr_assignments SET valid_to_utc='2026-09-25T17:00:00Z',ended_by=user_id,version=version+1");
        if (fault == "future-grant") await db.Database.ExecuteSqlRawAsync("UPDATE hr_assignments SET valid_from_utc='2026-09-26T17:00:00Z',version=version+1");
        if (fault == "missing-capability") await db.Database.ExecuteSqlRawAsync("DELETE FROM role_permissions WHERE permission_id='20000000-0000-0000-0000-000000000018'");
        if (fault == "old-mfa") await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sessions SET mfa_verified_at_utc={f.Clock.GetUtcNow().AddMinutes(-15)} WHERE id={HrSession}");
        Assert.Equal(403, (await Call(f, draft.Id, Command(pending), true)).Status);
        Assert.Single(await db.Set<CorrectionRouteSnapshot>().ToArrayAsync());
        Assert.Equal(pending.Version, (await db.Set<CorrectionRequest>().SingleAsync()).Version);
    }

    [Fact]
    public async Task Reroute_restarts_supervisor_with_new_revision_and_retains_old_approval_and_deadline()
    {
        await using var f = await Fixture(); var draft = await Draft(f);
        var pending = (await Call(f, draft.Id, Command(draft))).Value!;
        await SeedDecision(f, "Approved");
        await MoveClock(f, DateTimeOffset.Parse("2026-10-10T17:00:00Z"));
        var command = Command(pending) with { ExpectedVersion = 4 };
        var result = await Call(f, draft.Id, command, true);
        Assert.Equal(200, result.Status); Assert.Equal(CorrectionState.PendingSupervisor, result.Value!.State);
        Assert.Equal(2, result.Value.CurrentRevision); Assert.Equal(draft.DeadlineUtc, result.Value.DeadlineUtc);
        Assert.Equal(draft.Proposal, result.Value.Proposal);
        Assert.Equal(200, (await Call(f, draft.Id, command, true)).Status);
        await using var db = f.Data.Database.CreateContext();
        Assert.Equal(2, await db.Set<CorrectionRouteSnapshot>().CountAsync());
        Assert.Equal(1, (await db.Set<CorrectionApprovalAction>().SingleAsync()).Revision);
        Assert.Equal(2, await db.Set<CorrectionRevision>().CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Returned_resubmit_uses_new_route_revision_or_expires(bool expired)
    {
        await using var f = await Fixture(); var draft = await Draft(f);
        await Call(f, draft.Id, Command(draft)); await SeedDecision(f, "Returned");
        if (expired) await MoveClock(f, DateTimeOffset.Parse("2026-10-03T17:00:00Z"));
        var result = await Call(f, draft.Id, Command(draft) with { ExpectedVersion = 4 });
        Assert.Equal(expired ? 400 : 200, result.Status);
        await using var db = f.Data.Database.CreateContext(); var row = await db.Set<CorrectionRequest>().SingleAsync();
        Assert.Equal(expired ? CorrectionState.Expired : CorrectionState.PendingSupervisor, row.State);
        Assert.Equal(expired ? 1 : 2, row.CurrentRevision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revocation_during_lock_wait_prevents_write_and_receipt_replay(bool replay)
    {
        await using var f = await Fixture(); var draft = await Draft(f);
        var pending = (await Call(f, draft.Id, Command(draft))).Value!;
        var command = Command(pending);
        if (replay) Assert.Equal(200, (await Call(f, draft.Id, command, true)).Status);
        await using var db = f.Data.Database.CreateContext(); await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        var waiting = Call(f, draft.Id, command, true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await f.WaitForIdentityWaiter(timeout.Token);
        await db.Database.ExecuteSqlRawAsync("UPDATE hr_assignments SET valid_to_utc='2026-09-25T17:00:00Z',ended_by=user_id,version=version+1");
        await tx.CommitAsync(); Assert.Equal(403, (await waiting).Status);
        Assert.Equal(replay ? 2 : 1, await db.Set<CorrectionRouteSnapshot>().CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Audit_failure_rolls_back_submission_or_reroute(bool reroute)
    {
        await using var f = await Fixture(); var draft = await Draft(f);
        if (reroute) draft = (await Call(f, draft.Id, Command(draft))).Value!;
        await using var db = f.Data.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION test_route_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic'; END $$; CREATE TRIGGER test_route_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION test_route_audit();");
        Assert.Equal(503, (await Call(f, draft.Id, Command(draft), reroute)).Status);
        Assert.Equal(reroute ? 1 : 0, await db.Set<CorrectionRouteSnapshot>().CountAsync());
        Assert.Equal(draft.Version, (await db.Set<CorrectionRequest>().SingleAsync()).Version);
        Assert.Equal(reroute ? 2 : 1, await db.Set<CorrectionCommandReceipt>().CountAsync());
        Assert.Single(await db.Set<CorrectionRevision>().ToArrayAsync());
    }

    [Fact]
    public async Task Concurrent_submission_has_one_winner_and_pending_never_expires()
    {
        await using var f = await Fixture(); var draft = await Draft(f);
        var results = await Task.WhenAll(Call(f, draft.Id, Command(draft)), Call(f, draft.Id, Command(draft)));
        Assert.Equal(new[] { 200, 409 }, results.Select(x => x.Status).OrderBy(x => x));
        var pending = results.Single(x => x.Status == 200).Value!;
        await MoveClock(f, DateTimeOffset.Parse("2026-10-10T17:00:00Z"));
        Assert.Equal(409, (await Call(f, draft.Id, Command(pending))).Status);
        await using var db = f.Data.Database.CreateContext();
        Assert.Equal(CorrectionState.PendingSupervisor, (await db.Set<CorrectionRequest>().SingleAsync()).State);
        Assert.Single(await db.Set<CorrectionRouteSnapshot>().ToArrayAsync());
    }

    [Fact]
    public async Task Blocked_reroute_does_not_override_current_grants_or_mutate_old_route()
    {
        await using var f = await Fixture(); var draft = await Draft(f);
        var pending = (await Call(f, draft.Id, Command(draft))).Value!;
        await using var db = f.Data.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM role_permissions WHERE permission_id='20000000-0000-0000-0000-000000000016'");
        Assert.Equal(409, (await Call(f, draft.Id, Command(pending), true)).Status);
        Assert.Equal(pending.Version, (await db.Set<CorrectionRequest>().SingleAsync()).Version);
        Assert.Single(await db.Set<CorrectionRouteSnapshot>().ToArrayAsync());
        Assert.Single(await db.Set<CorrectionRevision>().ToArrayAsync());
    }

    [Theory]
    [InlineData(false, 403)]
    [InlineData(true, 401)]
    public async Task Submit_rechecks_record_grant_and_session_after_wait(bool session, int expected)
    {
        await using var f = await Fixture(); var draft = await Draft(f);
        await using var db = f.Data.Database.CreateContext(); await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        var waiting = Call(f, draft.Id, Command(draft));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await f.WaitForIdentityWaiter(timeout.Token);
        if (session) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sessions SET revoked_at_utc={f.Clock.GetUtcNow()} WHERE id={CaptureFixture.Session}");
        else await db.Database.ExecuteSqlRawAsync("DELETE FROM role_permissions WHERE permission_id='20000000-0000-0000-0000-000000000013'");
        await tx.CommitAsync(); Assert.Equal(expected, (await waiting).Status);
        Assert.Empty(await db.Set<CorrectionRouteSnapshot>().ToArrayAsync());
    }

    [Theory]
    [InlineData("stale-version", 409)]
    [InlineData("stale-revision", 409)]
    [InlineData("empty-key", 400)]
    [InlineData("draft-reroute", 409)]
    [InlineData("missing-reason", 400)]
    [InlineData("long-reason", 400)]
    [InlineData("other-owner", 404)]
    public async Task Invalid_commands_leave_request_unchanged(string fault, int expected)
    {
        await using var f = await Fixture(); var draft = await Draft(f); var command = Command(draft);
        if (fault == "stale-version") command = command with { ExpectedVersion = 1 };
        if (fault == "stale-revision") command = command with { Revision = 2 };
        if (fault == "empty-key") command = command with { CommandId = Guid.Empty };
        if (fault == "missing-reason") command = command with { Reason = "  " };
        if (fault == "long-reason") command = command with { Reason = new string('ก', 2001) };
        var reroute = fault is "draft-reroute" or "missing-reason" or "long-reason";
        var result = await Call(f, draft.Id, command, reroute, fault == "other-owner" ? HrSession : null);
        Assert.Equal(expected, result.Status);
        await using var db = f.Data.Database.CreateContext();
        Assert.Empty(await db.Set<CorrectionRouteSnapshot>().ToArrayAsync());
        Assert.Equal(draft.Version, (await db.Set<CorrectionRequest>().SingleAsync()).Version);
    }

    [Theory]
    [InlineData(false, 200)]
    [InlineData(true, 409)]
    public async Task Hr_requester_is_never_an_hr_candidate(bool noOtherHr, int expected)
    {
        await using var f = await Fixture(); var draft = await Draft(f);
        await using var db = f.Data.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO user_roles(user_id,role_id) VALUES ('00000000-0000-0000-0000-000000000031','00000000-0000-0000-0000-000000000083');
            INSERT INTO hr_assignments(id,user_id,workspace_id,department_id,valid_from_utc,created_by,reason)
            SELECT gen_random_uuid(),'00000000-0000-0000-0000-000000000031',workspace_id,department_id,valid_from_utc,created_by,reason FROM hr_assignments;
            UPDATE sessions SET mfa_verified_at_utc='2026-09-25T17:00:00Z' WHERE user_id='00000000-0000-0000-0000-000000000031';
            """);
        db.Add(new MfaFactor { Id = Guid.NewGuid(), UserId = CaptureFixture.Employee, ProtectedSecret = "synthetic-test-only", CreatedAtUtc = f.Clock.GetUtcNow(), ConfirmedAtUtc = f.Clock.GetUtcNow() });
        await db.SaveChangesAsync();
        if (noOtherHr) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE hr_assignments SET valid_to_utc={f.Clock.GetUtcNow()},ended_by=user_id,version=version+1 WHERE user_id={Hr}");
        Assert.Equal(expected, (await Call(f, draft.Id, Command(draft))).Status);
        if (!noOtherHr) Assert.Equal(new[] { Hr }, (await db.Set<CorrectionRouteSnapshot>().SingleAsync()).HrCandidateIds);
    }

    [Fact]
    public async Task Head_requester_routes_to_own_manager_not_to_their_subordinate()
    {
        await using var f = await Fixture(); var draft = await Draft(f);
        await using var db = f.Data.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO employee_memberships(id,user_id,workspace_id,department_id,valid_from_utc,created_by,reason)
            VALUES('00000000-0000-0000-0000-000000000043','00000000-0000-0000-0000-000000000033','00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000011','2026-09-01T00:00:00Z','00000000-0000-0000-0000-000000000032','ลูกทีม');
            INSERT INTO reporting_lines(id,employee_membership_id,employee_user_id,supervisor_user_id,valid_from_utc,created_by,reason)
            VALUES(gen_random_uuid(),'00000000-0000-0000-0000-000000000043','00000000-0000-0000-0000-000000000033','00000000-0000-0000-0000-000000000031','2026-09-01T00:00:00Z','00000000-0000-0000-0000-000000000032','สายงาน');
            """);
        Assert.Equal(200, (await Call(f, draft.Id, Command(draft))).Status);
        Assert.Equal(Manager, (await db.Set<CorrectionRouteSnapshot>().SingleAsync()).SupervisorId);
    }

    [Fact]
    public async Task Reroute_resolves_new_manager_and_keeps_full_reason_in_audit_linked_receipt()
    {
        await using var f = await Fixture(); var draft = await Draft(f);
        var pending = (await Call(f, draft.Id, Command(draft))).Value!;
        await using var db = f.Data.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO users(id,username,normalized_username) VALUES ('00000000-0000-0000-0000-000000000034','next','NEXT');
            INSERT INTO employee_memberships(id,user_id,workspace_id,department_id,valid_from_utc,created_by,reason)
            VALUES(gen_random_uuid(),'00000000-0000-0000-0000-000000000034','00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000011','2026-09-01T00:00:00Z','00000000-0000-0000-0000-000000000032','หัวหน้าใหม่');
            INSERT INTO user_roles(user_id,role_id) VALUES ('00000000-0000-0000-0000-000000000034','00000000-0000-0000-0000-000000000082');
            UPDATE reporting_lines SET valid_to_utc='2026-09-25T17:00:00Z',ended_by=employee_user_id,version=version+1;
            INSERT INTO reporting_lines(id,employee_membership_id,employee_user_id,supervisor_user_id,valid_from_utc,created_by,reason)
            VALUES(gen_random_uuid(),'00000000-0000-0000-0000-000000000041','00000000-0000-0000-0000-000000000031','00000000-0000-0000-0000-000000000034','2026-09-25T17:00:00Z','00000000-0000-0000-0000-000000000032','เปลี่ยนหัวหน้า');
            """);
        var command = Command(pending) with { Reason = new string('ก', 1000) + "\n" + new string('ข', 999) };
        Assert.Equal(200, (await Call(f, draft.Id, command, true)).Status);
        var route = await db.Set<CorrectionRouteSnapshot>().SingleAsync(x => x.Revision == 2);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000034"), route.SupervisorId);
        var receipt = await db.Set<CorrectionCommandReceipt>().SingleAsync(x => x.CommandId == command.CommandId);
        using var json = System.Text.Json.JsonDocument.Parse(receipt.ResultJson);
        Assert.Equal(command.Reason, json.RootElement.GetProperty("CommandReason").GetString());
        var audit = await db.AuditEvents.SingleAsync(x => x.EventType == "attendance.correction.reroute");
        Assert.True(await db.AuditMetadata.AnyAsync(x => x.AuditEventId == audit.Id && x.Key == "evidence-reference" && x.Value.Contains(command.CommandId.ToString())));
    }

    private async Task<CaptureFixture> Fixture()
    {
        var f = await CaptureFixture.CreateAsync(postgres.ConnectionString, s => { s.AddScoped<PermissionContext>(); s.AddScoped<PermissionMutationGuard>(); });
        await using var db = f.Data.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE user_scope_assignments SET created_at_utc='2026-09-01T00:00:00Z',version=version+1;
            INSERT INTO users(id,username,normalized_username,security_version) VALUES ('00000000-0000-0000-0000-000000000033','hr','HR',1);
            INSERT INTO employee_memberships(id,user_id,workspace_id,department_id,valid_from_utc,created_by,reason)
            VALUES('00000000-0000-0000-0000-000000000042','00000000-0000-0000-0000-000000000032','00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000011','2026-09-01T00:00:00Z','00000000-0000-0000-0000-000000000032','หัวหน้า');
            INSERT INTO reporting_lines(id,employee_membership_id,employee_user_id,supervisor_user_id,valid_from_utc,created_by,reason)
            VALUES(gen_random_uuid(),'00000000-0000-0000-0000-000000000041','00000000-0000-0000-0000-000000000031','00000000-0000-0000-0000-000000000032','2026-09-01T00:00:00Z','00000000-0000-0000-0000-000000000032','สายงาน');
            INSERT INTO roles(id,name,role_class) VALUES ('00000000-0000-0000-0000-000000000082','หัวหน้า','approval'),('00000000-0000-0000-0000-000000000083','บุคคล','accounting'),('00000000-0000-0000-0000-000000000084','จัดการ','system-administration');
            INSERT INTO role_permissions(role_id,permission_id) VALUES ('00000000-0000-0000-0000-000000000082','20000000-0000-0000-0000-000000000016'),('00000000-0000-0000-0000-000000000083','20000000-0000-0000-0000-000000000017'),('00000000-0000-0000-0000-000000000084','20000000-0000-0000-0000-000000000018');
            INSERT INTO user_roles(user_id,role_id) VALUES ('00000000-0000-0000-0000-000000000032','00000000-0000-0000-0000-000000000082'),('00000000-0000-0000-0000-000000000033','00000000-0000-0000-0000-000000000083'),('00000000-0000-0000-0000-000000000033','00000000-0000-0000-0000-000000000084');
            INSERT INTO hr_assignments(id,user_id,workspace_id,department_id,valid_from_utc,created_by,reason) VALUES(gen_random_uuid(),'00000000-0000-0000-0000-000000000033','00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000011','2026-09-01T00:00:00Z','00000000-0000-0000-0000-000000000032','บุคคล');
            INSERT INTO sessions(id,user_id,token_hash,stage,created_at_utc,last_seen_at_utc,expires_at_utc,mfa_verified_at_utc,security_version) VALUES ('00000000-0000-0000-0000-000000000072','00000000-0000-0000-0000-000000000033',decode(repeat('bc',32),'hex'),'Active','2026-09-25T16:00:00Z','2026-09-25T17:00:00Z','2026-09-26T17:00:00Z','2026-09-25T17:00:00Z',1);
            """);
        db.Add(new MfaFactor { Id = Guid.NewGuid(), UserId = Hr, ProtectedSecret = "synthetic-test-only", CreatedAtUtc = f.Clock.GetUtcNow(), ConfirmedAtUtc = f.Clock.GetUtcNow() });
        await db.SaveChangesAsync(); return f;
    }
    private static async Task<CorrectionView> Draft(CaptureFixture f)
    {
        await using var scope = await f.ScopeAsync();
        var service = ActivatorUtilities.CreateInstance<CorrectionDraftService>(scope.ServiceProvider);
        var p = new CorrectionProposal(new(2026, 9, 25), DateTimeOffset.Parse("2026-09-25T01:00:00Z"), null,
            new(CaptureFixture.Site.WorkspaceId, CaptureFixture.Site.ProjectId!.Value, CaptureFixture.Site.SiteId!.Value), null, "ลืมลงเวลา");
        var result = await service.CreateAsync(new(null, new(2026, 9, 25), null, p), Guid.NewGuid(), default);
        Assert.Equal(201, result.Status); return result.Value!;
    }
    private static CorrectionCommand Command(CorrectionView v) => new(Guid.NewGuid(), v.Version, v.CurrentRevision, "ปรับเส้นทางตามสายงาน");
    private static async Task<CorrectionResult<CorrectionView>> Call(CaptureFixture f, Guid id, CorrectionCommand command, bool reroute = false, Guid? session = null)
    {
        var type = typeof(CorrectionRequest).Assembly.GetType("TPR10.Api.Attendance.Corrections.CorrectionSubmission");
        Assert.NotNull(type);
        await using var scope = await f.ScopeAsync(session ?? (reroute ? HrSession : null));
        var service = ActivatorUtilities.CreateInstance(scope.ServiceProvider, type);
        return await (Task<CorrectionResult<CorrectionView>>)type.GetMethod(reroute ? "RerouteAsync" : "SubmitAsync")!.Invoke(service, [id, command, CancellationToken.None])!;
    }
    private static async Task MoveClock(CaptureFixture f, DateTimeOffset at)
    {
        f.Clock.Advance(at - f.Clock.GetUtcNow()); await using var db = f.Data.Database.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sessions SET last_seen_at_utc={at.ToUniversalTime()},expires_at_utc={at.AddDays(1).ToUniversalTime()},mfa_verified_at_utc=CASE WHEN mfa_verified_at_utc IS NULL THEN NULL ELSE {at.ToUniversalTime()} END");
    }
    private static async Task SeedDecision(CaptureFixture f, string outcome)
    {
        await using var db = f.Data.Database.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO correction_approval_actions(request_id,revision,step,actor_id,outcome,reason,occurred_at_utc)
            SELECT id,current_revision,'Supervisor',{Manager},{outcome},'ตรวจทาน','2026-09-25T17:00:00Z' FROM correction_requests;
            """);
        if (outcome == "Returned") await db.Database.ExecuteSqlRawAsync("UPDATE correction_requests SET state='Returned',deadline_utc='2026-10-03T17:00:00Z',version=version+1");
        else await db.Database.ExecuteSqlRawAsync("UPDATE correction_requests SET state='PendingHr',version=version+1");
    }
}
