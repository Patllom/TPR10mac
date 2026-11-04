using System.Text;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
namespace TPR10.Api.FieldAdvances.Settlements;

public sealed class SettlementUiService(Tpr10DbContext db, SettlementAccess access, SettlementReadService read, IDataProtectionProvider protection)
{
    internal static bool ReviewOperationMatches(string operation, Guid commandId, object entity)
    {
        var slash = operation.LastIndexOf('/');
        if (slash < 0 || operation[..slash] is not ("corrections" or "case-payments" or "excess-payments"))
            return true;
        var expected = operation[(slash + 1)..] switch
        {
            "confirm" => "Accept",
            "return" => "Return",
            "reject" => "Reject",
            _ => null
        };
        if (expected is null) return false;
        CorrectionHistory[]? history = entity switch
        {
            RefundCorrection row => RefundCorrectionService.Read(row)?.History,
            SettlementCasePayment row => AdjustmentCaseService.ReadPayment(row)?.History,
            SettlementExcessPayment row => ExpenseExcessService.ReadPayment(row)?.History,
            _ => null
        };
        return history?.Any(entry => entry.CommandId == commandId && entry.Operation == expected) == true;
    }
    internal async Task<SettlementVersion?> VersionAsync(Guid request, CancellationToken ct) => await
        (from r in db.Set<AdvanceRequest>().AsNoTracking()
         join f in db.Set<AdvanceFamily>().AsNoTracking() on r.FamilyId equals f.Id
         where r.Id == request
         select new SettlementVersion(r.Version, f.Version)).SingleOrDefaultAsync(ct);
    internal async Task<SettlementHttp.ProjectionOutcome> ValidateAsync(Guid actor, Guid request, SettlementVersion version,
        CancellationToken ct, string action = SettlementActions.Read, Guid? entity = null)
    {
        if (!(await access.CheckAsync(actor, request, action, entity, ct)).Allowed) return SettlementHttp.ProjectionOutcome.Denied;
        return await VersionAsync(request, ct) == version ? SettlementHttp.ProjectionOutcome.Current : SettlementHttp.ProjectionOutcome.Stale;
    }
    // Recovery is read-only: it never reconstructs command input or executes a mutation.
    // The committed result binds request/entity; create rows additionally bind their original command ID.
    public async Task<IResult> ReceiptAsync(Guid actor, Guid request, Guid commandId, string operation, Guid? entityId, CancellationToken ct)
    {
        var action = operation switch
        {
            "rounds/create" or "rounds/update" => SettlementActions.Draft,
            "rounds/submit" => SettlementActions.Confirm,
            "rounds/review" => SettlementActions.Approve,
            "documents/create" or "documents/update" => SettlementActions.Document,
            "refunds/create" => SettlementActions.RefundPropose,
            "refunds/update" => SettlementActions.RefundUpdate,
            "refunds/confirm" => SettlementActions.RefundConfirm,
            "refunds/cancel" => SettlementActions.RefundCancel,
            "refunds/review" => SettlementActions.RefundReview,
            "incidents/confirm" => SettlementActions.IncidentReview,
            "incidents/recover" => SettlementActions.RecoveryPropose,
            "incidents/release" => SettlementActions.IncidentRelease,
            "incidents/shortfall" => SettlementActions.ShortfallPrepare,
            "recoveries/confirm" => SettlementActions.RecoveryReview,
            "corrections/confirm" or "corrections/return" or "corrections/reject" => SettlementActions.CorrectionReview,
            "case-payments/confirm" or "case-payments/return" or "case-payments/reject" => SettlementActions.CasePaymentReview,
            "excess-payments/confirm" or "excess-payments/return" or "excess-payments/reject" => SettlementActions.ExcessPaymentReview,
            "completion/correction-confirm" => SettlementActions.CompletionReview,
            "duplicates/review" => SettlementActions.DuplicateReview,
            "excess/disposition" => SettlementActions.ExcessDisposition,
            "declaration" => SettlementActions.Declare,
            _ => null
        };
        var create = operation is "rounds/create" or "documents/create" or "refunds/create" or "declaration";
        if (action is null || commandId == Guid.Empty
            || (operation is "incidents/recover" or "incidents/shortfall"
                ? entityId is null : create != (entityId is null)))
            return SettlementHttp.Error(403, "finance-access-denied");
        db.ChangeTracker.Clear(); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var receipt = await db.Set<FinanceCommandReceipt>().AsNoTracking().SingleOrDefaultAsync(x => x.CommandId == commandId, ct);
        FinanceResult<SettlementCommandResult>? saved = null;
        try { if (receipt is not null) saved = JsonSerializer.Deserialize<FinanceResult<SettlementCommandResult>>(receipt.ResultJson); }
        catch (JsonException) { return SettlementHttp.Error(403, "finance-access-denied"); }
        Guid? target = entityId;
        if (operation is "incidents/recover" or "incidents/shortfall") target = null;
        if (operation is "rounds/submit" or "rounds/review")
            target = saved?.Value?.EntityId ?? await db.Set<SettlementRevision>().Where(x => x.RequestId == request && x.RoundId == entityId)
                .OrderByDescending(x => x.RevisionNumber).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
        if (operation == "rounds/review" && target is { } stepId
            && await db.Set<SettlementApprovalStep>().AnyAsync(x => x.Id == stepId && x.LevelCode == "Finance", ct))
            action = SettlementActions.Check;
        if (operation == "refunds/create" && saved?.Value?.EntityId is { } refundId
            && await db.Set<Refund>().AnyAsync(x => x.Id == refundId && x.RequestId == request && x.AdjustmentCaseId != null, ct))
            action = SettlementActions.CaseRefundCreate;
        if (!(await access.CheckAsync(actor, request, action, target, ct)).Allowed)
            return SettlementHttp.Error(403, "finance-access-denied");
        if (receipt is null) return SettlementHttp.Error(404, "settlement-receipt-not-found");
        var scope = await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == request, ct);
        if (receipt.ActorId != actor || receipt.WorkspaceId != scope.WorkspaceId || receipt.Action != "settlement." + action
            || saved?.Value is not { EntityId: { } resultId } value || value.RequestId != request || saved.Status is < 200 or >= 300 || receipt.Status != saved.Status)
            return SettlementHttp.Error(403, "finance-access-denied");
        if (operation == "incidents/shortfall")
        {
            var claim = await db.Set<PaymentShortfallClaim>().AsNoTracking().SingleOrDefaultAsync(x =>
                x.CommandId == commandId && x.IncidentId == entityId && x.InstallmentId == resultId
                && x.RequestId == request && x.WorkspaceId == scope.WorkspaceId
                && x.ProjectId == scope.ProjectId && x.FamilyId == scope.FamilyId, ct);
            var installment = await db.Set<AdvanceInstallment>().AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == resultId && x.RequestId == request && x.WorkspaceId == scope.WorkspaceId
                && x.ProjectId == scope.ProjectId && x.FamilyId == scope.FamilyId, ct);
            if (claim is null || installment is null || !await db.Set<PaymentIncidentResolution>().AnyAsync(x =>
                x.Id == entityId && x.RequestId == request && x.State == "Accepted" && x.AcceptedAtUtc != null, ct))
                return SettlementHttp.Error(403, "finance-access-denied");
            var stampForClaim = (await VersionAsync(request, ct))!;
            return SettlementHttp.ProtectReceiptLookup(actor, request,
                new(commandId, operation, entityId, value), async token =>
            {
                var authorization = await ValidateAsync(actor, request, stampForClaim, token, action, null);
                if (authorization != SettlementHttp.ProjectionOutcome.Current) return authorization;
                var currentReceipt = await db.Set<FinanceCommandReceipt>().AsNoTracking()
                    .SingleOrDefaultAsync(x => x.CommandId == commandId, token);
                if (currentReceipt is null || currentReceipt.ActorId != actor
                    || currentReceipt.Action != receipt.Action || currentReceipt.ResultJson != receipt.ResultJson
                    || currentReceipt.PayloadSha256 != receipt.PayloadSha256) return SettlementHttp.ProjectionOutcome.Denied;
                return await db.Set<PaymentShortfallClaim>().AnyAsync(x => x.Id == claim.Id
                    && x.CommandId == commandId && x.IncidentId == entityId && x.InstallmentId == resultId
                    && x.RequestId == request, token)
                    && await db.Set<AdvanceInstallment>().AnyAsync(x => x.Id == resultId
                        && x.RequestId == request && x.Version == installment.Version, token)
                    ? SettlementHttp.ProjectionOutcome.Current : SettlementHttp.ProjectionOutcome.Stale;
            });
        }
        var entityAction = action == SettlementActions.CaseRefundCreate ? SettlementActions.RefundPropose : action;
        var resultEntity = await access.LoadEntityAsync(entityAction, resultId, scope, ct);
        if (resultEntity is null || !SettlementAccess.InScope(resultEntity, scope)
            || !ReviewOperationMatches(operation, commandId, resultEntity.Entity)
            || create && (Guid)resultEntity.Property("CommandId").CurrentValue! != commandId
            || !create && operation is not ("rounds/submit" or "rounds/review" or "incidents/recover") && value.EntityId != entityId
            || (operation is "rounds/update" or "documents/update") && (Guid)resultEntity.Property("CommandId").CurrentValue! == commandId
            || operation == "rounds/submit" && (resultEntity.Entity is not SettlementRevision revision || revision.RoundId != entityId)
            || operation == "rounds/review" && (resultEntity.Entity is not SettlementApprovalStep step
                || !await db.Set<SettlementRevision>().AnyAsync(x => x.Id == step.RevisionId && x.RoundId == entityId
                    && x.RequestId == request, ct))
            || operation == "incidents/recover" && (resultEntity.Entity is not PaymentIncidentRecovery recovery
                || recovery.ResolutionId != entityId || recovery.CommandId != commandId))
            return SettlementHttp.Error(403, "finance-access-denied");
        var version = (long)resultEntity.Property("Version").CurrentValue!;
        var stamp = (await VersionAsync(request, ct))!;
        return SettlementHttp.ProtectReceiptLookup(actor, request, new(commandId, operation, entityId, value), async token =>
        {
            var authorization = await ValidateAsync(actor, request, stamp, token, action, target);
            if (authorization != SettlementHttp.ProjectionOutcome.Current) return authorization;
            var currentReceipt = await db.Set<FinanceCommandReceipt>().AsNoTracking().SingleOrDefaultAsync(x => x.CommandId == commandId, token);
            if (currentReceipt is null || currentReceipt.ActorId != actor || currentReceipt.Action != receipt.Action
                || currentReceipt.WorkspaceId != receipt.WorkspaceId || currentReceipt.PayloadSha256 != receipt.PayloadSha256 || currentReceipt.ResultJson != receipt.ResultJson)
                return SettlementHttp.ProjectionOutcome.Denied;
            var currentEntity = await access.LoadEntityAsync(entityAction, resultId, scope, token);
            return currentEntity is null || !SettlementAccess.InScope(currentEntity, scope)
                || !ReviewOperationMatches(operation, commandId, currentEntity.Entity)
                ? SettlementHttp.ProjectionOutcome.Denied
                : (long)currentEntity.Property("Version").CurrentValue! == version ? SettlementHttp.ProjectionOutcome.Current : SettlementHttp.ProjectionOutcome.Stale;
        });
    }
    public async Task<IResult> SummaryAsync(Guid actor, Guid request, CancellationToken ct)
    {
        var stamp = await VersionAsync(request, ct);
        var value = await read.ReadAsync(actor, request, ct);
        if (value.Value is not { } row || stamp is null) return SettlementHttp.Error(value.Status, value.Code);
        var wire = new SettlementSummaryHttp(row.RequestId, row.State, row.Totals, row.Holds, row.AllowedActions, stamp.RequestVersion, stamp.FamilyVersion, row.Declaration);
        return SettlementHttp.Protect(actor, request, Results.Json(wire, SettlementHttp.Json), async token =>
        {
            var state = await ValidateAsync(actor, request, stamp, token);
            if (state != SettlementHttp.ProjectionOutcome.Current) return state;
            foreach (var action in row.AllowedActions)
                if (!(await access.CheckAsync(actor, request, action, null, token)).Allowed) return SettlementHttp.ProjectionOutcome.Denied;
            var current = await read.Summary(actor, request, token);
            return current.Value is not null && JsonSerializer.Serialize(current.Value, SettlementHttp.Json) == JsonSerializer.Serialize(row, SettlementHttp.Json)
                ? state : SettlementHttp.ProjectionOutcome.Stale;
        });
    }
    private sealed record FamilyCursor(Guid Actor, Guid Request, Guid Family, long FamilyVersion, Guid After);
    public async Task<IResult> FamilyAsync(Guid actor, Guid request, CancellationToken ct, int take = 25, string? cursor = null)
    {
        if (take is < 1 or > 100 || cursor?.Length > 2048) return SettlementHttp.Error(400, "invalid-page");
        var protector = protection.CreateProtector("TPR10.Settlement.FamilyPage.v1");
        FamilyCursor? continuation = null;
        if (cursor is not null)
        {
            try { continuation = JsonSerializer.Deserialize<FamilyCursor>(protector.Unprotect(cursor)); }
            catch (Exception error) when (error is CryptographicException or JsonException or FormatException)
            { return SettlementHttp.Error(400, "invalid-cursor"); }
            if (continuation is null || continuation.Actor != actor || continuation.Request != request || continuation.After == Guid.Empty)
                return SettlementHttp.Error(400, "invalid-cursor");
        }
        db.ChangeTracker.Clear(); await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!(await access.CheckAsync(actor, request, SettlementActions.Read, null, ct)).Allowed) return SettlementHttp.Error(403, "finance-access-denied");
        var stamp = (await VersionAsync(request, ct))!;
        var family = await db.Set<AdvanceRequest>().Where(x => x.Id == request).Select(x => x.FamilyId).SingleAsync(ct);
        if (continuation is not null && (continuation.Family != family || continuation.FamilyVersion != stamp.FamilyVersion))
            return SettlementHttp.Error(409, "settlement-projection-changed");
        var after = continuation?.After;
        var result = await read.ReadFamilyPageLockedAsync(actor, request, take, after, ct);
        if (result.Value is not { } value) return SettlementHttp.Error(result.Status, result.Code);
        // Protect (encrypt + authenticate) the last SCANNED key: it may belong to a hidden request.
        var next = value.HasMore && value.LastScannedId is { } last
            ? protector.Protect(JsonSerializer.Serialize(new FamilyCursor(actor, request, family, stamp.FamilyVersion, last))) : null;
        var wire = new SettlementFamilyPage(request, value.Requests, value.PageTotals, "visible-page", value.PageComplete,
            value.PageSettled, next, stamp.FamilyVersion);
        var frozen = JsonSerializer.Serialize(value, SettlementHttp.Json);
        return SettlementHttp.Protect(actor, request, Results.Json(wire, SettlementHttp.Json), async token =>
        {
            var state = await ValidateAsync(actor, request, stamp, token);
            if (state != SettlementHttp.ProjectionOutcome.Current) return state;
            foreach (var row in value.Requests)
                if (!(await access.CheckAsync(actor, row.RequestId, SettlementActions.Read, null, token)).Allowed)
                    return SettlementHttp.ProjectionOutcome.Denied;
            var current = await read.ReadFamilyPageLockedAsync(actor, request, take, after, token);
            if (current.Value is null) return SettlementHttp.ProjectionOutcome.Denied;
            return JsonSerializer.Serialize(current.Value, SettlementHttp.Json) == frozen
                ? SettlementHttp.ProjectionOutcome.Current : SettlementHttp.ProjectionOutcome.Stale;
        });
    }
    internal static string Money(decimal value) => value.ToString("0.00##", System.Globalization.CultureInfo.InvariantCulture);
    internal static SettlementPreviewHttp PreviewWire(SettlementPreview value)
    {
        using var doc = JsonDocument.Parse(value.SnapshotJson); var root = doc.RootElement;
        var lines = root.GetProperty("Lines").EnumerateArray().Select(x =>
        {
            var allocations = JsonSerializer.Deserialize<SettlementDraftService.LineEvidence>(x.GetProperty("SnapshotJson").GetString()!)!.Allocations;
            string? Number(string key) => x.GetProperty(key).ValueKind == JsonValueKind.Null ? null : Money(x.GetProperty(key).GetDecimal());
            return new SettlementLineHttp(x.GetProperty("Id").GetGuid(), x.GetProperty("SourceLineId").ValueKind == JsonValueKind.Null ? null : x.GetProperty("SourceLineId").GetGuid(),
                x.GetProperty("CategoryId").GetGuid(), x.GetProperty("Description").GetString()!, DateOnly.Parse(x.GetProperty("IncurredOn").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                Number("Quantity"), Number("Rate"), Number("LumpSum"), Money(x.GetProperty("Amount").GetDecimal()), allocations.Select(a => new SettlementAllocationHttp(a.DocumentId, Money(a.Amount))).ToArray());
        }).ToArray();
        var documents = root.GetProperty("Documents").Deserialize<ExpenseDocument[]>()!.Select(x => new SettlementDocumentHttp(x.Id, x.Version, x.State, x.Issuer,
            x.DocumentNumber, x.HasNoNumber, x.IssuedOn, Money(x.Amount), JsonSerializer.Deserialize<ExpenseDocumentService.DocumentEvidence>(x.SnapshotJson)?.EvidenceIds ?? [x.EvidenceId], JsonSerializer.Deserialize<ExpenseDocumentService.DocumentEvidence>(x.SnapshotJson)?.EvidenceType)).ToArray();
        var evidence = root.GetProperty("Evidence").Deserialize<SettlementProofHttp[]>()!;
        return new(value.RequestId, value.RoundId, value.RevisionId, value.RequestVersion, value.FamilyVersion, value.RoundVersion, value.RevisionVersion,
            value.ConfirmationHash, Money(root.GetProperty("Total").GetDecimal()), Money(root.GetProperty("Approved").GetDecimal()), lines, documents, evidence);
    }
    internal async Task<SettlementHttp.ProjectionOutcome> ValidatePreviewAsync(Guid actor, Guid request, SettlementPreview value, CancellationToken ct)
    {
        var state = await ValidateAsync(actor, request, new(value.RequestVersion, value.FamilyVersion), ct, SettlementActions.Confirm, value.RevisionId);
        if (state != SettlementHttp.ProjectionOutcome.Current) return state;
        var revision = await db.Set<SettlementRevision>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == value.RevisionId && x.RequestId == request && x.RoundId == value.RoundId, ct);
        if (revision is null || await RoundWireAsync(actor, request, value.RoundId, value.RevisionId, ct) is null) return SettlementHttp.ProjectionOutcome.Denied;
        var current = await SettlementSubmissionService.BuildPreviewAsync(db, revision, ct);
        return current is not null && current.ConfirmationHash == value.ConfirmationHash && current.RevisionVersion == value.RevisionVersion && current.RoundVersion == value.RoundVersion
            ? SettlementHttp.ProjectionOutcome.Current : SettlementHttp.ProjectionOutcome.Stale;
    }
    private IQueryable<T> Scoped<T>(Guid request) where T : class => db.Set<T>().AsNoTracking().Where(x => EF.Property<Guid>(x, "RequestId") == request
        && db.Set<AdvanceRequest>().Any(r => r.Id == request && r.FamilyId == EF.Property<Guid>(x, "FamilyId")
            && r.ProjectId == EF.Property<Guid>(x, "ProjectId") && r.WorkspaceId == EF.Property<Guid>(x, "WorkspaceId")));
    internal async Task<bool> RoundAccessAsync(Guid actor, Guid request, Guid round, Guid revision, CancellationToken ct)
    {
        // Always check entity scope even for a general reader. Approver access is deliberately not Read.
        if (!await Scoped<SettlementRound>(request).AnyAsync(x => x.Id == round, ct)
            || !await Scoped<SettlementRevision>(request).AnyAsync(x => x.Id == revision && x.RoundId == round, ct)) return false;
        if ((await access.CheckAsync(actor, request, SettlementActions.Read, null, ct)).Allowed) return true;
        var current = await Scoped<SettlementRevision>(request).Where(x => x.RoundId == round).OrderByDescending(x => x.RevisionNumber).FirstOrDefaultAsync(ct);
        if (current?.Id != revision || current.State != "Submitted"
            || !await Scoped<SettlementRound>(request).AnyAsync(x => x.Id == round && x.State == "Submitted", ct)) return false;
        var step = await Scoped<SettlementApprovalStep>(request).Where(x => x.RevisionId == revision && x.State != "Accepted").OrderBy(x => x.StepOrder).FirstOrDefaultAsync(ct);
        return step?.State == "Pending" && (await access.CheckAsync(actor, request,
            step.LevelCode == "Finance" ? SettlementActions.Check : SettlementActions.Approve, step.Id, ct)).Allowed;
    }
    private async Task<SettlementRoundHttp?> RoundWireAsync(Guid actor, Guid request, Guid round, Guid revision, CancellationToken ct)
    {
        var r = await Scoped<SettlementRevision>(request).SingleOrDefaultAsync(x => x.Id == revision && x.RoundId == round, ct);
        var parent = await Scoped<SettlementRound>(request).SingleOrDefaultAsync(x => x.Id == round, ct);
        if (r is null || parent is null) return null;
        var lines = await Scoped<SettlementLine>(request).Where(x => x.RevisionId == revision).OrderBy(x => x.Id).Take(1001).ToArrayAsync(ct);
        if (lines.Length > 1000 || lines.Length != await db.Set<SettlementLine>().CountAsync(x => x.RevisionId == revision, ct)) return null;
        foreach (var line in lines)
            if (!await Owned<SettlementLine>(request, line.SourceLineId, ct)) return null;
        var wireLines = lines.Select(x => new SettlementLineHttp(x.Id, x.SourceLineId, x.CategoryId, x.Description, x.IncurredOn,
            x.Quantity is { } q ? Money(q) : null, x.Rate is { } rate ? Money(rate) : null, x.LumpSum is { } lump ? Money(lump) : null, Money(x.Amount),
            (JsonSerializer.Deserialize<SettlementDraftService.LineEvidence>(x.SnapshotJson)?.Allocations ?? []).Select(a => new SettlementAllocationHttp(a.DocumentId, Money(a.Amount))).ToArray())).ToArray();
        var ids = wireLines.SelectMany(x => x.Allocations).Select(x => x.DocumentId).Distinct().ToArray();
        var documents = await Scoped<ExpenseDocument>(request).Where(x => ids.Contains(x.Id)).OrderBy(x => x.Id).ToArrayAsync(ct);
        if (documents.Length != ids.Length) return null;
        var wireDocuments = documents.Select(DocumentWire).ToArray();
        var evidenceIds = wireDocuments.SelectMany(x => x.EvidenceIds).Distinct().ToArray();
        var evidence = await Scoped<AdvanceEvidence>(request).Where(x => evidenceIds.Contains(x.Id) && x.State == "Ready").OrderBy(x => x.Id)
            .Select(x => new SettlementProofHttp(x.Id, x.State, x.Sha256, x.Length, x.ContentType)).ToArrayAsync(ct);
        if (evidence.Length != evidenceIds.Length) return null;
        var stamp = (await VersionAsync(request, ct))!;
        var step = await Scoped<SettlementApprovalStep>(request).Where(x => x.RevisionId == revision && x.State == "Pending").OrderBy(x => x.StepOrder).FirstOrDefaultAsync(ct);
        return new(request, round, revision, stamp.RequestVersion, stamp.FamilyVersion, parent.Version, r.Version, r.State, step?.Id, step?.Version, Money(r.Total), wireLines, wireDocuments, evidence,
            await RoundActionsAsync(actor, request, parent, r, ct), await ReturnReviewAsync(request, r, ct), r.SubmittedAtUtc, SubmissionOnTime(r.SnapshotJson));
    }
    internal async Task<SettlementRoundHttp?> BoundProofAsync(Guid actor, Guid request, Guid round, Guid revision, Guid proof, CancellationToken ct)
    {
        if (!await RoundAccessAsync(actor, request, round, revision, ct)) return null;
        var value = await RoundWireAsync(actor, request, round, revision, ct);
        return value?.Evidence.Any(x => x.Id == proof) == true ? value : null;
    }
    public async Task<IResult> RoundAsync(Guid actor, Guid request, Guid round, Guid revision, CancellationToken ct)
    {
        db.ChangeTracker.Clear(); await using var tx = await db.Database.BeginTransactionAsync(ct);
        await DuplicateDocumentService.LockRegistryAsync(db, request, ct);
        if (!await RoundAccessAsync(actor, request, round, revision, ct)) return SettlementHttp.Error(403, "finance-access-denied");
        var value = await RoundWireAsync(actor, request, round, revision, ct);
        if (value is null) return SettlementHttp.Error(403, "finance-access-denied");
        var frozen = JsonSerializer.Serialize(value, SettlementHttp.Json);
        return SettlementHttp.ProtectRound(actor, request, value, async token =>
        {
            if (!await RoundAccessAsync(actor, request, round, revision, token)) return SettlementHttp.ProjectionOutcome.Denied;
            var current = await RoundWireAsync(actor, request, round, revision, token);
            if (current is null) return SettlementHttp.ProjectionOutcome.Denied;
            return JsonSerializer.Serialize(current, SettlementHttp.Json) == frozen ? SettlementHttp.ProjectionOutcome.Current : SettlementHttp.ProjectionOutcome.Stale;
        });
    }
    private static bool? SubmissionOnTime(string snapshot)
    {
        using var doc = JsonDocument.Parse(snapshot);
        return doc.RootElement.TryGetProperty("Timing", out var timing) && timing.ValueKind == JsonValueKind.Object
            && timing.TryGetProperty("SubmissionOnTime", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    }
    private async Task<string[]> RoundActionsAsync(Guid actor, Guid request, SettlementRound round, SettlementRevision revision, CancellationToken ct)
    {
        var actions = new List<string>();
        var latest = await Scoped<SettlementRevision>(request).Where(x => x.RoundId == round.Id).OrderByDescending(x => x.RevisionNumber).Select(x => x.Id).FirstAsync(ct);
        if (latest != revision.Id || round.AcceptedAtUtc is not null || revision.AcceptedAtUtc is not null) return [];
        var editable = round.State is "Draft" or "Returned" or "AwaitingOwner" && revision.State is "Draft" or "Returned" or "AwaitingOwner";
        var declaration = await SettlementCompletionService.CurrentDeclarationAsync(db, request, ct);
        if (editable && (round.RoundKind == "Adjustment" || revision.State is "Returned" or "AwaitingOwner" || declaration is null || declaration.IsWithdrawal)
            && (await access.CheckAsync(actor, request, SettlementActions.Draft, round.Id, ct)).Allowed) actions.Add(SettlementActions.Draft);
        if (round.State is "Draft" or "AwaitingOwner" && revision.State is "Draft" or "AwaitingOwner" && revision.SubmittedAtUtc is null
            && (await access.CheckAsync(actor, request, SettlementActions.Confirm, revision.Id, ct)).Allowed) actions.Add(SettlementActions.Confirm);
        return actions.ToArray();
    }
    private async Task<string[]> RefundActionsAsync(Guid actor, Guid request, Refund refund, CancellationToken ct)
    {
        var actions = new List<string>();
        if (!RefundService.Editable(refund)) return [];
        var row = await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == request, ct);
        var active = row.State != "Closed" || refund.AdjustmentCaseId is not null;
        if (refund.AdjustmentCaseId is { } caseId) active &= await AdjustmentCaseService.ActiveAsync(db, row, caseId, ct);
        if (active && (await access.CheckAsync(actor, request, SettlementActions.RefundUpdate, refund.Id, ct)).Allowed) actions.Add(SettlementActions.RefundUpdate);
        if (active && refund.State is "Draft" or "Returned" && (await access.CheckAsync(actor, request, SettlementActions.RefundConfirm, refund.Id, ct)).Allowed) actions.Add(SettlementActions.RefundConfirm);
        if ((await access.CheckAsync(actor, request, SettlementActions.RefundCancel, refund.Id, ct)).Allowed) actions.Add(SettlementActions.RefundCancel);
        return actions.ToArray();
    }
    private async Task<SettlementReturnReviewHttp?> ReturnReviewAsync(Guid request, SettlementRevision revision, CancellationToken ct)
    {
        var step = await Scoped<SettlementApprovalStep>(request).Where(x => x.RevisionId == revision.Id && x.State == "Returned").OrderByDescending(x => x.ReviewedAtUtc).FirstOrDefaultAsync(ct);
        if (step is null) return null;
        using var snapshot = JsonDocument.Parse(step.SnapshotJson);
        var ids = snapshot.RootElement.GetProperty("LineIds").EnumerateArray().Select(x => x.GetGuid()).ToArray();
        if (await Scoped<SettlementLine>(request).CountAsync(x => x.RevisionId == revision.Id && ids.Contains(x.Id), ct) != ids.Distinct().Count()) return null;
        return new(revision.Id, step.Id, step.Version, step.Reason, ids, step.ReviewedAtUtc, revision.CorrectionDueOn);
    }
    private static SettlementDocumentHttp DocumentWire(ExpenseDocument x) => new(x.Id, x.Version, x.State, x.Issuer, x.DocumentNumber,
        x.HasNoNumber, x.IssuedOn, Money(x.Amount), JsonSerializer.Deserialize<ExpenseDocumentService.DocumentEvidence>(x.SnapshotJson)?.EvidenceIds ?? [x.EvidenceId], JsonSerializer.Deserialize<ExpenseDocumentService.DocumentEvidence>(x.SnapshotJson)?.EvidenceType);
    private async Task<bool> Owned<T>(Guid request, Guid? id, CancellationToken ct) where T : class => id is null
        || await Scoped<T>(request).AnyAsync(x => EF.Property<Guid>(x, "Id") == id, ct);
    internal async Task<SettlementRow[]?> DetailsAsync<T>(Guid actor, SettlementRow[] rows, Guid request, CancellationToken ct) where T : class
    {
        var result = new List<SettlementRow>();
        foreach (var row in rows)
        {
            var entity = await Scoped<T>(request).SingleOrDefaultAsync(x => EF.Property<Guid>(x, "Id") == row.Id, ct);
            if (entity is null) return null;
            Guid[] proofs = []; SettlementRow item = row with { AllowedActions = [] };
            switch (entity)
            {
                case ExpenseDocument x:
                    var document = DocumentWire(x); proofs = document.EvidenceIds; item = row with { Document = document }; break;
                case Refund x:
                    var refund = RefundService.ReadSnapshot(x);
                    if (refund is null || !await Owned<SettlementAdjustmentCase>(request, x.AdjustmentCaseId, ct)) return null;
                    proofs = refund.Input.EvidenceIds;
                    item = row with { Refund = new(Money(x.Amount), x.RefundedOn, x.Channel, refund.Input.CompanyRecipient, proofs, x.AdjustmentCaseId), AllowedActions = await RefundActionsAsync(actor, request, x, ct), SubmittedAtUtc = refund.Timing?.SubmittedAtUtc, SubmissionOnTime = refund.Timing?.SubmissionOnTime }; break;
                case SettlementRound x:
                    if (!await Owned<SettlementRound>(request, x.SourceRoundId, ct) || !await Owned<SettlementAdjustmentCase>(request, x.AdjustmentCaseId, ct)) return null;
                    var revision = await Scoped<SettlementRevision>(request).Where(r => r.RoundId == x.Id).OrderByDescending(r => r.RevisionNumber).FirstOrDefaultAsync(ct);
                    item = row with { Round = new(x.RoundKind, x.SourceRoundId, x.AdjustmentCaseId, revision?.Id, revision?.Version, revision is null ? null : Money(revision.Total)), AllowedActions = revision is null ? [] : await RoundActionsAsync(actor, request, x, revision, ct), SubmittedAtUtc = revision?.SubmittedAtUtc, SubmissionOnTime = revision is null ? null : SubmissionOnTime(revision.SnapshotJson) }; break;
                case SettlementAdjustmentCase x:
                    if (!await Owned<Refund>(request, x.RootRefundId, ct) || !await Owned<SettlementRound>(request, x.RootExpenseRoundId, ct)) return null;
                    proofs = x.ExternalEvidenceId is { } evidence ? [evidence] : [];
                    item = row with { Adjustment = new(x.Kind, x.RootRefundId, x.RootExpenseRoundId, x.BaselineVersion, x.BaselineAmount is { } baseline ? Money(baseline) : null, Money(x.ExternalPaidAmount), proofs) }; break;
                case PaymentIncidentResolution x:
                    if (!await Owned<AdvanceInstallment>(request, x.InstallmentId, ct) || !await Owned<AdvancePaymentAttempt>(request, x.AttemptId, ct)) return null;
                    proofs = SnapshotProofs(x.SnapshotJson, x.EvidenceId);
                    item = row with { Incident = new(x.InstallmentId, x.AttemptId, Money(x.OwnerReceived), Money(x.WrongRecipientReceived), proofs) }; break;
                case RefundCorrection x:
                    if (!await Owned<Refund>(request, x.RefundId, ct) || !await Owned<RefundCorrection>(request, x.SupersedesId, ct) || !await Owned<SettlementAdjustmentCase>(request, x.AdjustmentCaseId, ct)) return null;
                    proofs = SnapshotProofs(x.SnapshotJson, x.EvidenceId);
                    item = row with { Correction = new(x.RefundId, x.SupersedesId, x.BaseVersion, Money(x.BeforeAmount), Money(x.AfterAmount), Money(x.Delta), x.AdjustmentCaseId, proofs) }; break;
                case SettlementCasePayment x:
                    if (!await Owned<SettlementAdjustmentCase>(request, x.CaseId, ct) || !await Owned<SettlementCasePayment>(request, x.SupersedesId, ct)) return null;
                    proofs = SnapshotProofs(x.SnapshotJson, x.EvidenceId);
                    item = row with { Payment = new(x.CaseId, x.SupersedesId, Money(x.Amount), x.PaidOn, proofs) }; break;
                case SettlementExcessPayment x:
                    if (!await Owned<SettlementExcessCase>(request, x.ExcessId, ct) || !await Owned<SettlementExcessPayment>(request, x.SupersedesId, ct)) return null;
                    proofs = SnapshotProofs(x.SnapshotJson, x.EvidenceId);
                    item = row with { Payment = new(x.ExcessId, x.SupersedesId, Money(x.Amount), x.PaidOn, proofs) }; break;
                case SettlementExcessCase x:
                    if (!await Owned<ExpenseDocument>(request, x.DocumentId, ct) || !await Owned<SettlementRevision>(request, x.SourceRevisionId, ct)) return null;
                    var acceptedPayments = await Scoped<SettlementExcessPayment>(request)
                        .Where(value => value.ExcessId == x.Id && value.AcceptedAtUtc != null)
                        .OrderBy(value => value.Id).Select(value => value.Id).Take(101).ToArrayAsync(ct);
                    if (acceptedPayments.Length > 100) return null;
                    item = row with
                    {
                        Excess = new(x.DocumentId, x.SourceRevisionId, Money(x.Amount),
                        Money(x.ClaimedAmount), Money(x.CapAmount), acceptedPayments)
                    }; break;
                case PaymentIncidentRecovery x:
                    if (!await Owned<PaymentIncidentResolution>(request, x.ResolutionId, ct)) return null;
                    proofs = SnapshotProofs(x.SnapshotJson, x.EvidenceId);
                    item = row with { Recovery = new(x.ResolutionId, x.ReturnKind, Money(x.Amount), x.ReturnedOn, proofs) }; break;
                case WorkCompletion x:
                    if (!await Owned<WorkCompletion>(request, x.SupersedesId, ct)) return null;
                    var previousDate = x.SupersedesId is { } previousId
                        ? await Scoped<WorkCompletion>(request).Where(value => value.Id == previousId)
                            .Select(value => (DateOnly?)value.CompletedOn).SingleAsync(ct)
                        : null;
                    item = row with
                    {
                        Completion = new(x.CompletedOn, x.ClearingDueOn, x.RefundDueOn,
                        x.ClearingDays, x.SupersedesId, previousDate)
                    }; break;
                case ExpenseDuplicateCase x:
                    if (!await Owned<ExpenseDocument>(request, x.DocumentId, ct)) return null;
                    // Cross-request duplicate identifiers, amounts, and free-form review reason are not a local read contract.
                    item = row with { Reason = "", Duplicate = new(x.DocumentId, x.DocumentVersion) }; break;
                default: return null;
            }
            if (await Scoped<AdvanceEvidence>(request).CountAsync(x => proofs.Contains(x.Id), ct) != proofs.Distinct().Count()) return null;
            result.Add(item with { AllowedActions = item.AllowedActions ?? [] });
        }
        return result.ToArray();
    }
    private static Guid[] SnapshotProofs(string snapshot, Guid fallback)
    {
        using var doc = JsonDocument.Parse(snapshot);
        return doc.RootElement.TryGetProperty("Evidence", out var evidence) && evidence.ValueKind == JsonValueKind.Array
            ? evidence.EnumerateArray().Select(x => x.GetProperty("Id").GetGuid()).ToArray() : [fallback];
    }
    public async Task<IResult> PageAsync<T>(Guid actor, Guid request, int take, string? cursor, CancellationToken ct) where T : class
    {
        if (take is < 1 or > 100 || cursor?.Length > 512) return SettlementHttp.Error(400, "invalid-page");
        Guid? after = null;
        if (cursor is not null)
        {
            try
            {
                var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');
                if (parts.Length != 3 || parts[0] != request.ToString("N") || parts[1] != typeof(T).Name || !Guid.TryParseExact(parts[2], "N", out var id))
                    return SettlementHttp.Error(400, "invalid-cursor");
                after = id;
            }
            catch (FormatException) { return SettlementHttp.Error(400, "invalid-cursor"); }
        }
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!(await access.CheckAsync(actor, request, SettlementActions.Read, null, ct)).Allowed) return SettlementHttp.Error(403, "finance-access-denied");
        var stamp = (await VersionAsync(request, ct))!;
        var query = db.Set<T>().AsNoTracking().Where(x => EF.Property<Guid>(x, "RequestId") == request
            && db.Set<AdvanceRequest>().Any(r => r.Id == request && r.FamilyId == EF.Property<Guid>(x, "FamilyId") && r.ProjectId == EF.Property<Guid>(x, "ProjectId") && r.WorkspaceId == EF.Property<Guid>(x, "WorkspaceId")));
        if (after is { } last) query = query.Where(x => EF.Property<Guid>(x, "Id").CompareTo(last) > 0);
        var projection = query.OrderBy(x => EF.Property<Guid>(x, "Id")).Take(take + 1)
            .Select(x => new SettlementRow(EF.Property<Guid>(x, "Id"), EF.Property<Guid>(x, "RequestId"), EF.Property<long>(x, "Version"), EF.Property<string>(x, "State"), EF.Property<string>(x, "Reason")));
        var rows = await DetailsAsync<T>(actor, await projection.ToArrayAsync(ct), request, ct);
        if (rows is null) return SettlementHttp.Error(403, "finance-access-denied");
        var items = rows.Take(take).ToArray();
        var next = rows.Length > take ? Convert.ToBase64String(Encoding.UTF8.GetBytes($"{request:N}|{typeof(T).Name}|{items[^1].Id:N}")) : null;
        return SettlementHttp.Protect(actor, request, Results.Json(new SettlementPage(items, next, stamp.RequestVersion, stamp.FamilyVersion), SettlementHttp.Json),
            async token =>
            {
                var state = await ValidateAsync(actor, request, stamp, token);
                if (state != SettlementHttp.ProjectionOutcome.Current) return state;
                var current = await DetailsAsync<T>(actor, await projection.ToArrayAsync(token), request, token);
                if (current is null) return SettlementHttp.ProjectionOutcome.Denied;
                // Scope/ownership disappearance is denial; data changes with retained rights are stale.
                foreach (var item in items)
                    if (!await db.Set<T>().AnyAsync(x => EF.Property<Guid>(x, "Id") == item.Id && EF.Property<Guid>(x, "RequestId") == request
                        && db.Set<AdvanceRequest>().Any(r => r.Id == request && r.FamilyId == EF.Property<Guid>(x, "FamilyId")
                            && r.ProjectId == EF.Property<Guid>(x, "ProjectId") && r.WorkspaceId == EF.Property<Guid>(x, "WorkspaceId")), token))
                        return SettlementHttp.ProjectionOutcome.Denied;
                return JsonSerializer.Serialize(rows, SettlementHttp.Json) == JsonSerializer.Serialize(current, SettlementHttp.Json) ? SettlementHttp.ProjectionOutcome.Current : SettlementHttp.ProjectionOutcome.Stale;
            });
    }
}
