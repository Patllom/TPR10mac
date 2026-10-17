using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Attendance.Corrections;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Attendance.History;
using TPR10.Api.Attendance.Recording;
using static TPR10.Api.IntegrationTests.CorrectionApprovalTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class CorrectionReadTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("original")]
    [InlineData("open")]
    [InlineData("closed")]
    public async Task Approved_history_preserves_live_events_or_explicitly_has_no_capture(string mode)
    {
        var original = mode == "original";
        await using var f = await new CorrectionApprovalTests(postgres).Fixture();
        Guid? dayId = original ? await new AttendanceHistoryFixture(f).Day(CaptureFixture.Employee, 25) : null;
        CorrectionView v;
        if (dayId is null)
        {
            v = await CorrectionRoutingTests.Draft(f);
            if (mode == "closed")
            {
                await using var scope = await f.ScopeAsync();
                v = (await ActivatorUtilities.CreateInstance<CorrectionDraftService>(scope.ServiceProvider).SaveAsync(v.Id,
                    new(null, v.OriginalWorkDate, null, v.Proposal with { OutUtc = v.Proposal.InUtc.AddHours(8), OutSite = v.Proposal.InSite }), Command(v), default)).Value!;
            }
            v = await Submit(f, v);
        }
        else
        {
            await using var scope = await f.ScopeAsync();
            var site = new CorrectionSite(CaptureFixture.Site.WorkspaceId, CaptureFixture.Site.ProjectId!.Value, CaptureFixture.Site.SiteId!.Value);
            var proposal = new CorrectionProposal(new(2026, 9, 25), DateTimeOffset.Parse("2026-09-25T11:00:00Z"), DateTimeOffset.Parse("2026-09-25T14:00:00Z"), site, site, "แก้ไขเวลา");
            var draft = await ActivatorUtilities.CreateInstance<CorrectionDraftService>(scope.ServiceProvider).CreateAsync(new(dayId, proposal.WorkDate, 2, proposal), Guid.NewGuid(), default);
            Assert.Equal(201, draft.Status); v = await Submit(f, draft.Value!);
        }
        v = (await Decide(f, v, "supervisor-approve")).Value!;
        Assert.Equal(200, (await Decide(f, v, "hr-approve")).Status);
        await using var db = f.Data.Database.CreateContext(); var day = await db.Set<AttendanceDay>().SingleAsync();
        await using var own = await f.ScopeAsync();
        var history = ActivatorUtilities.CreateInstance<AttendanceHistory>(own.ServiceProvider, new EphemeralDataProtectionProvider());
        var result = await history.GetAsync(day.Id, default); Assert.Equal(200, result.Status);
        var json = JsonSerializer.SerializeToElement(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(v.Proposal.InUtc, json.GetProperty("effectiveInUtc").GetDateTimeOffset());
        Assert.Equal(v.Id, json.GetProperty("adjustments")[0].GetProperty("requestId").GetGuid());
        Assert.Equal(v.Proposal.InSite.SiteId, json.GetProperty("effectiveInSite").GetProperty("siteId").GetGuid());
        if (original)
        {
            Assert.Equal(DateTimeOffset.Parse("2026-09-25T12:00:00Z"), json.GetProperty("in").GetProperty("occurredAtUtc").GetDateTimeOffset());
            Assert.Equal(DateTimeOffset.Parse("2026-09-25T13:00:00Z"), json.GetProperty("out").GetProperty("occurredAtUtc").GetDateTimeOffset());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, json.GetProperty("in").ValueKind); Assert.Equal(JsonValueKind.Null, json.GetProperty("out").ValueKind);
            Assert.DoesNotContain("evidenceId", json.ToString()); Assert.DoesNotContain("latitude", json.ToString());
            Assert.Equal(200, (await history.CurrentAsync(default)).Status);
        }
        Assert.Equal(200, (await history.ListAsync(AttendanceAudience.Mine, new(new(2026, 9, 1), new(2026, 9, 26)), default)).Status);
        await db.Database.ExecuteSqlRawAsync("INSERT INTO role_permissions(role_id,permission_id) VALUES ('00000000-0000-0000-0000-000000000082','20000000-0000-0000-0000-000000000014')");
        await using var head = await f.ScopeAsync(HeadSession);
        var team = await ActivatorUtilities.CreateInstance<AttendanceHistory>(head.ServiceProvider, new EphemeralDataProtectionProvider()).GetAsync(day.Id, default);
        Assert.Equal(200, team.Status);
        var teamJson = JsonSerializer.Serialize(team.Value);
        Assert.DoesNotContain("EvidenceId", teamJson); Assert.DoesNotContain("/evidence/", teamJson);
        Assert.Equal(v.Id, team.Value!.Adjustments[0].RequestId);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Correction_only_then_live_checkout_reads_real_out_site_without_fake_in()
    {
        await using var f = await OnlineRecordingFixture.CreateAsync(postgres.ConnectionString);
        await CorrectionRoutingTests.SeedRoute(f.Capture); await AddHeadSession(f.Capture);
        var v = await Pending(f.Capture); v = (await Decide(f.Capture, v, "supervisor-approve")).Value!;
        Assert.Equal(200, (await Decide(f.Capture, v, "hr-approve")).Status);
        var metadata = await f.Capture.Metadata(TPR10.Api.Attendance.Evidence.EvidenceAction.CheckOut);
        var result = await f.Record(TPR10.Api.Attendance.Evidence.EvidenceAction.CheckOut, metadata with { ConfirmContinuousWork = true });
        Assert.Equal(201, result.Status);
        await using var scope = await f.Capture.ScopeAsync();
        var read = await ActivatorUtilities.CreateInstance<AttendanceHistory>(scope.ServiceProvider, new EphemeralDataProtectionProvider()).GetAsync(result.Value!.DayId, default);
        Assert.Equal(200, read.Status); Assert.Null(read.Value!.In); Assert.NotNull(read.Value.Out);
        Assert.Equal(read.Value.Out.Site, read.Value.EffectiveOutSite); Assert.Equal(result.Value.OccurredAtUtc, read.Value.EffectiveOutUtc);
        Assert.Equal(v.Id, read.Value.Adjustments[0].RequestId);
    }

    [Fact]
    public async Task Queue_filters_current_route_and_unit_before_pagination_and_count()
    {
        await using var f = await new CorrectionApprovalTests(postgres).Fixture(); var v = await Pending(f);
        await OtherUnit(f, v);
        var page = await Read(f, "ListAsync", HeadSession, "supervisor", 1, 1);
        Assert.Equal(200, page.Status); Assert.Equal(1, page.Value.GetProperty("total").GetInt32());
        Assert.Equal(v.Id, page.Value.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Empty((await Read(f, "ListAsync", HeadSession, "supervisor", 2, 1)).Value.GetProperty("items").EnumerateArray());
        var hr = await Read(f, "ListAsync", HrSession, "hr", 1, 20);
        Assert.Equal(0, hr.Value.GetProperty("total").GetInt32());
        v = (await Decide(f, v, "supervisor-approve")).Value!;
        Assert.Equal(1, (await Read(f, "ListAsync", HrSession, "hr", 1, 20)).Value.GetProperty("total").GetInt32());
        Assert.Equal(0, (await Read(f, "ListAsync", HeadSession, "supervisor", 1, 20)).Value.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Workflow_read_is_request_only_and_ends_when_no_decision_is_due()
    {
        await using var f = await new CorrectionApprovalTests(postgres).Fixture(); var v = await Pending(f);
        var read = await Read(f, "GetAsync", HeadSession, v.Id);
        Assert.Equal(200, read.Status); Assert.Equal("WorkflowSupervisor", read.Value.GetProperty("readBasis").GetString());
        Assert.Contains("supervisor-approve", read.Value.GetProperty("actions").EnumerateArray().Select(x => x.GetString()));
        Assert.DoesNotContain("evidence", read.Value.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("latitude", read.Value.ToString());
        v = (await Decide(f, v, "supervisor-approve")).Value!;
        Assert.Equal(404, (await Read(f, "GetAsync", HeadSession, v.Id)).Status);
        Assert.Equal(200, (await Read(f, "GetAsync", HrSession, v.Id)).Status);
        Assert.Equal(200, (await Decide(f, v, "hr-approve")).Status);
        Assert.Equal(404, (await Read(f, "GetAsync", HrSession, v.Id)).Status);
        await using var scope = await f.ScopeAsync(HeadSession);
        Assert.Equal(403, (await ActivatorUtilities.CreateInstance<AttendanceHistory>(scope.ServiceProvider, new EphemeralDataProtectionProvider()).ListAsync(
            AttendanceAudience.Team, new(new(2026, 9, 1), new(2026, 9, 26)), default)).Status);
    }

    [Theory]
    [InlineData("wrong-unit")]
    [InlineData("admin-only")]
    [InlineData("revoked")]
    public async Task Cross_scope_requests_are_concealed(string fault)
    {
        await using var f = await new CorrectionApprovalTests(postgres).Fixture(); var v = await Pending(f);
        v = (await Decide(f, v, "supervisor-approve")).Value!;
        await using var db = f.Data.Database.CreateContext();
        if (fault == "wrong-unit") await db.Database.ExecuteSqlRawAsync("UPDATE hr_assignments SET workspace_id='00000000-0000-0000-0000-000000000002',department_id='00000000-0000-0000-0000-000000000012',version=version+1");
        if (fault == "admin-only") await db.Database.ExecuteSqlRawAsync("DELETE FROM user_roles WHERE user_id='00000000-0000-0000-0000-000000000033' AND role_id='00000000-0000-0000-0000-000000000083'");
        if (fault == "revoked") await db.Database.ExecuteSqlRawAsync("UPDATE hr_assignments SET valid_to_utc='2026-09-25T17:00:00Z',ended_by=user_id,version=version+1");
        var result = await Read(f, "GetAsync", HrSession, v.Id);
        Assert.Equal(404, result.Status); Assert.Equal(JsonValueKind.Null, result.Value.ValueKind);
    }

    [Theory]
    [InlineData("other", 1, 20)]
    [InlineData("mine", 0, 20)]
    [InlineData("mine", -1, 20)]
    [InlineData("mine", 1, 0)]
    [InlineData("mine", 1, 101)]
    [InlineData("mine", int.MaxValue, 100)]
    public async Task Invalid_pagination_is_rejected(string audience, int page, int size)
    {
        await using var f = await new CorrectionApprovalTests(postgres).Fixture();
        Assert.Equal(400, (await Read(f, "ListAsync", null, audience, page, size)).Status);
    }

    [Fact]
    public async Task Owner_reads_expired_history_without_edit_actions()
    {
        await using var f = await new CorrectionApprovalTests(postgres).Fixture(); var draft = await CorrectionRoutingTests.Draft(f);
        await CorrectionRoutingTests.MoveClock(f, DateTimeOffset.Parse("2026-10-10T17:00:00Z"));
        var result = await Read(f, "GetAsync", null, draft.Id);
        Assert.Equal(200, result.Status); Assert.Equal((int)CorrectionState.Expired, result.Value.GetProperty("state").GetInt32());
        Assert.Empty(result.Value.GetProperty("actions").EnumerateArray());
        Assert.Single(result.Value.GetProperty("history").GetProperty("revisions").EnumerateArray());
    }

    [Fact]
    public async Task Detail_retains_prior_revisions_approvals_and_full_reroute_reason()
    {
        await using var f = await new CorrectionApprovalTests(postgres).Fixture(); var v = await Pending(f);
        v = (await Decide(f, v, "supervisor-approve")).Value!;
        var reason = new string('ก', 1000) + "\n" + new string('ข', 999);
        await using (var scope = await f.ScopeAsync(HrSession))
            Assert.Equal(200, (await ActivatorUtilities.CreateInstance<CorrectionSubmission>(scope.ServiceProvider)
                .RerouteAsync(v.Id, Command(v) with { Reason = reason }, default)).Status);
        var result = await Read(f, "GetAsync", null, v.Id);
        var history = result.Value.GetProperty("history");
        Assert.Equal(2, history.GetProperty("revisions").GetArrayLength());
        Assert.Single(history.GetProperty("approvals").EnumerateArray());
        Assert.Equal(reason, history.GetProperty("reroutes")[0].GetProperty("reason").GetString());
        Assert.False((await Read(f, "ListAsync", null, "mine", 1, 20)).Value.GetProperty("items")[0].TryGetProperty("history", out _));
    }

    [Fact]
    public async Task Draft_is_not_visible_to_other_readers_until_submitted()
    {
        await using var f = await new CorrectionApprovalTests(postgres).Fixture(); var v = await CorrectionRoutingTests.Draft(f);
        await using var db = f.Data.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("INSERT INTO role_permissions(role_id,permission_id) VALUES ('00000000-0000-0000-0000-000000000083','20000000-0000-0000-0000-000000000015')");
        Assert.Equal(404, (await Read(f, "GetAsync", HrSession, v.Id)).Status);
        v = await Submit(f, v); Assert.Equal(200, (await Read(f, "GetAsync", HrSession, v.Id)).Status);
    }

    internal static async Task<(int Status, JsonElement Value)> Read(CaptureFixture f, string method, Guid? session, params object[] args)
    {
        var type = typeof(CorrectionRequest).Assembly.GetType("TPR10.Api.Attendance.Corrections.CorrectionQueries"); Assert.NotNull(type);
        await using var scope = await f.ScopeAsync(session);
        var service = ActivatorUtilities.CreateInstance(scope.ServiceProvider, type);
        var task = (Task)type.GetMethod(method)!.Invoke(service, [.. args, CancellationToken.None])!; await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        return ((int)result.GetType().GetProperty("Status")!.GetValue(result)!,
            JsonSerializer.SerializeToElement(result.GetType().GetProperty("Value")!.GetValue(result), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static async Task OtherUnit(CaptureFixture f, CorrectionView source)
    {
        await using var db = f.Data.Database.CreateContext();
        var original = await db.Set<CorrectionRequest>().AsNoTracking().SingleAsync(x => x.Id == source.Id);
        var member = new EmployeeMembership
        {
            Id = Guid.NewGuid(),
            UserId = Guid.Parse("00000000-0000-0000-0000-000000000033"),
            WorkspaceId = Guid.Parse("00000000-0000-0000-0000-000000000002"),
            DepartmentId = Guid.Parse("00000000-0000-0000-0000-000000000012"),
            ValidFromUtc = original.SnapshotAtUtc.AddDays(-1),
            CreatedBy = Head,
            Reason = "หน่วยงานอื่น"
        };
        db.Add(member); await db.SaveChangesAsync();
        var r = new CorrectionRequest
        {
            Id = Guid.NewGuid(),
            EmployeeId = member.UserId,
            OriginalWorkDate = original.OriginalWorkDate,
            MembershipId = member.Id,
            WorkspaceId = member.WorkspaceId,
            DepartmentId = member.DepartmentId,
            SnapshotAtUtc = original.SnapshotAtUtc,
            DeadlineAnchorUtc = original.DeadlineAnchorUtc,
            DeadlineUtc = original.DeadlineUtc
        };
        db.Add(r); await db.SaveChangesAsync();
        var revision = await db.Set<CorrectionRevision>().AsNoTracking().SingleAsync(x => x.RequestId == source.Id);
        revision.RequestId = r.Id; db.Add(revision); await db.SaveChangesAsync();
        db.Add(new CorrectionRouteSnapshot { RequestId = r.Id, Revision = 1, SupervisorId = Head, HrCandidateIds = [CaptureFixture.Employee], MembershipVersion = 1, ReportingVersion = 1, CreatedAtUtc = f.Clock.GetUtcNow() });
        r.CurrentRevision = 1; r.Version++; r.State = CorrectionState.PendingSupervisor; await db.SaveChangesAsync();
    }
}
