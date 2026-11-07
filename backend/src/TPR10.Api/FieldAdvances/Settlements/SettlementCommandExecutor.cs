using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Payments;

namespace TPR10.Api.FieldAdvances.Settlements;

public sealed class SettlementCommandExecutor(Tpr10DbContext db, SettlementAccess access,
    FamilyParticipation participation, IAuditEventWriter audit, TimeProvider clock)
{
    private Guid? activeCommand;
    private Func<CancellationToken, Task<bool>>? finalAuthorization;
    // Server-only participant authorization, registered inside this command's active body.
    // Replays authorize the caller without rerunning an already committed financial decision.
    internal void RegisterFinalAuthorization(Guid requestId, Guid commandId, Func<CancellationToken, Task<bool>> validate)
    {
        if (db.Database.CurrentTransaction is null || activeRequest != requestId || activeCommand != commandId || finalAuthorization is not null)
            throw new InvalidOperationException("ต้องลงทะเบียนตรวจสิทธิ์สุดท้ายภายในคำสั่งที่ทำงานอยู่เท่านั้น");
        finalAuthorization = validate;
    }
    internal void RequireCaseContext(Guid requestId, Guid commandId)
    {
        if (db.Database.CurrentTransaction is null || activeRequest != requestId || activeCommand != commandId
            || activeAction != SettlementActions.CorrectionCreate)
            throw new InvalidOperationException("ต้องเปิดเคสภายในคำสั่งแก้เงินคืน");
    }
    private Guid? activeRequest;
    private string? activeAction;
    private Guid? activeEntity;
    internal bool IsAllocationEntity(Guid entityId) => activeEntity == entityId;
    internal void RequireAllocationContext(Guid requestId, string action)
    {
        if (db.Database.CurrentTransaction is null || activeRequest != requestId || activeAction != action)
            throw new InvalidOperationException("ต้องจัดสรรเอกสารภายในคำสั่งเคลียร์ที่ล็อกทะเบียนแล้ว");
    }

    // The shared contract is for request-level/create commands. Existing entity commands use
    // the overload below: an opaque payload hash cannot securely convey an entity identifier.
    public Task<FinanceResult<SettlementCommandResult>> ExecuteAsync(Guid actor, Guid requestId,
        string action, SettlementCommand command, string payloadHash,
        Func<CancellationToken, Task<FinanceResult<SettlementCommandResult>>> mutate, CancellationToken ct)
        => ExecuteAsync(actor, requestId, action, null, command, payloadHash, mutate, ct);

    public Task<FinanceResult<SettlementCommandResult>> ExecuteAsync(Guid actor, Guid requestId,
        string action, Guid? entityId, SettlementCommand command, string payloadHash,
        Func<CancellationToken, Task<FinanceResult<SettlementCommandResult>>> mutate, CancellationToken ct)
        => ExecuteCoreAsync(actor, requestId, action, entityId, command, payloadHash, mutate, null, ct);

    internal Task<FinanceResult<SettlementCommandResult>> ExecuteRoundAsync(Guid actor, Guid requestId,
        Guid roundId, bool confirm, SettlementCommand command, string payloadHash,
        Func<Guid, CancellationToken, Task<FinanceResult<SettlementCommandResult>>> mutate, CancellationToken ct)
    {
        Guid target = Guid.Empty;
        return ExecuteCoreAsync(actor, requestId, confirm ? SettlementActions.Confirm : SettlementActions.Approve,
            null, command, payloadHash, token => mutate(target, token), async token =>
            {
                var revision = await db.Set<SettlementRevision>().AsNoTracking().Where(x => x.RequestId == requestId && x.RoundId == roundId)
                    .OrderByDescending(x => x.RevisionNumber).FirstOrDefaultAsync(token);
                if (revision is null) return ("invalid", (Guid?)null);
                var receipt = await db.Set<FinanceCommandReceipt>().AsNoTracking().SingleOrDefaultAsync(x => x.CommandId == command.CommandId, token);
                var replayId = receipt is null ? null : JsonSerializer.Deserialize<FinanceResult<SettlementCommandResult>>(receipt.ResultJson)?.Value?.EntityId;
                if (confirm)
                {
                    target = replayId ?? revision.Id;
                    if (!await db.Set<SettlementRevision>().AnyAsync(x => x.Id == target && x.RequestId == requestId && x.RoundId == roundId, token))
                        return ("invalid", (Guid?)null);
                    return (SettlementActions.Confirm, (Guid?)target);
                }
                var step = replayId is { } original
                    ? await db.Set<SettlementApprovalStep>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == original && x.RequestId == requestId
                        && db.Set<SettlementRevision>().Any(r => r.Id == x.RevisionId && r.RoundId == roundId), token)
                    : await db.Set<SettlementApprovalStep>().AsNoTracking().Where(x => x.RevisionId == revision.Id && x.State == "Pending")
                        .OrderBy(x => x.StepOrder).FirstOrDefaultAsync(token);
                if (step is null || replayId is null && revision.State != "Submitted") return ("invalid", (Guid?)null);
                if (replayId is null)
                {
                    // Refresh only an undecided assignment under the family lock. Access still
                    // requires the persisted assignee; any denial/stale command rolls this back.
                    var route = await new SettlementRouteResolver(db, new(db, clock), clock).CurrentStepAsync(step, token);
                    await db.Set<SettlementApprovalStep>().Where(x => x.Id == step.Id && x.State == "Pending" && x.ReviewedAtUtc == null)
                        .ExecuteUpdateAsync(set => set.SetProperty(x => x.AssigneePersonId, route.PersonId), token);
                }
                target = step.Id;
                return (step.LevelCode == "Finance" ? SettlementActions.Check : SettlementActions.Approve, (Guid?)target);
            }, ct);
    }

    private async Task<FinanceResult<SettlementCommandResult>> ExecuteCoreAsync(Guid actor, Guid requestId,
        string action, Guid? entityId, SettlementCommand command, string payloadHash,
        Func<CancellationToken, Task<FinanceResult<SettlementCommandResult>>> mutate,
        Func<CancellationToken, Task<(string Action, Guid? Entity)>>? resolve, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null) throw new InvalidOperationException("คำสั่งเคลียร์ต้องเป็นเจ้าของธุรกรรม");
        try
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // All proof claim/reservation/release paths share this order: proof gate ->
            // participation gate (7241002) -> family -> request -> entity/evidence.
            // A dedicated global gate avoids resolving untrusted workspace identity before
            // authorization. Duplicate comparisons remain strictly workspace-scoped.
            if (action is SettlementActions.RefundPropose or SettlementActions.RefundUpdate or SettlementActions.RefundConfirm or SettlementActions.RefundCancel or SettlementActions.RefundReview
                or SettlementActions.CaseRefundCreate or SettlementActions.CorrectionCreate or SettlementActions.CorrectionPropose or SettlementActions.CorrectionReview or SettlementActions.CorrectionCancel
                or SettlementActions.RecoveryPropose or SettlementActions.RecoveryReview or SettlementActions.ExcessPaymentCreate or SettlementActions.ExcessPaymentReview or SettlementActions.ExcessPaymentCancel or SettlementActions.ExcessDisposition or SettlementActions.CasePaymentCreate or SettlementActions.CasePaymentReview or SettlementActions.CasePaymentCancel)
                await SettlementPaymentProof.LockAsync(db, ct);
            if (action is SettlementActions.ExcessTrack or SettlementActions.ExpensePropose or SettlementActions.Document or SettlementActions.DuplicateReview or SettlementActions.Draft or SettlementActions.Confirm or SettlementActions.Approve or SettlementActions.Check)
                await DuplicateDocumentService.LockRegistryAsync(db, requestId, ct);
            var request = await FamilyParticipation.LockRequestAsync(db, requestId, ct);
            if (resolve is not null) (action, entityId) = await resolve(ct);
            var decision = await access.CheckAsync(actor, requestId, action, entityId, ct);
            if (request is null || !decision.Allowed || decision.PersonId is null || action == SettlementActions.Read) return Denied();
            if (command.CommandId == Guid.Empty || string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Length > 2000
                || payloadHash is null || payloadHash.Length != 64 || payloadHash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                return new(400, "invalid-settlement", null);
            var hash = Hash(new { actor, requestId, action, entityId, command, payloadHash });
            var receiptAction = "settlement." + action;
            var receipt = await db.Set<FinanceCommandReceipt>().AsNoTracking().SingleOrDefaultAsync(x => x.CommandId == command.CommandId, ct);
            if (receipt is not null)
            {
                if (receipt.ActorId != actor || receipt.WorkspaceId != request.WorkspaceId || receipt.Action != receiptAction
                    || receipt.PayloadSha256 != hash) return Conflict();
                var replay = JsonSerializer.Deserialize<FinanceResult<SettlementCommandResult>>(receipt.ResultJson);
                return replay?.Value?.RequestId == requestId ? replay : Conflict();
            }
            if (request.State == "Closed" && action == SettlementActions.Close) return Conflict();
            var family = await db.Set<AdvanceFamily>().SingleAsync(x => x.Id == request.FamilyId, ct);
            var entity = entityId is { } id ? await access.LoadEntityAsync(action, id, request, ct) : null;
            if (request.Version != command.RequestVersion || family.Version != command.FamilyVersion
                || (entity is null ? null : (long?)entity.Property("Version").CurrentValue) != command.EntityVersion) return Conflict();
            if (entity is not null) entity.State = EntityState.Unchanged;
            var wasClosed = request.State == "Closed";
            var original = wasClosed ? await ClosedSnapshot(requestId, ct) : null;
            receipt = new FinanceCommandReceipt
            {
                CommandId = command.CommandId,
                WorkspaceId = request.WorkspaceId,
                ActorId = actor,
                Action = receiptAction,
                PayloadSha256 = hash,
                Status = 200,
                ResultJson = "{}",
                CreatedAtUtc = clock.GetUtcNow()
            };
            db.Add(receipt);
            if (SettlementAccess.Role(action) is { } role)
                await participation.RecordAsync(family.Id, requestId, decision.PersonId.Value, role, ct);
            await db.SaveChangesAsync(ct);
            if (action != SettlementActions.IncidentRelease && entity is not null && command.EntityVersion is { } initialVersion)
                entity.Property("Version").CurrentValue = checked(initialVersion + 1);
            activeCommand = command.CommandId; activeRequest = requestId; activeAction = action; activeEntity = entityId;
            FinanceResult<SettlementCommandResult> result;
            try { result = await mutate(ct); }
            finally { activeCommand = null; activeRequest = null; activeAction = null; activeEntity = null; }
            if (result.Status is < 200 or >= 300) return result;
            if (result.Value is null || result.Value.RequestId != requestId
                || entityId is not null && result.Value.EntityId != entityId)
                throw new InvalidOperationException("ผลคำสั่งเคลียร์ไม่ตรงขอบเขต");
            if (entityId is null && result.Value.EntityId is { } createdId)
            {
                // A create result must reference a new row owned by this receipt, never an arbitrary ID.
                entity = db.ChangeTracker.Entries().SingleOrDefault(e => (e.Metadata.FindProperty("AcceptedAtUtc") is not null
                    || action == SettlementActions.ShortfallPrepare && e.Entity is AdvanceInstallment && e.State == EntityState.Added
                        && db.Set<PaymentShortfallClaim>().Local.Any(c => c.InstallmentId == createdId && c.CommandId == command.CommandId))
                    && e.Metadata.FindProperty("Id") is not null && (Guid)e.Property("Id").CurrentValue! == createdId
                    && (e.Entity is AdvanceInstallment || e.Metadata.FindProperty("CommandId") is not null && (Guid)e.Property("CommandId").CurrentValue! == command.CommandId));
                if (entity is null || !SettlementAccess.InScope(entity, request))
                    throw new InvalidOperationException("รายการใหม่ไม่ตรงคำสั่งเคลียร์");
            }
            family.Version = Math.Max(family.Version, checked(command.FamilyVersion + 1));
            if (!wasClosed) request.Version = Math.Max(request.Version, checked(command.RequestVersion + 1));
            result = result with
            {
                Value = result.Value with
                {
                    RequestVersion = request.Version,
                    FamilyVersion = family.Version,
                    EntityVersion = entity is null ? null : (long)entity.Property("Version").CurrentValue!
                }
            };
            receipt.Status = result.Status; receipt.ResultJson = JsonSerializer.Serialize(result);
            await audit.WriteAsync(new SecurityAuditRequest(actor, null, request.WorkspaceId, request.ProjectId, null,
                "field-advance.settlement." + action, "field-advance-request", requestId, "success",
                new Dictionary<string, string> { ["scope-validation"] = "authorized" }), ct);
            await db.SaveChangesAsync(ct);
            if (wasClosed && original != await ClosedSnapshot(requestId, ct))
                throw new InvalidOperationException("เคสหลังปิดห้ามแก้คำขอหรือฉบับเดิม");
            var final = await access.CheckAsync(actor, requestId, action, entityId, ct);
            if (!final.Allowed || final.PersonId != decision.PersonId) return Denied();
            if (finalAuthorization is { } validate && !await validate(ct)) return Denied();
            await tx.CommitAsync(ct);
            return result;
        }
        catch (DbUpdateConcurrencyException) { return Conflict(); }
        finally { finalAuthorization = null; db.ChangeTracker.Clear(); }
    }

    // Include operation, entity ID and the complete typed input in service payloads as well.
    public static string Hash<T>(T input) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input)));
    private async Task<string> ClosedSnapshot(Guid request, CancellationToken ct) => JsonSerializer.Serialize(new
    {
        Request = await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == request, ct),
        Revisions = await db.Set<AdvanceRequestRevision>().AsNoTracking().Where(x => x.RequestId == request).OrderBy(x => x.Id).ToArrayAsync(ct)
    });
    private static FinanceResult<SettlementCommandResult> Denied() => new(403, "finance-access-denied", null);
    private static FinanceResult<SettlementCommandResult> Conflict() => new(409, "settlement-conflict", null);
}
