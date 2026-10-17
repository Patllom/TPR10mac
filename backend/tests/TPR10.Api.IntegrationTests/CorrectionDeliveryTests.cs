using System.Runtime.Versioning;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Attendance.Corrections;
using TPR10.Api.Attendance.History;
using TPR10.Api.Attendance.Recording;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.Data;
using TPR10.Api.Scopes;
using static TPR10.Api.IntegrationTests.CorrectionApprovalTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
[UnsupportedOSPlatform("windows")]
public sealed class CorrectionDeliveryTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revocation_before_query_or_after_read_prevents_private_bytes(bool afterRead)
    {
        await using var f = await new CorrectionApprovalTests(postgres).Fixture(); var v = await Pending(f);
        await using var db = f.Data.Database.CreateContext();
        if (afterRead) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE reporting_lines SET valid_to_utc={f.Clock.GetUtcNow().AddSeconds(1)},ended_by=employee_user_id,version=version+1");
        await using var factory = new ApiFactory(f.Data.Database.ConnectionString, clock: f.Clock);
        using var scope = factory.Services.CreateScope(); await EvidenceRevocationTests.SetSession(scope.ServiceProvider, Head);
        var http = EvidenceRevocationTests.Context(scope.ServiceProvider);
        var result = new AttendanceJsonResult(async (s, ct) =>
        {
            var read = await Delivery(s, v.Id, ct);
            if (afterRead) { Assert.NotNull(read.Value); f.Clock.Advance(TimeSpan.FromSeconds(2)); }
            return read;
        });
        if (afterRead) await result.ExecuteAsync(http);
        else
        {
            await using var tx = await ScopeOperation.BeginAsync(db, default);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE reporting_lines SET valid_to_utc={f.Clock.GetUtcNow()},ended_by=employee_user_id,version=version+1");
            var pending = result.ExecuteAsync(http); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await f.WaitForIdentityWaiter(timeout.Token); await tx.CommitAsync(); await pending;
        }
        Assert.Equal(404, http.Response.StatusCode); http.Response.Body.Position = 0;
        var body = await new StreamReader(http.Response.Body).ReadToEndAsync();
        Assert.DoesNotContain("proposal", body); Assert.DoesNotContain(v.Id.ToString(), body);
        Assert.True(await Available(f));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delivery_holds_lock_until_success_or_abort(bool stalled)
    {
        await using var f = await new CorrectionApprovalTests(postgres).Fixture(); var v = await Pending(f);
        await using var factory = new ApiFactory(f.Data.Database.ConnectionString, clock: f.Clock);
        using var scope = factory.Services.CreateScope(); await EvidenceRevocationTests.SetSession(scope.ServiceProvider, Head);
        var http = EvidenceRevocationTests.Context(scope.ServiceProvider); using var lifetime = new Lifetime(); http.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        using var body = new HeldStream(); http.Response.Body = body;
        var pending = new AttendanceJsonResult((s, ct) => Delivery(s, v.Id, ct)).ExecuteAsync(http);
        await body.Reached.Task.WaitAsync(TimeSpan.FromSeconds(4)); Assert.False(await Available(f));
        if (!stalled) body.Resume.TrySetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(7));
        Assert.Equal(stalled, lifetime.Aborted); Assert.True(await Available(f)); body.Resume.TrySetResult();
        if (!stalled) { Assert.Equal(200, http.Response.StatusCode); Assert.Equal("no-store", http.Response.Headers.CacheControl); }
    }

    [Fact]
    public async Task Expiry_during_audit_discards_projection_and_audit()
    {
        await using var f = await new CorrectionApprovalTests(postgres).Fixture(); var v = await Pending(f);
        await using var db = f.Data.Database.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE reporting_lines SET valid_to_utc={f.Clock.GetUtcNow().AddSeconds(1)},ended_by=employee_user_id,version=version+1");
        await using var scope = await f.ScopeAsync(HeadSession); var services = scope.ServiceProvider;
        var audit = new AdvancingAudit(services.GetRequiredService<Tpr10DbContext>(), services.GetRequiredService<ICorrelationContext>(), f.Clock);
        var read = await ActivatorUtilities.CreateInstance<CorrectionQueries>(services, audit).GetAsync(v.Id, default);
        Assert.Equal(404, read.Status); Assert.Null(read.Value);
        Assert.Equal(0, await db.AuditEvents.CountAsync(x => x.EventType == "attendance.correction.read"));
    }

    [Fact]
    public async Task Audit_failure_returns_no_projection()
    {
        await using var f = await new CorrectionApprovalTests(postgres).Fixture(); var v = await Pending(f);
        await using var db = f.Data.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION fail_correction_read() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.event_type='attendance.correction.read' THEN RAISE EXCEPTION 'synthetic'; END IF; RETURN NEW; END $$; CREATE TRIGGER fail_correction_read BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION fail_correction_read();");
        var read = await CorrectionReadTests.Read(f, "GetAsync", HeadSession, v.Id);
        Assert.Equal(503, read.Status); Assert.Equal(System.Text.Json.JsonValueKind.Null, read.Value.ValueKind);
    }

    [Fact]
    public async Task Queue_rechecks_count_and_items_after_grant_expires()
    {
        await using var f = await new CorrectionApprovalTests(postgres).Fixture(); await Pending(f);
        await using var db = f.Data.Database.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE reporting_lines SET valid_to_utc={f.Clock.GetUtcNow().AddSeconds(1)},ended_by=employee_user_id,version=version+1");
        await using var factory = new ApiFactory(f.Data.Database.ConnectionString, clock: f.Clock);
        using var scope = factory.Services.CreateScope(); await EvidenceRevocationTests.SetSession(scope.ServiceProvider, Head);
        var http = EvidenceRevocationTests.Context(scope.ServiceProvider);
        await new AttendanceJsonResult(async (s, ct) =>
        {
            var result = await s.CorrectionsAsync("supervisor", 1, 20, ct);
            Assert.Equal(1, Assert.IsType<CorrectionPage>(result.Value).Total); f.Clock.Advance(TimeSpan.FromSeconds(2)); return result;
        }).ExecuteAsync(http);
        Assert.Equal(409, http.Response.StatusCode); http.Response.Body.Position = 0;
        Assert.DoesNotContain("proposal", await new StreamReader(http.Response.Body).ReadToEndAsync());
    }

    private static Task<RecordingResult<object>> Delivery(AttendanceDeliveryServices service, Guid id, CancellationToken ct)
    {
        var method = typeof(AttendanceDeliveryServices).GetMethod("CorrectionAsync"); Assert.NotNull(method);
        return (Task<RecordingResult<object>>)method.Invoke(service, [id, ct])!;
    }
    private static async Task<bool> Available(CaptureFixture f)
    {
        await using var db = f.Data.Database.CreateContext(); await using var tx = await db.Database.BeginTransactionAsync();
        return await db.Database.SqlQueryRaw<bool>("SELECT pg_try_advisory_xact_lock(7241002) AS \"Value\"").SingleAsync();
    }
    private sealed class AdvancingAudit(Tpr10DbContext db, ICorrelationContext correlation, ManualTimeProvider clock) : IAuditEventWriter
    {
        private readonly AuditEventWriter inner = new(db, correlation, clock);
        public Task WriteAsync(string eventType, Guid targetId, IReadOnlyDictionary<string, string> metadata, CancellationToken ct, Guid? actorId = null) => inner.WriteAsync(eventType, targetId, metadata, ct, actorId);
        public async Task WriteAsync(SecurityAuditRequest request, CancellationToken ct)
        { await inner.WriteAsync(request, ct); if (request.Action == "attendance.correction.read") clock.Advance(TimeSpan.FromSeconds(2)); }
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
