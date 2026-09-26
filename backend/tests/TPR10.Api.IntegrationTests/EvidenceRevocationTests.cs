using System.Runtime.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Attendance.Storage;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Data;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Scopes;

namespace TPR10.Api.IntegrationTests;

[Collection("database"), UnsupportedOSPlatform("windows")]
public sealed class EvidenceRevocationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Audit_failure_with_stalled_problem_writer_is_bounded_and_releases_lock()
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        using var scope = f.D.Factory.Services.CreateScope();
        await SetSession(scope.ServiceProvider, f.Subject.EmployeeId);
        var result = await scope.ServiceProvider.GetRequiredService<EvidenceReader>().ReadAsync(f.Pin.EvidenceId, "full", false, default);
        await using var db = f.D.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_read_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
            IF NEW.event_type='attendance.evidence.read' THEN RAISE EXCEPTION 'test audit failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER fail_read_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION fail_read_audit();
            """);
        var http = Context(scope.ServiceProvider);
        using var lifetime = new Lifetime(); http.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        using var stream = new HeldStream(false); http.Response.Body = stream;
        var delivery = result.ExecuteAsync(http);
        await stream.Reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var completed = await Task.WhenAny(delivery, Task.Delay(TimeSpan.FromSeconds(7))) == delivery;
        var aborted = lifetime.Aborted;
        var unlocked = await CanTakeExclusive(f);
        stream.Resume.TrySetResult();
        await delivery.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(completed, "Error delivery exceeded its five-second deadline");
        Assert.True(aborted);
        Assert.True(unlocked);
    }
    [Theory]
    [InlineData("before-file", "revoke")]
    [InlineData("after-file", "revoke")]
    [InlineData("before-lock", "revoke")]
    [InlineData("before-file", "disable")]
    [InlineData("after-file", "disable")]
    [InlineData("before-file", "hr-end")]
    [InlineData("after-file", "hr-end")]
    [InlineData("before-lock", "hr-end")]
    public async Task Revocation_winning_delivery_lock_prevents_all_image_bytes(string barrier, string mutation)
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        using var scope = f.D.Factory.Services.CreateScope();
        if (mutation == "hr-end")
        {
            var role = await AttendanceIdentityTests.RoleAsync(f.D, "approval", 15);
            await using var setup = f.D.Database.CreateContext();
            setup.Add(new UserRole { UserId = f.Admin, RoleId = role, CreatedAtUtc = f.D.Clock.GetUtcNow() });
            setup.Add(new HrAssignment
            {
                Id = Guid.NewGuid(),
                UserId = f.Admin,
                WorkspaceId = f.Subject.WorkspaceId,
                DepartmentId = f.Subject.DepartmentId,
                ValidFromUtc = f.D.Clock.GetUtcNow().AddDays(-1),
                CreatedBy = f.Admin,
                CreatedAtUtc = f.D.Clock.GetUtcNow(),
                Reason = "ทดสอบ HR"
            });
            await setup.SaveChangesAsync();
        }
        await SetSession(scope.ServiceProvider, mutation == "hr-end" ? f.Admin : f.Subject.EmployeeId);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new StorageRuntime(scope.ServiceProvider.GetRequiredService<IOptionsMonitor<StorageOptions>>(), f.D.Clock,
            (id, definition) => new PausedAdapter(new FolderStorageAdapter(id, definition, f.D.Clock), reached, resume));
        var reader = barrier == "before-file" ? ActivatorUtilities.CreateInstance<EvidenceReader>(scope.ServiceProvider, runtime)
            : scope.ServiceProvider.GetRequiredService<EvidenceReader>();
        var read = reader.ReadAsync(f.Pin.EvidenceId, "thumbnail", false, default);
        if (barrier == "before-file")
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Revoke(f, mutation); // Proves there is no identity lock during filesystem work.
            resume.SetResult();
        }
        var result = await read;
        var http = Context(scope.ServiceProvider);
        if (barrier == "before-lock")
        {
            await using var db = f.D.Database.CreateContext();
            await using var tx = await ScopeOperation.BeginAsync(db, default);
            await Mutate(db, f, mutation);
            var delivery = result.ExecuteAsync(http);
            await WaitForSharedWaiter(f);
            await tx.CommitAsync();
            await delivery;
        }
        else
        {
            if (barrier == "after-file") await Revoke(f, mutation);
            await result.ExecuteAsync(http);
        }
        Assert.Equal(mutation == "hr-end" ? 404 : 401, http.Response.StatusCode);
        Assert.DoesNotContain("image/", http.Response.ContentType ?? "");
        var bytes = ((MemoryStream)http.Response.Body).ToArray();
        Assert.False(bytes.Length >= 2 && bytes[0] == 255 && bytes[1] == 216);
        Assert.True(await CanTakeExclusive(f));
    }

    [Theory]
    [InlineData("success")]
    [InlineData("cancel")]
    [InlineData("slow")]
    [InlineData("network")]
    public async Task Delivery_serializes_revocation_is_bounded_and_never_leaks_lock(string outcome)
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        using var scope = f.D.Factory.Services.CreateScope();
        await SetSession(scope.ServiceProvider, f.Subject.EmployeeId);
        var result = await scope.ServiceProvider.GetRequiredService<EvidenceReader>().ReadAsync(f.Pin.EvidenceId, "full", false, default);
        var http = Context(scope.ServiceProvider);
        using var lifetime = new Lifetime(); http.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        using var stream = new HeldStream(outcome == "network"); http.Response.Body = stream;
        var delivery = result.ExecuteAsync(http);
        await stream.Reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(await CanTakeExclusive(f));
        if (outcome is "success" or "network") stream.Resume.SetResult();
        if (outcome == "cancel") lifetime.Cancel();
        await delivery.WaitAsync(TimeSpan.FromSeconds(7));
        Assert.Equal(outcome != "success", lifetime.Aborted);
        Assert.True(await CanTakeExclusive(f));
        stream.Resume.TrySetResult();
    }

    [Fact]
    public async Task Audit_failure_before_headers_returns_503_without_image_and_releases_lock()
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        await using var db = f.D.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_evidence_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
            IF NEW.event_type='attendance.evidence.read' THEN RAISE EXCEPTION 'test audit unavailable'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER fail_evidence_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION fail_evidence_audit();
            """);
        using var response = await f.Owner.GetAsync(f.Url);
        Assert.Equal(503, (int)response.StatusCode);
        Assert.DoesNotContain("image/", response.Content.Headers.ContentType?.ToString() ?? "");
        Assert.True(await CanTakeExclusive(f));
    }

    internal static async Task SetSession(IServiceProvider services, Guid actor)
    {
        var db = services.GetRequiredService<Tpr10DbContext>();
        services.GetRequiredService<RequestSession>().Entity = await db.Set<IdentitySession>().AsNoTracking()
            .Where(x => x.UserId == actor && x.RevokedAtUtc == null).OrderByDescending(x => x.CreatedAtUtc).FirstAsync();
    }
    internal static DefaultHttpContext Context(IServiceProvider services)
    {
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Method = "GET"; http.Response.Body = new MemoryStream(); return http;
    }
    private static async Task Revoke(EvidenceFixture f, string mutation)
    {
        await using var db = f.D.Database.CreateContext();
        await using var tx = await ScopeOperation.BeginAsync(db, default);
        await Mutate(db, f, mutation); await tx.CommitAsync();
    }
    private static async Task Mutate(Tpr10DbContext db, EvidenceFixture f, string mutation)
    {
        if (mutation == "hr-end")
        {
            var grant = await db.Set<HrAssignment>().SingleAsync(x => x.UserId == f.Admin);
            grant.ValidToUtc = f.D.Clock.GetUtcNow(); grant.EndedBy = f.Admin; grant.Version++;
            await db.SaveChangesAsync();
        }
        else if (mutation == "disable") await db.Set<IdentityUser>().Where(x => x.Id == f.Subject.EmployeeId).ExecuteUpdateAsync(x => x.SetProperty(u => u.IsActive, false));
        else await db.Set<IdentitySession>().Where(x => x.UserId == f.Subject.EmployeeId).ExecuteUpdateAsync(x => x.SetProperty(s => s.RevokedAtUtc, f.D.Clock.GetUtcNow()));
    }
    private static async Task<bool> CanTakeExclusive(EvidenceFixture f)
    {
        await using var db = f.D.Database.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        return await db.Database.SqlQueryRaw<bool>("SELECT pg_try_advisory_xact_lock(7241002) AS \"Value\"").SingleAsync();
    }
    private static async Task WaitForSharedWaiter(EvidenceFixture f)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using var db = f.D.Database.CreateContext();
        while (!await db.Database.SqlQueryRaw<bool>("SELECT EXISTS(SELECT 1 FROM pg_locks WHERE locktype='advisory' AND objid=7241002 AND mode='ShareLock' AND NOT granted) AS \"Value\"").SingleAsync(timeout.Token))
            await Task.Delay(10, timeout.Token);
    }
    private sealed class PausedAdapter(IStorageAdapter inner, TaskCompletionSource reached, TaskCompletionSource resume) : IStorageAdapter
    {
        public Task<StoredCopy> WriteImmutableAsync(string key, ReadOnlyMemory<byte> bytes, CancellationToken ct) => inner.WriteImmutableAsync(key, bytes, ct);
        public Task<StorageHealthView> ProbeAsync(CancellationToken ct) => inner.ProbeAsync(ct);
        public async Task<byte[]> ReadVerifiedAsync(string key, string sha256, long length, CancellationToken ct)
        { reached.SetResult(); await resume.Task.WaitAsync(ct); return await inner.ReadVerifiedAsync(key, sha256, length, ct); }
    }
    private sealed class Lifetime : IHttpRequestLifetimeFeature, IDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        public CancellationToken RequestAborted { get => cancellation.Token; set => throw new NotSupportedException(); }
        public bool Aborted { get; private set; }
        public void Abort() { Aborted = true; cancellation.Cancel(); }
        public void Cancel() => cancellation.Cancel();
        public void Dispose() => cancellation.Dispose();
    }
    private sealed class HeldStream(bool fail) : MemoryStream
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer[..1], cancellationToken);
            Reached.TrySetResult(); await Resume.Task; // Deliberately ignores cancellation, as a stalled transport can.
            if (fail) throw new IOException("simulated transport failure");
            await base.WriteAsync(buffer[1..], cancellationToken);
        }
    }
}
