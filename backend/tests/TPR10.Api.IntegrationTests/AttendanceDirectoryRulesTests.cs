using TPR10.Api.Attendance.Directory;

namespace TPR10.Api.IntegrationTests;

public sealed class AttendanceDirectoryRulesTests
{
    [Fact]
    public void End_boundary_is_excluded_and_returning_to_unit_does_not_reopen_old_interval()
    {
        var start = DateTimeOffset.Parse("2026-09-25T00:00:00Z");
        var end = start.AddDays(1);
        Assert.True(DirectoryRules.Contains(start, end, start));
        Assert.True(DirectoryRules.Contains(start, end, end.AddTicks(-1)));
        Assert.False(DirectoryRules.Contains(start, end, end));
        Assert.False(DirectoryRules.Contains(end.AddDays(1), null, start));
        Assert.True(DirectoryRules.Contains(start, null, end.AddYears(20)));
        Assert.False(DirectoryRules.Contains(start, start, start));
    }

    [Fact]
    public void Proposed_manager_chain_must_not_reach_employee_or_an_existing_cycle()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        Assert.True(DirectoryRules.CreatesCycle(a, b, new Dictionary<Guid, Guid> { { b, c }, { c, a } }));
        Assert.True(DirectoryRules.CreatesCycle(a, a, new Dictionary<Guid, Guid>()));
        Assert.True(DirectoryRules.CreatesCycle(a, b, new Dictionary<Guid, Guid> { { b, c }, { c, b } }));
        Assert.False(DirectoryRules.CreatesCycle(a, b, new Dictionary<Guid, Guid> { { b, c } }));
    }

    [Fact]
    public void Empty_identity_fails_closed_including_inside_manager_chain()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        Assert.True(DirectoryRules.CreatesCycle(Guid.Empty, b, new Dictionary<Guid, Guid>()));
        Assert.True(DirectoryRules.CreatesCycle(a, Guid.Empty, new Dictionary<Guid, Guid>()));
        Assert.True(DirectoryRules.CreatesCycle(a, b, new Dictionary<Guid, Guid> { { b, Guid.Empty } }));
    }

    [Fact]
    public void Long_chain_is_iterative_and_does_not_reuse_an_ended_relation()
    {
        var ids = Enumerable.Range(0, 1001).Select(_ => Guid.NewGuid()).ToArray();
        var graph = Enumerable.Range(0, 1000).ToDictionary(i => ids[i], i => ids[i + 1]);
        var employee = Guid.NewGuid();
        Assert.False(DirectoryRules.CreatesCycle(employee, ids[0], graph));
        graph[ids[1000]] = employee;
        Assert.True(DirectoryRules.CreatesCycle(employee, ids[0], graph));
        graph.Remove(ids[1000]); // The caller's current graph excludes the ended relation.
        Assert.False(DirectoryRules.CreatesCycle(employee, ids[0], graph));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("\u2003", false)]
    [InlineData("ย้ายแผนก", true)]
    [InlineData("  เปลี่ยนหัวหน้า  ", true)]
    [InlineData("bad\nreason", false)]
    [InlineData("bad\0reason", false)]
    [InlineData("\treason", false)]
    public void Reason_is_trimmed_bounded_and_without_controls(string? reason, bool expected) =>
        Assert.Equal(expected, DirectoryRules.ValidReason(reason));

    [Fact]
    public void Reason_rejects_malformed_utf16_and_limits_unicode_scalars()
    {
        Assert.False(DirectoryRules.ValidReason("broken\ud800"));
        Assert.False(DirectoryRules.ValidReason("\udc00broken"));
        Assert.True(DirectoryRules.ValidReason(new string('ก', 500)));
        Assert.False(DirectoryRules.ValidReason(new string('ก', 501)));
        Assert.True(DirectoryRules.ValidReason(string.Concat(Enumerable.Repeat("😀", 500))));
        Assert.False(DirectoryRules.ValidReason(string.Concat(Enumerable.Repeat("😀", 501))));
    }
}
