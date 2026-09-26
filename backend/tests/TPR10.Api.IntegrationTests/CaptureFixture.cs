using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Attendance;
using TPR10.Api.Attendance.Access;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Attendance.Recording;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.Data;
using TPR10.Api.Identity;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Organization;
using TPR10.Api.Scopes;

namespace TPR10.Api.IntegrationTests;

internal sealed class CaptureFixture(AttendanceRecordingFixture data, ServiceProvider services, ManualTimeProvider clock) : IAsyncDisposable
{
    internal AttendanceRecordingFixture Data { get; } = data;
    internal ServiceProvider Services { get; } = services;
    internal ManualTimeProvider Clock { get; } = clock;
    internal static Guid Employee => AttendanceRecordingFixture.Employee;
    internal static readonly Guid Session = Guid.Parse("00000000-0000-0000-0000-000000000071");
    internal static readonly ScopeKey Site = new(Guid.Parse("00000000-0000-0000-0000-000000000001"),
        Guid.Parse("00000000-0000-0000-0000-000000000021"), Guid.Parse("00000000-0000-0000-0000-000000000051"));
    internal EmploymentSnapshot Subject => new(Guid.Parse("00000000-0000-0000-0000-000000000041"), Employee,
        Site.WorkspaceId, Guid.Parse("00000000-0000-0000-0000-000000000011"), Clock.GetUtcNow());

    internal static async Task<CaptureFixture> CreateAsync(string connection)
    {
        var data = await AttendanceRecordingFixture.CreateAsync(connection);
        try
        {
            await using var db = data.Database.CreateContext();
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE users SET security_version=1;
                INSERT INTO roles(id,name,role_class) VALUES ('00000000-0000-0000-0000-000000000081','Capture staff','staff');
                INSERT INTO role_permissions(role_id,permission_id) VALUES ('00000000-0000-0000-0000-000000000081','20000000-0000-0000-0000-000000000013');
                INSERT INTO user_scope_assignments(id,user_id,workspace_id,project_id,site_id,role_id,created_by,reason)
                VALUES ('00000000-0000-0000-0000-000000000091','00000000-0000-0000-0000-000000000031','00000000-0000-0000-0000-000000000001',
                    '00000000-0000-0000-0000-000000000021','00000000-0000-0000-0000-000000000051','00000000-0000-0000-0000-000000000081','00000000-0000-0000-0000-000000000032','ข้อมูลทดสอบ');
                """);
            var clock = new ManualTimeProvider();
            clock.Advance(AttendanceRecordingFixture.Start - clock.GetUtcNow());
            var services = new ServiceCollection();
            services.AddDbContext<Tpr10DbContext>(o => o.UseNpgsql(data.Database.ConnectionString));
            services.AddSingleton<TimeProvider>(clock);
            services.AddScoped<RequestSession>();
            services.AddScoped<ScopeAccess>();
            services.AddScoped<IEffectiveRolePolicy, EffectiveRolePolicy>();
            services.AddSingleton<ICorrelationContext>(new CorrelationContext());
            services.AddScoped<IAuditEventWriter, AuditEventWriter>();
            services.AddAttendance();
            return new(data, services.BuildServiceProvider(), clock);
        }
        catch { await data.DisposeAsync(); throw; }
    }

    internal async Task<AsyncServiceScope> ScopeAsync(Guid? sessionId = null)
    {
        var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>();
        scope.ServiceProvider.GetRequiredService<RequestSession>().Entity = await db.Set<IdentitySession>().AsNoTracking()
            .SingleAsync(x => x.Id == (sessionId ?? Session));
        return scope;
    }

    internal static Type ServiceType(string name)
    {
        var type = typeof(AttendanceOperation).Assembly.GetType("TPR10.Api.Attendance.Recording." + name);
        Assert.NotNull(type); // RED must fail because the production service does not exist yet.
        return type;
    }
    internal static Task<T> Call<T>(IServiceProvider services, string type, string method, params object?[] args) =>
        (Task<T>)ServiceType(type).GetMethod(method)!.Invoke(services.GetRequiredService(ServiceType(type)), args)!;

    internal async Task<RecordingResult<CaptureChallengeView>> Issue(Guid key, EvidenceAction action = EvidenceAction.CheckIn, ScopeKey? site = null)
    {
        await using var scope = await ScopeAsync();
        return await Call<RecordingResult<CaptureChallengeView>>(scope.ServiceProvider, "CaptureChallengeService", "IssueAsync",
            new CaptureChallengeRequest(action, site ?? Site, key), CancellationToken.None);
    }
    internal async Task<CaptureMetadata> Metadata(EvidenceAction action = EvidenceAction.CheckIn)
    {
        var key = Guid.NewGuid(); var issued = await Issue(key, action);
        Assert.Equal(201, issued.Status);
        return new(issued.Value!.Id, key, Site, Clock.GetUtcNow(), new(13, 100, 10, Clock.GetUtcNow()), false,
            action == EvidenceAction.CheckOut ? 1 : null);
    }
    internal async Task<RecordingResult<AttendanceOperation>> Claim(CaptureMetadata metadata, string hash,
        EvidenceAction action = EvidenceAction.CheckIn, Guid? day = null)
    {
        await using var scope = await ScopeAsync();
        var db = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>();
        await using var tx = await ScopeOperation.BeginAsync(db, default);
        var result = await Call<RecordingResult<AttendanceOperation>>(scope.ServiceProvider, "CaptureOperationStore", "ClaimAsync",
            action, metadata, hash, Subject, day ?? Guid.NewGuid(), CancellationToken.None);
        await db.SaveChangesAsync(); await tx.CommitAsync();
        return result;
    }
    internal async Task<RecordingResult<CaptureOperationView>> Get(Guid key, EvidenceAction action = EvidenceAction.CheckIn, Guid? session = null)
    {
        await using var scope = await ScopeAsync(session);
        return await Call<RecordingResult<CaptureOperationView>>(scope.ServiceProvider, "CaptureOperationStore", "GetAsync",
            key, action, CancellationToken.None);
    }
    internal async Task<RecordingResult<AttendanceOperation>> ValidateCommit(Guid id, long fence)
    {
        await using var scope = await ScopeAsync();
        var db = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>();
        await using var tx = await ScopeOperation.BeginAsync(db, default);
        return await Call<RecordingResult<AttendanceOperation>>(scope.ServiceProvider, "CaptureOperationStore", "ValidateForCommitAsync",
            id, fence, CancellationToken.None);
    }

    internal async Task WaitForIdentityWaiter(CancellationToken ct)
    {
        await using var observer = Data.Database.CreateContext();
        while (!await observer.Database.SqlQueryRaw<bool>("""
            SELECT EXISTS(SELECT 1 FROM pg_locks WHERE locktype='advisory' AND classid=0 AND objid=7241002
              AND NOT granted AND database=(SELECT oid FROM pg_database WHERE datname=current_database())) AS "Value"
            """).SingleAsync(ct)) await Task.Delay(10, ct);
    }
    public async ValueTask DisposeAsync() { await Services.DisposeAsync(); await Data.DisposeAsync(); }
}
