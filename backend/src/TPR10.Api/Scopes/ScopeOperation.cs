using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.Identity.Sessions;

namespace TPR10.Api.Scopes;

public sealed class ScopeOperation(Tpr10DbContext db, ScopeAccess access, RequestSession current, IAuditEventWriter audit)
{
    public async Task<IResult> RunAsync(ScopeKey key, string capability, bool requireMfa,
        Func<ScopeContext, CancellationToken, Task<IResult>> operation, CancellationToken ct)
    {
        try
        {
            IResult result;
            int? denial;
            await using (var tx = await BeginAsync(db, ct))
            {
                var decision = await access.ResolveAsync(key, capability, requireMfa, ct);
                denial = decision.Status;
                result = denial is { } denied ? Problem(denied) : await operation(decision.Context!, ct);
                // A callback must materialize its DTO before returning; execution/streaming is never invoked here.
                if (result is not IStatusCodeHttpResult { StatusCode: { } status })
                    throw new InvalidOperationException("Scope operations must return a materialized result with an explicit status.");
                if (status is >= 200 and < 300)
                {
                    var context = decision.Context!;
                    await audit.WriteAsync(new SecurityAuditRequest(context.ActorId, context.ActingRoleId, key.WorkspaceId, key.ProjectId, key.SiteId,
                        "scope.operation.completed", "scope", null, "success", new Dictionary<string, string>
                        { ["capability"] = capability, ["scope-validation"] = "authorized", ["assignment-id"] = context.AssignmentId.ToString() }), ct);
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    return result;
                }
                denial ??= status;
                await tx.RollbackAsync(ct);
            }
            // Discard even changes already saved by a callback. Denial gets a new, clean transaction.
            db.ChangeTracker.Clear();
            await using var denialTx = await BeginAsync(db, ct);
            await DenialAuditAsync(audit, current.Entity?.UserId, key, capability, ct);
            await db.SaveChangesAsync(ct);
            await denialTx.CommitAsync(ct);
            return Problem(denial.Value);
        }
        catch (Exception error) when (IsDatabaseFault(error))
        {
            db.ChangeTracker.Clear();
            return Problem(503);
        }
    }

    internal static async Task<IDbContextTransaction> BeginAsync(Tpr10DbContext db, CancellationToken ct)
    {
        var tx = await db.Database.BeginTransactionAsync(ct);
        try { await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct); return tx; }
        catch { await tx.DisposeAsync(); throw; }
    }
    internal static bool IsDatabaseFault(Exception error) => error is NpgsqlException or DbUpdateException
        or InvalidOperationException { InnerException: NpgsqlException };
    internal static Task DenialAuditAsync(IAuditEventWriter audit, Guid? actor, ScopeKey? key, string capability, CancellationToken ct) =>
        audit.WriteAsync(new SecurityAuditRequest(actor, null, key?.WorkspaceId, key?.ProjectId, key?.SiteId,
            "scope.access.denied", "scope", null, "denied", new Dictionary<string, string>
            { ["capability"] = capability, ["scope-validation"] = "requested-unverified" }), ct);
    internal static string ProblemType(int status) => status switch
    {
        400 => "urn:tpr10:scope-invalid",
        401 => "urn:tpr10:session-required",
        403 => "urn:tpr10:scope-forbidden",
        404 => "urn:tpr10:scope-unavailable",
        409 => "urn:tpr10:scope-conflict",
        _ => "urn:tpr10:scope-unavailable-service"
    };
    internal static IResult Problem(int status) => Results.Problem(statusCode: status, type: ProblemType(status), title: status switch
    {
        400 => "ข้อมูลพื้นที่หรือคำขอไม่ถูกต้อง",
        401 => "กรุณาเข้าสู่ระบบใหม่",
        403 => "ไม่มีสิทธิ์หรือจำเป็นต้องยืนยันตัวตนเพิ่มเติม",
        404 => "ไม่พบพื้นที่หรือข้อมูลที่พร้อมใช้งาน",
        409 => "ข้อมูลเปลี่ยนแปลง กรุณาโหลดใหม่",
        _ => "บริการไม่พร้อมใช้งาน กรุณาลองใหม่ภายหลัง"
    });
}
