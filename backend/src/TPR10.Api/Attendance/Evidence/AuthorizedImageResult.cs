using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TPR10.Api.Attendance.Access;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.Data;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Organization;
using TPR10.Api.Scopes;

namespace TPR10.Api.Attendance.Evidence;

// Lifetime of the shared lock includes audit commit AND delivery, never filesystem I/O.
// Pooling=false is intentional: even a failed unlock cannot return a locked connection to a pool.
public sealed class AuthorizedImageResult(DbContextOptions<Tpr10DbContext> options, string connectionString,
    RequestSession current, ICorrelationContext correlation, TimeProvider clock, EvidenceLocation copy, byte[] bytes, bool download) : IResult
{
    public async Task ExecuteAsync(HttpContext http)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var ct = deadline.Token;
        var connectionOptions = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, Timeout = 5, CommandTimeout = 5 };
        await using var connection = new NpgsqlConnection(connectionOptions.ConnectionString);
        var locked = false;
        var delivering = false;
        try
        {
            if (bytes.Length != copy.Length || bytes.Length > ImageLimits.MaxBytes
                || Convert.ToHexStringLower(SHA256.HashData(bytes)) != copy.Sha256) throw new IOException("evidence-integrity");
            await connection.OpenAsync(ct);
            await using (var command = new NpgsqlCommand("SELECT pg_advisory_lock_shared(7241002)", connection))
                await command.ExecuteNonQueryAsync(ct);
            locked = true;
            await using var db = new Tpr10DbContext(new DbContextOptionsBuilder<Tpr10DbContext>(options).UseNpgsql(connection).Options);
            var session = new ScopeAccess(db, current, new EffectiveRolePolicy(db), clock);
            var access = new AttendanceAccess(db, session, clock);
            // Do not invoke ScopeOperation.BeginAsync: exclusive lock upgrade would deadlock readers.
            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                var authorization = await EvidenceReader.AuthorizeAsync(db, session, access, copy.EvidenceId, ct);
                var status = authorization.Status;
                if (status is null && !await db.Set<EvidenceLocation>().AnyAsync(x => x.Id == copy.Id && x.EvidenceId == copy.EvidenceId
                    && x.Sha256 == copy.Sha256 && x.Length == copy.Length && x.VerifiedAtUtc != null
                    && (x.State == CopyState.Active || x.State == CopyState.Fallback), ct)) status = 503;
                var audit = new AuditEventWriter(db, correlation, clock);
                var decision = status is null ? "success" : "denied";
                await audit.WriteAsync(new SecurityAuditRequest(current.Entity?.UserId, null, authorization.WorkspaceId, null, null,
                    "attendance.evidence.read", "attendance-evidence", copy.EvidenceId, decision,
                    new Dictionary<string, string> { ["source"] = copy.Variant, ["destination-type"] = download ? "download" : "inline", ["scope"] = authorization.Basis.ToString() }), ct);
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
                if (status is { } denied)
                {
                    if (HttpMethods.IsHead(http.Request.Method)) http.Response.StatusCode = denied;
                    else await ScopeOperation.Problem(denied).ExecuteAsync(http).WaitAsync(ct);
                    return;
                }
            }
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            http.Response.Headers.Remove("ETag"); http.Response.Headers.Remove("Last-Modified");
            if (http.Request.Headers.ContainsKey("Range")) { http.Response.StatusCode = 416; return; }
            http.Response.StatusCode = 200;
            http.Response.ContentType = "image/jpeg";
            http.Response.ContentLength = bytes.Length;
            http.Response.Headers.ContentDisposition = $"{(download ? "attachment" : "inline")}; filename=\"{copy.EvidenceId:N}.jpg\"";
            ct.ThrowIfCancellationRequested();
            delivering = true;
            if (!HttpMethods.IsHead(http.Request.Method))
            {
                await http.Response.Body.WriteAsync(bytes, ct).AsTask().WaitAsync(ct);
                await http.Response.Body.FlushAsync(ct).WaitAsync(ct);
            }
            await http.Response.CompleteAsync().WaitAsync(ct);
        }
        catch (Exception error) when (ScopeOperation.IsDatabaseFault(error) || error is IOException or OperationCanceledException or TimeoutException)
        {
            // Abort before unlock; do not append a JSON error after partial delivery.
            if (delivering || http.Response.HasStarted || ct.IsCancellationRequested) http.Abort();
            else
            {
                http.Response.Clear();
                try
                {
                    if (HttpMethods.IsHead(http.Request.Method)) http.Response.StatusCode = 503;
                    else await ScopeOperation.Problem(503).ExecuteAsync(http).WaitAsync(ct);
                }
                catch (Exception deliveryError) when (deliveryError is IOException or OperationCanceledException or TimeoutException)
                { http.Abort(); }
            }
        }
        finally
        {
            if (locked && connection.State == System.Data.ConnectionState.Open)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock_shared(7241002)", connection);
                    await unlock.ExecuteNonQueryAsync(cleanup.Token);
                }
                catch (Exception error) when (error is NpgsqlException or OperationCanceledException or InvalidOperationException) { /* Unpooled physical connection is disposed below. */ }
            }
        }
    }
}
