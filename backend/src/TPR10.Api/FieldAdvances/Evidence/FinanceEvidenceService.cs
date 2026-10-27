using Microsoft.EntityFrameworkCore;
using SkiaSharp;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Payments;

namespace TPR10.Api.FieldAdvances.Evidence;

public sealed class FinanceEvidenceService(Tpr10DbContext db, PaymentAccess access, IFinanceEvidenceStore store,
    IFinancePdfValidator pdf, FinanceEvidenceOptions options, TimeProvider clock)
{
    public async Task<FinanceResult<EvidenceTicket>> StageAsync(Guid actor, Guid requestId, Stream content, string declaredType, CancellationToken ct)
    {
        RequireNoTransaction();
        if (!await Authorized(actor, requestId, "record", ct)) return new(403, "finance-access-denied", null);
        try { options.Validate(); } catch (InvalidOperationException) { return new(503, "evidence-unavailable", null); }
        var bytes = await BoundedRead(content, ct);
        if (bytes is null || !await ValidContent(bytes, declaredType, ct)) return new(422, "evidence-invalid", null);
        var key = Guid.NewGuid().ToString("N"); EvidenceTicket stored;
        try
        {
            stored = await store.WriteImmutableAsync(options.ActiveStorageId, key, new MemoryStream(bytes, false), ct);
            await store.ReadVerifiedAsync(options.ActiveStorageId, key, stored.Sha256, stored.Length, ct);
        }
        catch (IOException) { return new(503, "evidence-unavailable", null); }
        ct.ThrowIfCancellationRequested();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var decision = await access.CheckAsync(actor, requestId, "record", ct);
            if (!decision.Allowed) return new(403, "finance-access-denied", null);
            var request = await FamilyParticipation.LockRequestAsync(db, requestId, ct);
            if (request is null || decision.PersonId is null) return new(403, "finance-access-denied", null);
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
            await FamilyParticipation.RecordAuthorizedAsync(db, request, decision.PersonId.Value, "Payer", actor, now, ct);
            await db.SaveChangesAsync(ct);
            if (!(await access.CheckAsync(actor, requestId, "record", ct)).Allowed)
            { return new(403, "finance-access-denied", null); }
            await transaction.CommitAsync(ct);
            return new(201, "evidence-staged", new(id, stored.Sha256, stored.Length, declaredType, "Ready"));
        }
        catch (Exception e) when (e is DbUpdateException or Npgsql.PostgresException)
        { return new(409, "evidence-persist-failed", null); }
        finally { db.ChangeTracker.Clear(); }
    }

    // I/O preparation is outside financial transactions. Later command writers must recheck the
    // selected Ready rows and their request/limits within their own locked transaction before linking.
    public Task<bool> ValidateLinksAsync(Guid actor, Guid requestId, Guid[] evidenceIds, CancellationToken ct)
        => ValidateLinksCoreAsync(actor, requestId, evidenceIds, false, ct);

    public Task<bool> ValidateCheckerLinksAsync(Guid actor, Guid requestId, Guid[] evidenceIds, CancellationToken ct)
        => ValidateLinksCoreAsync(actor, requestId, evidenceIds, true, ct);

    private async Task<bool> ValidateLinksCoreAsync(Guid actor, Guid requestId, Guid[] evidenceIds, bool checker, CancellationToken ct)
    {
        RequireNoTransaction();
        if (evidenceIds is null || evidenceIds.Length is 0 || evidenceIds.Length > options.MaxFiles
            || evidenceIds.Any(x => x == Guid.Empty) || evidenceIds.Distinct().Count() != evidenceIds.Length) return false;
        if (!await Authorized(actor, requestId, checker ? "reconcile" : "record", ct)) return false;
        var rows = await db.Set<AdvanceEvidence>().AsNoTracking().Where(x => evidenceIds.Contains(x.Id)).ToArrayAsync(ct);
        if (rows.Length != evidenceIds.Length || rows.Any(x => x.RequestId != requestId || x.State != "Ready")
            || rows.Sum(x => x.Length) > options.MaxTotalBytes) return false;
        foreach (var row in rows)
        {
            var result = await ReadAsync(actor, row.Id, ct);
            if (result.Status != 200) return false;
        }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            if (!(await access.CheckAsync(actor, requestId, checker ? "reconcile" : "record", ct)).Allowed) return false;
            var current = await db.Set<AdvanceEvidence>().AsNoTracking().Where(x => evidenceIds.Contains(x.Id)).ToArrayAsync(ct);
            return current.Length == rows.Length && current.All(x => x.RequestId == requestId && x.State == "Ready"
                && rows.Any(before => before.Id == x.Id && before.Sha256 == x.Sha256 && before.Length == x.Length))
                && current.Sum(x => x.Length) <= options.MaxTotalBytes;
        }
        finally { db.ChangeTracker.Clear(); }
    }

    public async Task<FinanceResult<byte[]>> ReadAsync(Guid actor, Guid evidenceId, CancellationToken ct)
    {
        RequireNoTransaction();
        var row = await db.Set<AdvanceEvidence>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == evidenceId, ct);
        if (row is null || !await Authorized(actor, row.RequestId, "evidence-read", ct)) return new(403, "finance-access-denied", null);
        var location = await db.Set<AdvanceEvidenceLocation>().AsNoTracking().SingleOrDefaultAsync(x => x.EvidenceId == evidenceId && x.IsCurrent, ct);
        if (row.State != "Ready" || location is null || !EvidenceLocator.TryParse(location.StorageKey, out var locator)) return new(409, "evidence-unavailable", null);
        byte[] bytes;
        try { bytes = await store.ReadVerifiedAsync(locator.StorageId, locator.Key, row.Sha256, row.Length, ct); }
        catch (Exception e) when (e is IOException or InvalidOperationException) { return new(409, "evidence-unavailable", null); }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            if (!(await access.CheckAsync(actor, row.RequestId, "evidence-read", ct)).Allowed) return new(403, "finance-access-denied", null);
            var current = await db.Set<AdvanceEvidence>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.Id, ct);
            if (current is null || current.State != "Ready" || current.Sha256 != row.Sha256 || current.Length != row.Length
                || !await db.Set<AdvanceEvidenceLocation>().AnyAsync(x => x.Id == location.Id && x.IsCurrent && x.StorageKey == location.StorageKey, ct))
                return new(409, "evidence-changed", null);
            await transaction.CommitAsync(ct); return new(200, "evidence", bytes);
        }
        finally { db.ChangeTracker.Clear(); }
    }
    private async Task<bool> Authorized(Guid actor, Guid requestId, string action, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try { return (await access.CheckAsync(actor, requestId, action, ct)).Allowed; }
        finally { db.ChangeTracker.Clear(); }
    }
    private void RequireNoTransaction()
    {
        if (db.Database.CurrentTransaction != null) throw new InvalidOperationException("Prepare evidence I/O before entering the financial transaction.");
    }
    private async Task<byte[]?> BoundedRead(Stream content, CancellationToken ct)
    {
        using var memory = new MemoryStream(); var buffer = new byte[65536]; int read;
        while ((read = await content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, options.MaxFileBytes - memory.Length + 1)), ct)) != 0)
        {
            if (memory.Length + read > options.MaxFileBytes) return null;
            await memory.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return memory.Length == 0 ? null : memory.ToArray();
    }
    private async Task<bool> ValidContent(byte[] bytes, string declaredType, CancellationToken ct)
    {
        if (declaredType == "application/pdf") return bytes.AsSpan().StartsWith("%PDF-"u8) && await pdf.ValidateAsync(bytes, ct);
        if (declaredType is not ("image/png" or "image/jpeg")) return false;
        using var codec = SKCodec.Create(new MemoryStream(bytes, false));
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0
            || (long)codec.Info.Width * codec.Info.Height > options.MaxImagePixels
            || codec.EncodedFormat != (declaredType == "image/png" ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg)) return false;
        if (declaredType == "image/png" && !bytes.AsSpan().EndsWith(new byte[] { 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130 })) return false;
        if (declaredType == "image/jpeg" && !bytes.AsSpan().EndsWith(new byte[] { 255, 217 })) return false;
        using var bitmap = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888));
        return codec.GetPixels(bitmap.Info, bitmap.GetPixels()) == SKCodecResult.Success;
    }
}
