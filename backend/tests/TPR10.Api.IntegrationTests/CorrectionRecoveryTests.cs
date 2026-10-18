using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Attendance.Corrections;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Attendance.History;
using TPR10.Api.Attendance.Recording;
using static TPR10.Api.IntegrationTests.CorrectionApprovalTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database"), UnsupportedOSPlatform("windows")]
public sealed class CorrectionRecoveryTests(PostgresFixture postgres)
{
    // Catches recovery losing original bytes/events, revision routes or either approval,
    // and readers conflating original capture with the restored effective adjustment.
    [Fact]
    public async Task Restore_preserves_original_evidence_revisions_two_approvers_and_effective_times()
    {
        await using var f = await RecordingHttpFixture.CreateAsync(postgres.ConnectionString);
        var capture = f.Recording.Capture;
        await CorrectionRoutingTests.SeedRoute(capture);
        await AddHeadSession(capture);
        var entry = await f.Recording.CheckIn();
        await CorrectionRoutingTests.MoveClock(capture, capture.Clock.GetUtcNow().AddHours(1));
        await f.Recording.RefreshAsync();
        var exit = await f.Recording.Record(EvidenceAction.CheckOut, await capture.Metadata(EvidenceAction.CheckOut));
        Assert.Equal(201, exit.Status);
        await using var db = capture.Data.Database.CreateContext();
        var original = JsonSerializer.Serialize(await db.Set<AttendanceEvent>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync());
        var site = new CorrectionSite(CaptureFixture.Site.WorkspaceId, CaptureFixture.Site.ProjectId!.Value, CaptureFixture.Site.SiteId!.Value);
        var proposal = new CorrectionProposal(entry.WorkDate, entry.OccurredAtUtc.AddMinutes(5), exit.Value!.OccurredAtUtc.AddMinutes(-5), site, site, "ทดสอบกู้คืนเวลาที่แก้ไข");
        CorrectionView draft;
        await using (var scope = await capture.ScopeAsync())
        {
            var created = await ActivatorUtilities.CreateInstance<CorrectionDraftService>(scope.ServiceProvider)
                .CreateAsync(new(entry.DayId, entry.WorkDate, 2, proposal), Guid.NewGuid(), default);
            Assert.Equal(201, created.Status);
            draft = created.Value!;
        }
        var pending = await Submit(capture, draft);
        var returned = await Decide(capture, pending, "return", command: Command(pending) with { Reason = "ขอรายละเอียดเพิ่มเติม" });
        Assert.Equal(200, returned.Status);
        await using (var scope = await capture.ScopeAsync())
        {
            var saved = await ActivatorUtilities.CreateInstance<CorrectionDraftService>(scope.ServiceProvider)
                .SaveAsync(draft.Id, new(entry.DayId, entry.WorkDate, 2, proposal with { Reason = "ยืนยันรายละเอียดสำหรับกู้คืน" }), Command(returned.Value!), default);
            Assert.Equal(200, saved.Status);
            draft = saved.Value!;
        }
        pending = await Submit(capture, draft);
        Assert.Equal(2, pending.CurrentRevision);
        pending = (await Decide(capture, pending, "supervisor-approve")).Value!;
        var approved = await Decide(capture, pending, "hr-approve");
        Assert.Equal(200, approved.Status);
        Assert.Equal(CorrectionState.Approved, approved.Value!.State);

        var expected = new Dictionary<string, byte[]>();
        foreach (var id in new[] { entry.EvidenceId, exit.Value.EvidenceId })
            foreach (var suffix in new[] { "", "/thumbnail", "/download" })
            {
                var url = $"/api/v1/attendance/evidence/{id}{suffix}";
                using var read = await f.Client.GetAsync(url);
                Assert.Equal(HttpStatusCode.OK, read.StatusCode);
                expected[url] = await read.Content.ReadAsByteArrayAsync();
            }
        var copies = await db.Set<EvidenceLocation>().AsNoTracking().ToArrayAsync();
        Assert.Equal(4, copies.Length);
        await f.Factory.DisposeAsync();
        await using var restored = new IdentityDatabase(postgres.ConnectionString);
        await restored.InitializeAsync();
        await postgres.BackupRestoreAsync(capture.Data.Database.ConnectionString, restored.ConnectionString);
        // Only synthetic files under this disposable fixture are moved. DB backup alone is insufficient.
        foreach (var copy in copies)
        {
            var path = Path.Combine(f.Recording.Storage.Root, "volume-0", copy.ObjectKey);
            File.Move(path, path + ".correction-recovery");
        }
        await using var host = new ApiFactory(restored.ConnectionString, "Production", capture.Clock, f.Recording.Storage.Settings);
        using var owner = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443") });
        owner.DefaultRequestHeaders.Add("Cookie", f.Client.DefaultRequestHeaders.GetValues("Cookie"));
        using (var missing = await owner.GetAsync(expected.Keys.First()))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, missing.StatusCode);
            Assert.DoesNotContain("image/", missing.Content.Headers.ContentType?.ToString() ?? "");
        }
        foreach (var copy in copies)
        {
            var path = Path.Combine(f.Recording.Storage.Root, "volume-0", copy.ObjectKey);
            File.Move(path + ".correction-recovery", path);
            Assert.Equal(copy.Sha256, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))));
        }
        using var anonymous = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443") });
        foreach (var (url, bytes) in expected)
        {
            using var read = await owner.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal(bytes, await read.Content.ReadAsByteArrayAsync());
            Assert.True(read.Headers.CacheControl?.NoStore);
            using var denied = await anonymous.GetAsync(url);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        using var detail = await owner.GetAsync($"/api/v1/attendance/corrections/{draft.Id}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var view = (await detail.Content.ReadFromJsonAsync<CorrectionView>())!;
        Assert.Equal(CorrectionState.Approved, view.State);
        Assert.Equal(2, view.CurrentRevision);
        Assert.Equal(2, view.History!.Revisions.Length);
        Assert.Equal(2, view.History.Routes.Length);
        Assert.Equal("ขอรายละเอียดเพิ่มเติม", Assert.Single(view.History.Approvals, x => x.Revision == 1).Reason);
        var decisions = view.History.Approvals.Where(x => x.Revision == 2).ToArray();
        Assert.Equal(2, decisions.Length);
        Assert.Equal(Head, Assert.Single(decisions, x => x.Step == "Supervisor").ActorId);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000033"), Assert.Single(decisions, x => x.Step == "Hr").ActorId);
        Assert.Equal(entry.OccurredAtUtc, view.Comparison!.Original!.InUtc);
        Assert.Equal(exit.Value.OccurredAtUtc, view.Comparison.Original.OutUtc);
        Assert.Equal(proposal.InUtc, view.Comparison.Current!.InUtc);
        Assert.Equal(proposal.OutUtc, view.Comparison.Current.OutUtc);
        using var historyResponse = await owner.GetAsync($"/api/v1/attendance/days/{entry.DayId}");
        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        var history = (await historyResponse.Content.ReadFromJsonAsync<AttendanceDayView>())!;
        Assert.Equal(proposal.InUtc, history.EffectiveInUtc);
        Assert.Equal(proposal.OutUtc, history.EffectiveOutUtc);
        Assert.Equal(entry.EvidenceId, history.In!.Photo!.EvidenceId);
        Assert.Equal(exit.Value.EvidenceId, history.Out!.Photo!.EvidenceId);
        var change = Assert.Single(history.Adjustments);
        Assert.Equal(draft.Id, change.RequestId);
        Assert.Equal(2, change.Revision);
        await using var recoveredDb = restored.CreateContext();
        Assert.Equal(original, JsonSerializer.Serialize(await recoveredDb.Set<AttendanceEvent>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync()));
        Assert.Equal(2, await recoveredDb.Set<EvidenceBinding>().CountAsync());
        var adjustment = await recoveredDb.Set<AttendanceAdjustment>().SingleAsync();
        Assert.Equal(entry.DayId, adjustment.DayId);
        Assert.Equal(draft.Id, adjustment.RequestId);
        Assert.Equal(2, adjustment.BeforeVersion);
        Assert.Equal(3, adjustment.AfterVersion);
        Assert.Equal(entry.OccurredAtUtc, adjustment.BeforeInUtc);
        Assert.Equal(exit.Value.OccurredAtUtc, adjustment.BeforeOutUtc);
        Assert.Equal(1, await recoveredDb.AuditEvents.CountAsync(x => x.EventType == "attendance.correction.hr-approve"));
    }
}
