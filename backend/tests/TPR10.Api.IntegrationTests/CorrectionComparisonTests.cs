using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Attendance.Corrections;
using static TPR10.Api.IntegrationTests.CorrectionApprovalTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class CorrectionComparisonTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Pending_workflow_can_compare_only_referenced_day_without_evidence_or_gps()
    {
        await using var f = await new CorrectionApprovalTests(postgres).Fixture();
        var day = await new AttendanceHistoryFixture(f).Day(CaptureFixture.Employee, 25);
        var site = new CorrectionSite(CaptureFixture.Site.WorkspaceId, CaptureFixture.Site.ProjectId!.Value, CaptureFixture.Site.SiteId!.Value);
        CorrectionView draft;
        await using (var scope = await f.ScopeAsync())
            draft = (await ActivatorUtilities.CreateInstance<CorrectionDraftService>(scope.ServiceProvider).CreateAsync(
                new(day, new(2026, 9, 25), 2, new(new(2026, 9, 25), DateTimeOffset.Parse("2026-09-25T11:00:00Z"),
                    DateTimeOffset.Parse("2026-09-25T14:00:00Z"), site, site, "ตรวจเวลา")), Guid.NewGuid(), default)).Value!;
        var v = await Submit(f, draft);
        var read = await CorrectionReadTests.Read(f, "GetAsync", HeadSession, v.Id);
        Assert.Equal(200, read.Status);
        Assert.True(read.Value.TryGetProperty("comparison", out var comparison), "detail must include authorized comparison");
        Assert.Equal(day, comparison.GetProperty("dayId").GetGuid());
        Assert.True(comparison.GetProperty("baselineMatches").GetBoolean());
        var current = comparison.GetProperty("current");
        Assert.Equal(2, current.GetProperty("version").GetInt64());
        Assert.Equal(DateTimeOffset.Parse("2026-09-25T12:00:00Z"), current.GetProperty("inUtc").GetDateTimeOffset());
        Assert.Equal(DateTimeOffset.Parse("2026-09-25T13:00:00Z"), current.GetProperty("outUtc").GetDateTimeOffset());
        Assert.Equal(site.SiteId, current.GetProperty("inSite").GetProperty("siteId").GetGuid());
        Assert.DoesNotContain("evidence", comparison.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("latitude", comparison.ToString());
        Assert.DoesNotContain("photo", comparison.ToString(), StringComparison.OrdinalIgnoreCase);
        var list = await CorrectionReadTests.Read(f, "ListAsync", HeadSession, "supervisor", 1, 20);
        Assert.False(list.Value.GetProperty("items")[0].TryGetProperty("comparison", out _));
        v = (await Decide(f, v, "supervisor-approve")).Value!;
        Assert.Equal(404, (await CorrectionReadTests.Read(f, "GetAsync", HeadSession, v.Id)).Status);
        Assert.Equal(200, (await CorrectionReadTests.Read(f, "GetAsync", HrSession, v.Id)).Status);
        Assert.Equal(200, (await Decide(f, v, "hr-approve")).Status);
        Assert.Equal(404, (await CorrectionReadTests.Read(f, "GetAsync", HrSession, v.Id)).Status);
    }

    [Fact]
    public async Task Missing_pair_comparison_is_explicit_null_not_synthetic_capture()
    {
        await using var f = await new CorrectionApprovalTests(postgres).Fixture(); var v = await Pending(f);
        var read = await CorrectionReadTests.Read(f, "GetAsync", HeadSession, v.Id);
        Assert.True(read.Value.TryGetProperty("comparison", out var comparison), "detail must include comparison");
        Assert.Equal(JsonValueKind.Null, comparison.GetProperty("dayId").ValueKind);
        Assert.Equal(JsonValueKind.Null, comparison.GetProperty("current").ValueKind);
        Assert.Equal(JsonValueKind.Null, comparison.GetProperty("original").ValueKind);
        Assert.True(comparison.GetProperty("baselineMatches").GetBoolean());
    }

    [Fact]
    public async Task Approved_missing_pair_resolves_its_adjustment_day_without_fabricating_original()
    {
        await using var f = await new CorrectionApprovalTests(postgres).Fixture(); var v = await Pending(f);
        v = (await Decide(f, v, "supervisor-approve")).Value!;
        Assert.Equal(200, (await Decide(f, v, "hr-approve")).Status);
        var read = await CorrectionReadTests.Read(f, "GetAsync", null, v.Id);
        var comparison = read.Value.GetProperty("comparison");
        Assert.Equal(JsonValueKind.Object, comparison.GetProperty("current").ValueKind);
        Assert.Equal(v.Proposal.InUtc, comparison.GetProperty("current").GetProperty("inUtc").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, comparison.GetProperty("original").ValueKind);
    }
}
