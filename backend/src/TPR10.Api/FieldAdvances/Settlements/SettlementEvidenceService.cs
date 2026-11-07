using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Payments;

using TPR10.Api.FieldAdvances.Evidence;
namespace TPR10.Api.FieldAdvances.Settlements;

public sealed class SettlementEvidenceService(Tpr10DbContext db, SettlementAccess access, IFinanceEvidenceStore store,
    IFinancePdfValidator pdf, FinanceEvidenceOptions options, TimeProvider clock)
{
    public Task<FinanceResult<Guid>> UploadAdjustmentAsync(Guid actor, Guid requestId, AdjustmentEvidenceTarget target,
        Stream content, string declaredType, CancellationToken ct)
        => UploadCoreAsync(actor, requestId, target, content, declaredType, ct);

    public Task<FinanceResult<Guid>> UploadAsync(Guid actor, Guid requestId, Stream content, string declaredType, CancellationToken ct)
        => UploadCoreAsync(actor, requestId, null, content, declaredType, ct);

    private async Task<FinanceResult<Guid>> UploadCoreAsync(Guid actor, Guid requestId, AdjustmentEvidenceTarget? target,
        Stream content, string declaredType, CancellationToken ct)
    {
        RequireNoTransaction();
        db.ChangeTracker.Clear();
        await using (var authorization = await db.Database.BeginTransactionAsync(ct))
            if (!(await UploadAccessAsync(actor, requestId, target, ct)).Allowed) return new(403, "finance-access-denied", default);
        db.ChangeTracker.Clear();
        try { options.Validate(); } catch (InvalidOperationException) { return new(503, "evidence-unavailable", default); }
        var validator = new FinanceEvidenceValidation(pdf, options);
        var bytes = await validator.BoundedRead(content, ct);
        if (bytes is null || !await validator.ValidContent(bytes, declaredType, ct)) return new(422, "evidence-invalid", default);
        var key = Guid.NewGuid().ToString("N"); EvidenceTicket stored;
        try
        {
            stored = await store.WriteImmutableAsync(options.ActiveStorageId, key, new MemoryStream(bytes, false), ct);
            await store.ReadVerifiedAsync(options.ActiveStorageId, key, stored.Sha256, stored.Length, ct);
        }
        catch (IOException) { return new(503, "evidence-unavailable", default); }
        ct.ThrowIfCancellationRequested();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var decision = await UploadAccessAsync(actor, requestId, target, ct);
            if (!decision.Allowed) return new(403, "finance-access-denied", default);
            var request = await FamilyParticipation.LockRequestAsync(db, requestId, ct);
            if (request is null || decision.PersonId is null) return new(403, "finance-access-denied", default);
            var now = clock.GetUtcNow(); var id = Guid.NewGuid();
            db.Add(new AdvanceEvidence
            {
                Id = id,
                WorkspaceId = request.WorkspaceId,
                ProjectId = request.ProjectId,
                FamilyId = request.FamilyId,
                RequestId = requestId,
                Sha256 = stored.Sha256,
                Length = stored.Length,
                ContentType = declaredType,
                State = "Ready",
                CreatedBy = actor,
                CreatedByPersonId = decision.PersonId.Value,
                CreatedAtUtc = now
            });
            db.Add(new AdvanceEvidenceLocation
            {
                Id = Guid.NewGuid(),
                WorkspaceId = request.WorkspaceId,
                ProjectId = request.ProjectId,
                FamilyId = request.FamilyId,
                RequestId = requestId,
                EvidenceId = id,
                StorageKey = new EvidenceLocator(options.ActiveStorageId, key).ToString(),
                IsCurrent = true,
                VerifiedAtUtc = now,
                CreatedAtUtc = now
            });
            await db.SaveChangesAsync(ct);
            if (!(await UploadAccessAsync(actor, requestId, target, ct)).Allowed)
            { return new(403, "finance-access-denied", default); }
            await transaction.CommitAsync(ct);
            return new(201, "evidence-staged", id);
        }
        catch (Exception e) when (e is DbUpdateException or Npgsql.PostgresException)
        { return new(409, "evidence-persist-failed", default); }
        finally { db.ChangeTracker.Clear(); }
    }

    internal async Task<SettlementAccessDecision> UploadAccessAsync(Guid actor, Guid requestId, AdjustmentEvidenceTarget? target, CancellationToken ct)
    {
        if (target is null) return await access.CheckAsync(actor, requestId, SettlementActions.Document, null, ct);
        var action = target switch
        {
            ExpenseEvidenceTarget => SettlementActions.ExpensePropose,
            ExcessPaymentEvidenceTarget => SettlementActions.ExcessPaymentCreate,
            ExcessDispositionEvidenceTarget => SettlementActions.ExcessDisposition,
            IncidentRecoveryEvidenceTarget => SettlementActions.RecoveryPropose,
            CorrectionEvidenceTarget => SettlementActions.CorrectionCreate,
            CaseRefundEvidenceTarget => SettlementActions.CaseRefundCreate,
            CasePaymentEvidenceTarget => SettlementActions.CasePaymentCreate,
            _ => "invalid"
        };
        var decision = await access.CheckAsync(actor, requestId, action, target is ExcessDispositionEvidenceTarget disposition ? disposition.ExcessId : null, ct);
        if (!decision.Allowed) return decision;
        var request = await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == requestId, ct);
        var valid = target switch
        {
            CorrectionEvidenceTarget source => (request.State is "Approved" or "Closed") && await db.Set<Refund>().AnyAsync(x => x.Id == source.RefundId && x.RequestId == requestId
                && x.WorkspaceId == request.WorkspaceId && x.ProjectId == request.ProjectId && x.FamilyId == request.FamilyId
                && x.State == "Accepted" && x.AcceptedAtUtc != null, ct)
                && (request.State != "Closed" || await db.Set<ClosureRecord>().AnyAsync(x => x.RequestId == requestId && x.WorkspaceId == request.WorkspaceId
                    && x.ProjectId == request.ProjectId && x.FamilyId == request.FamilyId && x.AcceptedAtUtc != null, ct)),
            ExpenseEvidenceTarget source => await db.Set<SettlementRound>().AnyAsync(x => x.Id == source.SourceRoundId && x.RequestId == requestId && x.AcceptedAtUtc != null, ct)
                && (request.State != "Closed" || await db.Set<ClosureRecord>().AnyAsync(x => x.RequestId == requestId && x.AcceptedAtUtc != null, ct)),
            ExcessPaymentEvidenceTarget source => await ExpenseExcessService.ActiveAsync(db, request, source.ExcessId, ct),
            ExcessDispositionEvidenceTarget source => await ExpenseExcessService.ActiveAsync(db, request, source.ExcessId, ct),
            IncidentRecoveryEvidenceTarget source => await db.Set<PaymentIncidentResolution>().AnyAsync(x =>
                x.Id == source.IncidentId && x.RequestId == requestId && x.WorkspaceId == request.WorkspaceId
                && x.ProjectId == request.ProjectId && x.FamilyId == request.FamilyId
                && x.State == "Accepted" && x.AcceptedAtUtc != null, ct),
            CaseRefundEvidenceTarget source => request.State == "Closed" && await AdjustmentCaseService.ActiveAsync(db, request, source.CaseId, ct),
            CasePaymentEvidenceTarget source => request.State == "Closed" && await AdjustmentCaseService.ActiveAsync(db, request, source.CaseId, ct),
            _ => false
        };
        return valid ? decision : new(false, null, "finance-access-denied");
    }

    // I/O preparation is outside financial transactions. Later command writers must recheck the
    // selected Ready rows and their request/limits within their own locked transaction before linking.
    public async Task<bool> ValidateLinksAsync(Guid actor, Guid requestId, Guid[] evidenceIds, CancellationToken ct)
        => await PrepareLinksAsync(actor, requestId, evidenceIds, ct) is not null;

    internal async Task<AdvanceEvidence[]?> PrepareLinksAsync(Guid actor, Guid requestId, Guid[] evidenceIds, CancellationToken ct, AdjustmentEvidenceTarget? target = null)
    {
        RequireNoTransaction();
        try { options.Validate(); } catch (InvalidOperationException) { return null; }
        if (evidenceIds is null || evidenceIds.Length is 0 || evidenceIds.Length > options.MaxFiles
            || evidenceIds.Any(x => x == Guid.Empty) || evidenceIds.Distinct().Count() != evidenceIds.Length) return null;
        if (!await Authorized(actor, requestId, SettlementActions.Document, ct, target)) return null;
        var rows = await db.Set<AdvanceEvidence>().AsNoTracking().Where(x => evidenceIds.Contains(x.Id)).ToArrayAsync(ct);
        if (rows.Length != evidenceIds.Length || rows.Any(x => x.RequestId != requestId || x.State != "Ready")
            || rows.Sum(x => x.Length) > options.MaxTotalBytes) return null;
        foreach (var row in rows) if ((await ReadAsync(actor, row.Id, ct)).Status != 200) return null;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            if (!(target is null ? await access.CheckAsync(actor, requestId, SettlementActions.Document, null, ct) : await UploadAccessAsync(actor, requestId, target, ct)).Allowed) return null;
            return await LockedLinksAsync(requestId, rows, ct);
        }
        finally { db.ChangeTracker.Clear(); }
    }
    internal async Task<AdvanceEvidence[]?> LockedLinksAsync(Guid requestId, AdvanceEvidence[] prepared, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("ต้องตรวจหลักฐานภายในธุรกรรม");
        var ids = prepared.Select(x => x.Id).ToArray();
        if (ids.Length == 0 || ids.Length > options.MaxFiles || ids.Distinct().Count() != ids.Length) return null;
        var rows = await db.Set<AdvanceEvidence>().FromSqlInterpolated(
            $"SELECT * FROM field_advance_evidence WHERE request_id={requestId} AND id=ANY({ids}) ORDER BY id FOR UPDATE").AsNoTracking().ToArrayAsync(ct);
        return rows.Length == ids.Length && rows.All(x => x.State == "Ready" && prepared.Any(before => SameEvidence(before, x)))
            && rows.Sum(x => x.Length) <= options.MaxTotalBytes ? rows : null;
    }

    public Task<FinanceResult<byte[]>> ReadAsync(Guid actor, Guid evidenceId, CancellationToken ct)
        => ReadCoreAsync(evidenceId, async (row, token) => (await access.CheckAsync(actor, row.RequestId, SettlementActions.Read, null, token)).Allowed, ct);

    internal Task<FinanceResult<byte[]>> ReadRoundAsync(Guid actor, Guid request, Guid round, Guid revision, Guid evidenceId, SettlementUiService ui, CancellationToken ct)
        => ReadCoreAsync(evidenceId, async (row, token) => row.RequestId == request
            && await ui.BoundProofAsync(actor, request, round, revision, evidenceId, token) is not null, ct, request);

    internal Task<FinanceResult<byte[]>> ReadWorkAsync(Guid actor, Guid request, string lane, Guid entity,
        Guid evidenceId, SettlementWorkService work, CancellationToken ct)
        => ReadCoreAsync(evidenceId, async (row, token) => row.RequestId == request
            && await work.BoundEvidenceAsync(actor, request, lane, entity, evidenceId, token) is not null,
            ct, request);

    private async Task<FinanceResult<byte[]>> ReadCoreAsync(Guid evidenceId, Func<AdvanceEvidence, CancellationToken, Task<bool>> authorize,
        CancellationToken ct, Guid? registryRequest = null)
    {
        RequireNoTransaction();
        var row = await db.Set<AdvanceEvidence>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == evidenceId, ct);
        if (row is null) return new(403, "finance-access-denied", default);
        await using (var before = await db.Database.BeginTransactionAsync(ct))
        {
            if (registryRequest is { } request) await DuplicateDocumentService.LockRegistryAsync(db, request, ct);
            if (!await authorize(row, ct)) return new(403, "finance-access-denied", default);
        }
        var location = await db.Set<AdvanceEvidenceLocation>().AsNoTracking().SingleOrDefaultAsync(x => x.EvidenceId == evidenceId && x.IsCurrent, ct);
        if (row.State != "Ready" || location is null || !EvidenceLocator.TryParse(location.StorageKey, out var locator)) return new(409, "evidence-unavailable", null);
        byte[] bytes;
        try { bytes = await store.ReadVerifiedAsync(locator.StorageId, locator.Key, row.Sha256, row.Length, ct); }
        catch (Exception e) when (e is IOException or InvalidOperationException) { return new(409, "evidence-unavailable", null); }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            if (registryRequest is { } request) await DuplicateDocumentService.LockRegistryAsync(db, request, ct);
            if (!await authorize(row, ct)) return new(403, "finance-access-denied", default);
            var current = await db.Set<AdvanceEvidence>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.Id, ct);
            if (current is null || current.State != "Ready" || !SameEvidence(row, current)
                || !await db.Set<AdvanceEvidenceLocation>().AnyAsync(x => x.Id == location.Id && x.IsCurrent && x.StorageKey == location.StorageKey, ct))
                return new(409, "evidence-changed", null);
            await transaction.CommitAsync(ct); return new(200, "evidence", bytes);
        }
        finally { db.ChangeTracker.Clear(); }
    }
    private static bool SameEvidence(AdvanceEvidence before, AdvanceEvidence after) => before.Id == after.Id
        && before.RequestId == after.RequestId && before.WorkspaceId == after.WorkspaceId && before.ProjectId == after.ProjectId
        && before.FamilyId == after.FamilyId && before.Sha256 == after.Sha256 && before.Length == after.Length && before.ContentType == after.ContentType;

    private async Task<bool> Authorized(Guid actor, Guid requestId, string action, CancellationToken ct, AdjustmentEvidenceTarget? target = null)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try { return (target is null ? await access.CheckAsync(actor, requestId, action, null, ct) : await UploadAccessAsync(actor, requestId, target, ct)).Allowed; }
        finally { db.ChangeTracker.Clear(); }
    }
    private void RequireNoTransaction()
    {
        if (db.Database.CurrentTransaction != null) throw new InvalidOperationException("Prepare evidence I/O before entering the financial transaction.");
    }
}
