using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Payments;

namespace TPR10.Api.FieldAdvances.Evidence;

// Operator-only service. No HTTP endpoint and no implied financial read authority for administrators.
public sealed class FinanceEvidenceMigration(Tpr10DbContext db, IFinanceEvidenceStore store, TimeProvider clock)
{
    public async Task<bool> CopyAndSwitchAsync(Guid evidenceId, Guid targetStorageId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction != null) throw new InvalidOperationException("Migration I/O must run outside financial locks.");
        db.ChangeTracker.Clear();
        var row = await db.Set<AdvanceEvidence>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == evidenceId, ct);
        var before = await db.Set<AdvanceEvidenceLocation>().AsNoTracking().SingleOrDefaultAsync(x => x.EvidenceId == evidenceId && x.IsCurrent, ct);
        if (row is null || before is null || !EvidenceLocator.TryParse(before.StorageKey, out var source) || targetStorageId == Guid.Empty) return false;
        var key = Guid.NewGuid().ToString("N");
        try
        {
            var bytes = await store.ReadVerifiedAsync(source.StorageId, source.Key, row.Sha256, row.Length, ct);
            await store.WriteImmutableAsync(targetStorageId, key, new MemoryStream(bytes, false), ct);
            await store.ReadVerifiedAsync(targetStorageId, key, row.Sha256, row.Length, ct);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException) { return false; }
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            if (await FamilyParticipation.LockRequestAsync(db, row.RequestId, ct) is null) return false;
            var current = await db.Set<AdvanceEvidenceLocation>().SingleOrDefaultAsync(x => x.EvidenceId == evidenceId && x.IsCurrent, ct);
            var evidence = await db.Set<AdvanceEvidence>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == evidenceId, ct);
            if (current is null || current.Id != before.Id || current.StorageKey != before.StorageKey || current.VerifiedAtUtc != before.VerifiedAtUtc
                || evidence is null || evidence.Sha256 != row.Sha256 || evidence.Length != row.Length) return false;
            current.IsCurrent = false; await db.SaveChangesAsync(ct); // free the partial unique index inside this transaction
            db.Add(new AdvanceEvidenceLocation
            {
                Id = Guid.NewGuid(),
                WorkspaceId = row.WorkspaceId,
                ProjectId = row.ProjectId,
                FamilyId = row.FamilyId,
                RequestId = row.RequestId,
                EvidenceId = row.Id,
                StorageKey = new EvidenceLocator(targetStorageId, key).ToString(),
                IsCurrent = true,
                VerifiedAtUtc = clock.GetUtcNow(),
                CreatedAtUtc = clock.GetUtcNow()
            });
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return true;
        }
        catch (Exception e) when (e is DbUpdateException or Npgsql.PostgresException)
        { return false; }
        finally { db.ChangeTracker.Clear(); }
    }
}
