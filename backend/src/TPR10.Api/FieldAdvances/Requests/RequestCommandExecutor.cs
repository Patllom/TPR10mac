using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Payments;

namespace TPR10.Api.FieldAdvances.Requests;

// Internal transaction/receipt machinery for request commands, not a public action dispatcher.
internal sealed class RequestCommandExecutor(Tpr10DbContext db, RequestAccess access, IAuditEventWriter audit, TimeProvider clock)
{
    internal async Task<FinanceResult<RequestView>> ExecuteAsync(Func<Task<FinanceResult<RequestView>>> command, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct);
            var result = await command();
            if (result.Status is >= 200 and < 300)
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            return result;
        }
        catch (DbUpdateConcurrencyException) { return Conflict(); }
        finally
        {
            // Includes commit-time failures after SaveChanges has accepted tracked state.
            // Successful commands also release their snapshots so reuse cannot read stale rows.
            db.ChangeTracker.Clear();
        }
    }

    internal Task<AdvanceRequest?> LockAsync(Guid requestId, CancellationToken ct) =>
        FamilyParticipation.LockRequestAsync(db, requestId, ct);

    // The caller checks current command authority BEFORE reaching this receipt lookup.
    internal async Task<FinanceResult<RequestView>?> ReplayAsync(Guid actor, FinanceScope scope, string action,
        RequestCommand meta, string hash, CancellationToken ct)
    {
        var receipt = await db.Set<FinanceCommandReceipt>().AsNoTracking().SingleOrDefaultAsync(x => x.CommandId == meta.CommandId, ct);
        if (receipt is null) return null;
        if (receipt.ActorId != actor || receipt.WorkspaceId != scope.WorkspaceId || receipt.Action != action || receipt.PayloadSha256 != hash)
            return Conflict();
        var result = JsonSerializer.Deserialize<FinanceResult<RequestView>>(receipt.ResultJson)
            ?? throw new InvalidOperationException("Invalid retained request receipt.");
        if (result.Value is null || !(await access.CheckAsync(actor, result.Value.Id, "read", ct)).Allowed) return Denied();
        return result;
    }

    internal async Task<FinanceResult<RequestView>> FinishAsync(Guid actor, Guid person, AdvanceRequest request,
        AdvanceRequestRevision revision, string action, string kind, RequestCommand meta, string hash,
        RequestContent content, Estimate estimate, int status, Func<Task<bool>> recheck, CancellationToken ct)
    {
        var result = new FinanceResult<RequestView>(status, "request-" + action,
            new(request.Id, revision.Id, request.Version, request.State, new(request.WorkspaceId, request.ProjectId),
                request.OwnerPersonId, request.DelegatePersonId, content, estimate, request.HoldCode));
        db.Add(new FinanceCommandReceipt
        {
            CommandId = meta.CommandId,
            WorkspaceId = request.WorkspaceId,
            ActorId = actor,
            Action = action,
            PayloadSha256 = hash,
            Status = status,
            ResultJson = JsonSerializer.Serialize(result),
            CreatedAtUtc = clock.GetUtcNow()
        });
        db.Add(new AdvanceRequestEvent
        {
            Id = Guid.NewGuid(),
            WorkspaceId = request.WorkspaceId,
            ProjectId = request.ProjectId,
            RequestId = request.Id,
            RevisionId = revision.Id,
            CommandId = meta.CommandId,
            ActorId = actor,
            ActorPersonId = person,
            Kind = kind,
            Reason = meta.Reason,
            DataJson = JsonSerializer.Serialize(new { revision.ContentHash, revision.Total }),
            AggregateVersion = request.Version,
            CreatedAtUtc = clock.GetUtcNow()
        });
        await audit.WriteAsync(new SecurityAuditRequest(actor, null, request.WorkspaceId, request.ProjectId, null,
            "field-advance.request." + action, "field-advance-request", request.Id, "success",
            new Dictionary<string, string> { ["scope-validation"] = "authorized" }), ct);
        // The advisory lock prevents concurrent mutations; rechecking also covers time expiry.
        if (!await recheck()) return Denied();
        return result;
    }

    internal static string Hash<T>(T input) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input)));
    internal static FinanceResult<RequestView> Denied() => new(403, "finance-access-denied", null);
    internal static FinanceResult<RequestView> Conflict() => new(409, "request-conflict", null);
    internal static FinanceResult<RequestView> Invalid() => new(400, "invalid-request", null);
}
