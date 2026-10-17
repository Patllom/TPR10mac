using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TPR10.Api.Attendance.Access;
using TPR10.Api.Attendance.Corrections;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Attendance.Recording;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.Data;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Identity.Authorization;
using TPR10.Api.Organization;
using TPR10.Api.Scopes;

namespace TPR10.Api.Attendance.History;

// Execute the read only at delivery time. A dedicated session lock survives service/audit commits.
// Exclusive (rather than shared) permits existing ScopeOperation transactions on this same connection.
// No filesystem I/O occurs here. The unpooled connection is physically closed on every exit path.
public sealed class AttendanceJsonResult(Func<AttendanceDeliveryServices, CancellationToken, Task<RecordingResult<object>>> read) : IResult
{
    public async Task ExecuteAsync(HttpContext http)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(5)); var ct = deadline.Token;
        var services = http.RequestServices;
        var options = services.GetRequiredService<DbContextOptions<Tpr10DbContext>>();
        var original = services.GetRequiredService<Tpr10DbContext>();
        var connectionOptions = new NpgsqlConnectionStringBuilder(original.Database.GetConnectionString()) { Pooling = false, Timeout = 5, CommandTimeout = 5 };
        await using var connection = new NpgsqlConnection(connectionOptions.ConnectionString);
        var delivering = false;
        try
        {
            await connection.OpenAsync(ct);
            await using (var command = new NpgsqlCommand("SELECT pg_advisory_lock(7241002)", connection)) await command.ExecuteNonQueryAsync(ct);
            await using var db = new Tpr10DbContext(new DbContextOptionsBuilder<Tpr10DbContext>(options).UseNpgsql(connection).Options);
            var delivery = new AttendanceDeliveryServices(db, services);
            var result = await read(delivery, ct);
            if (result.Value is null) { await RecordingEndpoints.Result(result, http).ExecuteAsync(http).WaitAsync(ct); return; }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (bytes.Length > 1024 * 1024) throw new IOException("history-response-too-large");
            // Serialization is bounded and rechecked before the first byte is written.
            if (await delivery.RevalidateAsync(result.Value, ct) is { } denied)
            { await RecordingEndpoints.Problem(denied).ExecuteAsync(http).WaitAsync(ct); return; }
            ct.ThrowIfCancellationRequested();
            http.Response.Headers.CacheControl = "no-store"; http.Response.Headers.XContentTypeOptions = "nosniff";
            http.Response.StatusCode = result.Status; http.Response.ContentType = "application/json; charset=utf-8"; http.Response.ContentLength = bytes.Length;
            delivering = true;
            await http.Response.Body.WriteAsync(bytes, ct).AsTask().WaitAsync(ct);
            await http.Response.Body.FlushAsync(ct).WaitAsync(ct);
            await http.Response.CompleteAsync().WaitAsync(ct);
        }
        catch (Exception error) when (ScopeOperation.IsDatabaseFault(error) || error is IOException or OperationCanceledException or TimeoutException)
        {
            if (delivering || http.Response.HasStarted || ct.IsCancellationRequested) http.Abort();
            else { http.Response.Clear(); await RecordingEndpoints.Problem(503).ExecuteAsync(http).WaitAsync(ct); }
        }
        // Dispose closes the non-pooled physical connection, releasing its session lock even on abort.
    }
}

public sealed class AttendanceDeliveryServices
{
    private readonly Tpr10DbContext db;
    private readonly ScopeAccess identity;
    private readonly AttendanceAccess access;
    private readonly AttendanceHistory history;
    private readonly CaptureOperationStore operations;
    private readonly CorrectionQueries corrections;
    private string? correctionAudience;
    private int correctionPage, correctionSize;
    private AttendanceAudience? audience;
    public AttendanceDeliveryServices(Tpr10DbContext db, IServiceProvider services)
    {
        this.db = db;
        var current = services.GetRequiredService<RequestSession>(); var clock = services.GetRequiredService<TimeProvider>();
        identity = new ScopeAccess(db, current, new EffectiveRolePolicy(db), clock); access = new AttendanceAccess(db, identity, clock);
        var audit = new AuditEventWriter(db, services.GetRequiredService<ICorrelationContext>(), clock);
        history = new AttendanceHistory(db, new AttendanceReadScope(db, identity, clock), access, identity, current,
            services.GetRequiredService<IDataProtectionProvider>(), audit, clock);
        var challenges = new CaptureChallengeService(db, identity, current, access, audit, clock);
        operations = new CaptureOperationStore(db, identity, current, challenges, clock);
        corrections = new CorrectionQueries(db, identity, access, new AttendanceRouteResolver(db, identity, clock),
            new PermissionMutationGuard(db, current, new PermissionContext(), clock), audit, clock);
    }
    public async Task<RecordingResult<object>> CurrentAsync(CancellationToken ct) => Box(await history.CurrentAsync(ct));
    public async Task<RecordingResult<object>> CorrectionAsync(Guid id, CancellationToken ct) => Box(await corrections.GetAsync(id, ct));
    public async Task<RecordingResult<object>> CorrectionsAsync(string requested, int page, int size, CancellationToken ct)
    {
        correctionAudience = requested; correctionPage = page; correctionSize = size;
        return Box(await corrections.ListAsync(requested, page, size, ct));
    }
    public async Task<RecordingResult<object>> DayAsync(Guid id, CancellationToken ct) => Box(await history.GetAsync(id, ct));
    public async Task<RecordingResult<object>> ListAsync(AttendanceAudience requested, AttendanceListQuery query, CancellationToken ct)
    { audience = requested; return Box(await history.ListAsync(requested, query, ct)); }
    public async Task<RecordingResult<object>> OperationAsync(Guid key, EvidenceAction action, CancellationToken ct) => Box(await operations.GetAsync(key, action, ct));
    public async Task<RecordingResult<object>> ReceiptAsync(Guid key, EvidenceAction action, int status, CancellationToken ct)
    {
        var result = await operations.GetAsync(key, action, ct);
        return result.Value?.Receipt is { } receipt ? new(receipt, status, null, null) : new(null, result.Status == 200 ? 409 : result.Status, result.Code, result.RetryAfterSeconds);
    }
    internal async Task<int?> RevalidateAsync(object value, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var validation = await identity.ValidateSessionAsync(audience is AttendanceAudience.Team or AttendanceAudience.Hr, ct);
        if (validation.Status is { } status) return status;
        if (value is CorrectionPage or CorrectionView)
            return await corrections.RevalidateAsync(value, correctionAudience, correctionPage, correctionSize, ct);
        AttendanceDayView[] days = value switch { AttendancePage p => p.Items, AttendanceDayView day => [day], CurrentAttendanceView { OpenDay: { } day } => [day], _ => [] };
        foreach (var day in days)
        {
            var row = await db.Set<AttendanceDay>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == day.Id, ct);
            if (row is null) return 404;
            var decision = await access.ReadAsync(new(row.MembershipId, row.EmployeeId, row.WorkspaceId, row.DepartmentId, row.SnapshotAtUtc), ct);
            if (decision.Status is { } denied) return denied;
            if (!decision.CanRead || decision.Basis != day.ReadBasis
                || day.In is { } entered && decision.CanReadPhoto != (entered.Photo is not null)
                || day.Out is { } exited && decision.CanReadPhoto != (exited.Photo is not null)) return 404;
        }
        return null;
    }
    private static RecordingResult<object> Box<T>(RecordingResult<T> result) => new(result.Value, result.Status, result.Code, result.RetryAfterSeconds);
    private static RecordingResult<object> Box<T>(CorrectionResult<T> result) => new(result.Value, result.Status, result.Problem, null);
}
