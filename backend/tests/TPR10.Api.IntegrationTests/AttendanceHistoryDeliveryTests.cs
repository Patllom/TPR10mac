using System.Runtime.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Attendance.History;
using TPR10.Api.Data;
using TPR10.Api.Scopes;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
[UnsupportedOSPlatform("windows")]
public sealed class AttendanceHistoryDeliveryTests(PostgresFixture postgres)
{
    [Fact]
    public async Task RevocationWinningDeliveryLockPreventsHistoryBytes()
    {
        await using var f = await AttendanceHistoryFixture.CreateAsync(postgres.ConnectionString, "hr");
        var day = await f.Day(CaptureFixture.Employee, 24);
        await using var factory = new ApiFactory(f.Capture.Data.Database.ConnectionString, clock: f.Capture.Clock);
        using var scope = factory.Services.CreateScope();
        await EvidenceRevocationTests.SetSession(scope.ServiceProvider, AttendanceHistoryFixture.Manager);
        var result = new AttendanceJsonResult((s, ct) => s.DayAsync(day, ct));
        var http = EvidenceRevocationTests.Context(scope.ServiceProvider);
        await using var db = f.Capture.Data.Database.CreateContext(); await using var tx = await ScopeOperation.BeginAsync(db, default);
        var hr = await db.Set<HrAssignment>().SingleAsync(); hr.ValidToUtc = f.Capture.Clock.GetUtcNow(); hr.EndedBy = hr.UserId; hr.Version++;
        await db.SaveChangesAsync();
        var pending = result.ExecuteAsync(http); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await f.Capture.WaitForIdentityWaiter(timeout.Token); await tx.CommitAsync(); await pending;
        Assert.Equal(404, http.Response.StatusCode);
        http.Response.Body.Position = 0; var json = await new StreamReader(http.Response.Body).ReadToEndAsync();
        Assert.DoesNotContain("latitude", json); Assert.DoesNotContain("evidenceId", json);
        Assert.True(await Available(f));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliveryHoldsRevocationLockUntilSuccessOrAbort(bool stalled)
    {
        await using var f = await AttendanceHistoryFixture.CreateAsync(postgres.ConnectionString, "hr"); var day = await f.Day(CaptureFixture.Employee, 24);
        await using var factory = new ApiFactory(f.Capture.Data.Database.ConnectionString, clock: f.Capture.Clock);
        using var scope = factory.Services.CreateScope(); await EvidenceRevocationTests.SetSession(scope.ServiceProvider, AttendanceHistoryFixture.Manager);
        var http = EvidenceRevocationTests.Context(scope.ServiceProvider); using var lifetime = new Lifetime(); http.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        using var body = new HeldStream(); http.Response.Body = body;
        var pending = new AttendanceJsonResult((s, ct) => s.DayAsync(day, ct)).ExecuteAsync(http);
        await body.Reached.Task.WaitAsync(TimeSpan.FromSeconds(4)); Assert.False(await Available(f));
        if (!stalled) body.Resume.TrySetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(7));
        Assert.Equal(stalled, lifetime.Aborted); Assert.True(await Available(f)); body.Resume.TrySetResult();
    }

    [Fact]
    public async Task StalledErrorWriterAbortsAndReleasesDeliveryLock()
    {
        await using var f = await AttendanceHistoryFixture.CreateAsync(postgres.ConnectionString, "hr"); var day = await f.Day(CaptureFixture.Employee, 24);
        await using var factory = new ApiFactory(f.Capture.Data.Database.ConnectionString, clock: f.Capture.Clock);
        using var scope = factory.Services.CreateScope(); await EvidenceRevocationTests.SetSession(scope.ServiceProvider, AttendanceHistoryFixture.Manager);
        await using var db = f.Capture.Data.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_history_delivery() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
            IF NEW.event_type='attendance.history.read' THEN RAISE EXCEPTION 'test audit failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER fail_history_delivery BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION fail_history_delivery();
            """);
        var http = EvidenceRevocationTests.Context(scope.ServiceProvider); using var lifetime = new Lifetime(); http.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        using var body = new HeldStream(); http.Response.Body = body;
        var pending = new AttendanceJsonResult((s, ct) => s.DayAsync(day, ct)).ExecuteAsync(http);
        await body.Reached.Task.WaitAsync(TimeSpan.FromSeconds(4));
        await pending.WaitAsync(TimeSpan.FromSeconds(7)); Assert.True(lifetime.Aborted); Assert.True(await Available(f)); body.Resume.TrySetResult();
    }
    private static async Task<bool> Available(AttendanceHistoryFixture f)
    {
        await using var db = f.Capture.Data.Database.CreateContext(); await using var tx = await db.Database.BeginTransactionAsync();
        return await db.Database.SqlQueryRaw<bool>("SELECT pg_try_advisory_xact_lock(7241002) AS \"Value\"").SingleAsync();
    }
    private sealed class Lifetime : IHttpRequestLifetimeFeature, IDisposable
    {
        private readonly CancellationTokenSource source = new();
        public CancellationToken RequestAborted { get => source.Token; set => throw new NotSupportedException(); }
        public bool Aborted { get; private set; }
        public void Abort() { Aborted = true; source.Cancel(); }
        public void Dispose() => source.Dispose();
    }
    private sealed class HeldStream : MemoryStream
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        { Reached.TrySetResult(); await Resume.Task; await base.WriteAsync(buffer, ct); }
    }
}
