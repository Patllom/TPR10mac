using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Attendance.Access;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.Data;
using TPR10.Api.Identity.Data;
using TPR10.Api.Scopes;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Attendance.Recording;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class AttendanceHistoryTests(PostgresFixture postgres)
{
    [Fact]
    public async Task EqualWorkDatesUseStableIdCursorWithoutDuplicates()
    {
        await using var f = await AttendanceHistoryFixture.CreateAsync(postgres.ConnectionString);
        var a = await f.Day(CaptureFixture.Employee, 24);
        var b = await f.Day(AttendanceHistoryFixture.Manager, 24);
        var first = await f.List("Hr", 1);
        Assert.Equal(200, first.Status);
        var second = await f.List("Hr", 1, first.Value.GetProperty("nextCursor").GetString());
        Assert.Equal(200, second.Status);
        var ids = new[] { first, second }.Select(x => Assert.Single(x.Value.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid()).ToArray();
        Assert.Equal(new[] { a, b }.OrderDescending(), ids);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, second.Value.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task CurrentRecognizesCompletedBangkokWorkDate()
    {
        await using var f = await AttendanceHistoryFixture.CreateAsync(postgres.ConnectionString);
        await f.Day(CaptureFixture.Employee, 25);
        f.Capture.Clock.Advance(TimeSpan.FromSeconds(-1));
        var result = await f.Call("CurrentAsync", CaptureFixture.Session);
        Assert.Equal(200, result.Status);
        Assert.True(result.Value.GetProperty("todayCompleted").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, result.Value.GetProperty("openDay").ValueKind);
    }

    [Fact]
    public async Task IdentityLockTimeoutReturnsNoHistory()
    {
        await using var f = await AttendanceHistoryFixture.CreateAsync(postgres.ConnectionString);
        var day = await f.Day(CaptureFixture.Employee, 24);
        await using var db = f.Capture.Data.Database.CreateContext();
        await using var tx = await ScopeOperation.BeginAsync(db, default);
        var result = await f.Call("GetAsync", null, day);
        Assert.Equal(503, result.Status);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, result.Value.ValueKind);
    }

    [Fact]
    public async Task GrantExpiringDuringProjectionCannotBeReturned()
    {
        await using var f = await AttendanceHistoryFixture.CreateAsync(postgres.ConnectionString, "hr", services =>
            services.AddScoped<IAuditEventWriter>(s => new AdvancingAudit(s.GetRequiredService<Tpr10DbContext>(),
                s.GetRequiredService<ICorrelationContext>(), s.GetRequiredService<TimeProvider>())));
        var day = await f.Day(CaptureFixture.Employee, 24);
        await using var db = f.Capture.Data.Database.CreateContext(); var grant = await db.Set<HrAssignment>().SingleAsync();
        grant.ValidToUtc = f.Capture.Clock.GetUtcNow().AddSeconds(1); grant.EndedBy = grant.UserId; grant.Version++; await db.SaveChangesAsync();
        var result = await f.Call("GetAsync", null, day);
        Assert.Equal(404, result.Status); Assert.Equal(System.Text.Json.JsonValueKind.Null, result.Value.ValueKind);
    }
    private sealed class AdvancingAudit(Tpr10DbContext db, ICorrelationContext correlation, TimeProvider clock) : IAuditEventWriter
    {
        private readonly AuditEventWriter inner = new(db, correlation, clock);
        public Task WriteAsync(string eventType, Guid targetId, IReadOnlyDictionary<string, string> metadata, CancellationToken ct, Guid? actorId = null) =>
            inner.WriteAsync(eventType, targetId, metadata, ct, actorId);
        public async Task WriteAsync(SecurityAuditRequest request, CancellationToken ct)
        {
            await inner.WriteAsync(request, ct);
            if (request.Action == "attendance.history.read") ((ManualTimeProvider)clock).Advance(TimeSpan.FromSeconds(2));
        }
    }
    [Theory]
    [InlineData("team")]
    [InlineData("hr")]
    [InlineData("both")]
    [InlineData("admin")]
    public async Task HistoryMatchesSharedReadPolicy(string mode)
    {
        await using var f = await AttendanceHistoryFixture.CreateAsync(postgres.ConnectionString, mode);
        var day = await f.Day(CaptureFixture.Employee, 24); var foreign = await f.Day(AttendanceHistoryFixture.Stranger, 25);
        foreach (var id in new[] { day, foreign })
        {
            var result = await f.Call("GetAsync", null, id);
            await using var scope = await f.Capture.ScopeAsync(AttendanceHistoryFixture.ManagerSession);
            var db = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>(); await using var tx = await ScopeOperation.BeginAsync(db, default);
            var row = await db.Set<AttendanceDay>().SingleAsync(x => x.Id == id);
            var decision = await scope.ServiceProvider.GetRequiredService<IAttendanceAccess>().ReadAsync(
                new(row.MembershipId, row.EmployeeId, row.WorkspaceId, row.DepartmentId, row.SnapshotAtUtc), default);
            Assert.Equal(decision.CanRead ? 200 : decision.Status, result.Status);
            if (decision.CanRead)
            {
                Assert.Equal((int)decision.Basis, result.Value.GetProperty("readBasis").GetInt32());
                Assert.Equal(decision.CanReadPhoto, result.Value.GetProperty("in").TryGetProperty("photo", out _));
            }
        }
    }
    [Fact]
    public async Task CursorCannotCrossSessionsOfSamePrivilegedActor()
    {
        await using var f = await AttendanceHistoryFixture.CreateAsync(postgres.ConnectionString);
        await f.Day(CaptureFixture.Employee, 24); await f.Day(CaptureFixture.Employee, 23);
        var first = await f.List("Hr", 1); var cursor = first.Value.GetProperty("nextCursor").GetString();
        await using var db = f.Capture.Data.Database.CreateContext();
        var other = await db.Set<IdentitySession>().AsNoTracking().SingleAsync(x => x.Id == AttendanceHistoryFixture.ManagerSession);
        other.Id = Guid.NewGuid(); other.TokenHash = Enumerable.Repeat((byte)20, 32).ToArray(); db.Add(other); await db.SaveChangesAsync();
        Assert.Equal(400, (await f.List("Hr", 1, cursor, other.Id)).Status);
    }
    [Fact]
    public async Task RevocationWinningIdentityLockHidesHistory()
    {
        await using var f = await AttendanceHistoryFixture.CreateAsync(postgres.ConnectionString, "hr"); var day = await f.Day(CaptureFixture.Employee, 24);
        await using var db = f.Capture.Data.Database.CreateContext(); await using var tx = await ScopeOperation.BeginAsync(db, default);
        var hr = await db.Set<HrAssignment>().SingleAsync(); hr.ValidToUtc = f.Capture.Clock.GetUtcNow(); hr.EndedBy = hr.UserId; hr.Version++;
        await db.SaveChangesAsync();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var read = f.Call("GetAsync", null, day); await f.Capture.WaitForIdentityWaiter(budget.Token);
        await tx.CommitAsync(); Assert.Equal(404, (await read).Status);
    }
    [Fact]
    public async Task AuditFailureReturnsNoHistoryAndNoSuccessAudit()
    {
        await using var f = await AttendanceHistoryFixture.CreateAsync(postgres.ConnectionString); var day = await f.Day(CaptureFixture.Employee, 24);
        await using var db = f.Capture.Data.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_history_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
            IF NEW.event_type='attendance.history.read' THEN RAISE EXCEPTION 'test audit failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER fail_history_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION fail_history_audit();
            """);
        var result = await f.Call("GetAsync", null, day); Assert.Equal(503, result.Status);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, result.Value.ValueKind);
        Assert.False(await db.AuditEvents.AnyAsync(x => x.EventType == "attendance.history.read"));
    }
    [Fact]
    public async Task ScopeAppliedBeforePagination()
    {
        await using var f = await AttendanceHistoryFixture.CreateAsync(postgres.ConnectionString, "hr");
        await f.Day(AttendanceHistoryFixture.Stranger, 25);
        var a = await f.Day(CaptureFixture.Employee, 24); var b = await f.Day(CaptureFixture.Employee, 23);
        var first = await f.List("Hr", 1); Assert.Equal(200, first.Status);
        Assert.Equal(a, Assert.Single(first.Value.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.False(first.Value.TryGetProperty("total", out _));
        var second = await f.List("Hr", 1, first.Value.GetProperty("nextCursor").GetString()); Assert.Equal(200, second.Status);
        Assert.Equal(b, Assert.Single(second.Value.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, second.Value.GetProperty("nextCursor").ValueKind);
    }
    [Fact]
    public async Task NewSupervisorCannotReadPreviousMembershipHistory()
    {
        await using var f = await AttendanceHistoryFixture.CreateAsync(postgres.ConnectionString, "team");
        var day = await f.Day(CaptureFixture.Employee, 24);
        await using var db = f.Capture.Data.Database.CreateContext();
        var old = await db.Set<EmployeeMembership>().SingleAsync(x => x.UserId == CaptureFixture.Employee);
        var end = f.Capture.Clock.GetUtcNow().AddHours(-1);
        var line = await db.Set<ReportingLine>().SingleAsync(); line.ValidToUtc = end; line.EndedBy = AttendanceHistoryFixture.Manager; line.Version++;
        old.ValidToUtc = end; old.EndedBy = AttendanceHistoryFixture.Manager; old.Version++; await db.SaveChangesAsync();
        var next = new EmployeeMembership
        {
            Id = Guid.NewGuid(),
            UserId = old.UserId,
            WorkspaceId = old.WorkspaceId,
            DepartmentId = old.DepartmentId,
            ValidFromUtc = end,
            CreatedBy = AttendanceHistoryFixture.Manager,
            Reason = "สังกัดใหม่"
        };
        db.Add(next); db.Add(new ReportingLine
        {
            Id = Guid.NewGuid(),
            EmployeeMembershipId = next.Id,
            EmployeeUserId = next.UserId,
            SupervisorUserId = AttendanceHistoryFixture.Manager,
            ValidFromUtc = end,
            CreatedBy = AttendanceHistoryFixture.Manager,
            Reason = "หัวหน้าใหม่"
        });
        await db.SaveChangesAsync();
        Assert.Empty((await f.List("Team")).Value.GetProperty("items").EnumerateArray());
        Assert.Equal(404, (await f.Call("GetAsync", null, day)).Status);
    }
    [Fact]
    public async Task TeamJsonOmitsPhotoField()
    {
        await using var f = await AttendanceHistoryFixture.CreateAsync(postgres.ConnectionString, "team");
        var id = await f.Day(CaptureFixture.Employee, 24);
        var result = await f.Call("GetAsync", null, id); Assert.Equal(200, result.Status);
        foreach (var action in new[] { "in", "out" })
        {
            var item = result.Value.GetProperty(action); Assert.True(item.TryGetProperty("gps", out _));
            Assert.False(item.TryGetProperty("photo", out _));
            Assert.DoesNotContain("evidence", item.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(2, result.Value.GetProperty("readBasis").GetInt32());
    }
    [Fact]
    public async Task HrGrantRemovalHidesOldDays()
    {
        await using var f = await AttendanceHistoryFixture.CreateAsync(postgres.ConnectionString, "hr"); var id = await f.Day(CaptureFixture.Employee, 24);
        Assert.Equal(200, (await f.Call("GetAsync", null, id)).Status);
        await using var db = f.Capture.Data.Database.CreateContext(); var grant = await db.Set<HrAssignment>().SingleAsync();
        grant.ValidToUtc = f.Capture.Clock.GetUtcNow(); grant.EndedBy = grant.UserId; grant.Version++; await db.SaveChangesAsync();
        Assert.Empty((await f.List("Hr")).Value.GetProperty("items").EnumerateArray());
        Assert.Equal(404, (await f.Call("GetAsync", null, id)).Status);
    }
    [Fact]
    public async Task AdminWithoutGrantGetsNoBusinessData()
    {
        await using var f = await AttendanceHistoryFixture.CreateAsync(postgres.ConnectionString, "admin"); var id = await f.Day(CaptureFixture.Employee, 24);
        Assert.Equal(403, (await f.List("Hr")).Status); Assert.Equal(403, (await f.List("Team")).Status);
        Assert.Empty((await f.List("Mine")).Value.GetProperty("items").EnumerateArray());
        Assert.Equal(404, (await f.Call("GetAsync", null, id)).Status);
    }
    [Theory]
    [InlineData("tamper")]
    [InlineData("audience")]
    [InlineData("range")]
    [InlineData("size")]
    [InlineData("session")]
    public async Task ForgedCursorCannotChangeScope(string change)
    {
        await using var f = await AttendanceHistoryFixture.CreateAsync(postgres.ConnectionString);
        await f.Day(CaptureFixture.Employee, 24); await f.Day(CaptureFixture.Employee, 23);
        var cursor = (await f.List("Hr", 1)).Value.GetProperty("nextCursor").GetString()!;
        var result = await f.List(change == "audience" ? "Team" : "Hr", change == "size" ? 2 : 1,
            change == "tamper" ? "invalid-" + cursor : cursor, change == "session" ? CaptureFixture.Session : null,
            change == "range" ? AttendanceHistoryFixture.From.AddDays(1) : null);
        Assert.Equal(change == "session" ? 403 : 400, result.Status);
    }
    [Theory]
    [InlineData(0, 26, 400)]
    [InlineData(101, 26, 400)]
    [InlineData(100, 31, 200)]
    [InlineData(20, 32, 400)]
    public async Task DateRangeAndPageSizeAreBounded(int size, int days, int status)
    {
        await using var f = await AttendanceHistoryFixture.CreateAsync(postgres.ConnectionString);
        Assert.Equal(status, (await f.List("Mine", size, from: AttendanceHistoryFixture.From,
            to: AttendanceHistoryFixture.From.AddDays(days - 1))).Status);
    }
    [Fact]
    public async Task CurrentReturnsOwnOpenDayEvenFromPreviousDate()
    {
        await using var f = await AttendanceHistoryFixture.CreateAsync(postgres.ConnectionString);
        var empty = await f.Call("CurrentAsync", CaptureFixture.Session); Assert.Equal(200, empty.Status);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, empty.Value.GetProperty("openDay").ValueKind);
        var day = await f.Day(CaptureFixture.Employee, 25, false);
        var result = await f.Call("CurrentAsync", CaptureFixture.Session); Assert.Equal(200, result.Status);
        Assert.Equal(day, result.Value.GetProperty("openDay").GetProperty("id").GetGuid());
        Assert.False(result.Value.GetProperty("todayCompleted").GetBoolean());
        Assert.Equal(f.Capture.Clock.GetUtcNow(), result.Value.GetProperty("serverNowUtc").GetDateTimeOffset());
    }
    [Theory]
    [InlineData("own", 1)]
    [InlineData("hr", 3)]
    [InlineData("both", 3)]
    public async Task OwnerAndHrCanReadPhotoReferenceWithExplicitBasis(string mode, int basis)
    {
        await using var f = await AttendanceHistoryFixture.CreateAsync(postgres.ConnectionString, mode == "own" ? "team" : mode);
        var day = await f.Day(CaptureFixture.Employee, 24);
        var result = await f.Call("GetAsync", mode == "own" ? CaptureFixture.Session : null, day); Assert.Equal(200, result.Status);
        Assert.Equal(basis, result.Value.GetProperty("readBasis").GetInt32());
        var photo = result.Value.GetProperty("in").GetProperty("photo"); Assert.Single(photo.EnumerateObject());
        Assert.NotEqual(Guid.Empty, photo.GetProperty("evidenceId").GetGuid());
        Assert.True(result.Value.TryGetProperty("effectiveInUtc", out _));
    }
}
