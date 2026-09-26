using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.Scopes;

namespace TPR10.Api.Attendance.Storage;

public sealed class MigrationWorker(Tpr10DbContext db, StorageRuntime runtime, IAuditEventWriter audit, TimeProvider clock)
{
    private static readonly int[] RetrySeconds = [1, 5, 30, 120, 300];
    private sealed record Claim(MigrationItem Item, StorageLocation Source, StorageLocation Target);

    public async Task<int> RunBatchAsync(Guid jobId, int limit, CancellationToken ct)
    {
        if (jobId == Guid.Empty || limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        if (db.Database.CurrentTransaction is not null) throw new InvalidOperationException("Migration owns its short transactions.");
        await ReconcileCompletionAsync(jobId, ct);
        var completed = 0;
        for (var i = 0; i < limit; i++)
        {
            ct.ThrowIfCancellationRequested();
            var claim = await ClaimAsync(jobId, ct);
            if (claim is null) break;
            string? failure = null;
            try
            {
                var item = claim.Item;
                var key = StorageObjectKey.Create(item.EvidenceId, item.Variant);
                var bytes = await runtime.Resolve(claim.Source).ReadVerifiedAsync(key, item.ExpectedSha256, item.ExpectedLength, ct);
                Verify(bytes, item);
                var target = runtime.Resolve(claim.Target);
                var written = await target.WriteImmutableAsync(key, bytes, ct);
                if (written.Sha256 != item.ExpectedSha256 || written.Length != item.ExpectedLength) throw new IOException("storage-checksum-mismatch");
                Verify(await target.ReadVerifiedAsync(key, item.ExpectedSha256, item.ExpectedLength, ct), item);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                failure = error.Message == "storage-checksum-mismatch" ? "checksum-mismatch" : "storage-unavailable";
            }
            // Cancellation or database/audit failure intentionally leaves a leased item. A new process can reclaim it.
            if (await FinishAsync(claim, failure, ct)) completed++;
        }
        await ReconcileCompletionAsync(jobId, ct);
        return completed;
    }

    private async Task<Claim?> ClaimAsync(Guid jobId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var tx = await ScopeOperation.BeginAsync(db, ct);
        var job = await db.Set<MigrationJob>().SingleOrDefaultAsync(x => x.Id == jobId, ct);
        if (job is null || job.Status is "Blocked" or "Completed") return null;
        if (!await MigrationAuthority.AllowsAsync(db, job.RequestedBy, ct))
        {
            await BlockAsync(job, null, "creator-authority-revoked", ct);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return null;
        }
        var source = await db.Set<StorageLocation>().AsNoTracking().SingleAsync(x => x.Id == job.SourceId, ct);
        var target = await db.Set<StorageLocation>().AsNoTracking().SingleAsync(x => x.Id == job.TargetId, ct);
        if (source.AcceptWrites || !target.AcceptWrites || await db.Set<StorageWriteTarget>().AnyAsync(x => x.StorageId == source.Id, ct))
        {
            await BlockAsync(job, null, "storage-seal-changed", ct);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return null;
        }
        var now = DirectoryQueries.Now(clock);
        var item = await db.Set<MigrationItem>().Where(x => x.JobId == jobId
            && ((x.Status == "Pending" && (x.NextAttemptAtUtc == null || x.NextAttemptAtUtc <= now))
                || (x.Status == "Copying" && x.LeaseUntilUtc <= now)))
            .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.EvidenceId).ThenBy(x => x.Variant).FirstOrDefaultAsync(ct);
        if (item is null) { await tx.CommitAsync(ct); return null; }
        if (item.Attempts >= 6)
        {
            await BlockAsync(job, item, "retry-exhausted", ct);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return null;
        }
        item.Status = "Copying"; item.Attempts++; item.LeaseOwner = Guid.NewGuid(); item.LeaseUntilUtc = now.AddSeconds(60);
        item.FencingVersion++; item.Version++; item.NextAttemptAtUtc = null;
        job.Status = "Running"; job.Version++;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(item, source, target);
    }

    private async Task<bool> FinishAsync(Claim claim, string? failure, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var tx = await ScopeOperation.BeginAsync(db, ct);
        var item = await db.Set<MigrationItem>().SingleAsync(x => x.Id == claim.Item.Id, ct);
        var job = await db.Set<MigrationJob>().SingleAsync(x => x.Id == item.JobId, ct);
        var now = DirectoryQueries.Now(clock);
        if (job.Status != "Running" || item.Status != "Copying" || item.LeaseOwner != claim.Item.LeaseOwner
            || item.FencingVersion != claim.Item.FencingVersion || item.LeaseUntilUtc <= now) return false;
        if (!await MigrationAuthority.AllowsAsync(db, job.RequestedBy, ct))
        {
            await BlockAsync(job, item, "creator-authority-revoked", ct);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return false;
        }
        var source = await db.Set<StorageLocation>().AsNoTracking().SingleAsync(x => x.Id == item.SourceId, ct);
        var target = await db.Set<StorageLocation>().AsNoTracking().SingleAsync(x => x.Id == item.TargetId, ct);
        if (source.AcceptWrites || !target.AcceptWrites) failure = "storage-seal-changed";
        else if (source.Version != claim.Source.Version || target.Version != claim.Target.Version) failure ??= "storage-version-changed";
        else
        {
            try { if (!runtime.Matches(source) || !runtime.Matches(target)) failure ??= "storage-unavailable"; }
            catch (IOException) { failure ??= "storage-unavailable"; }
        }
        var evidence = await db.Set<EvidenceObject>().AsNoTracking().SingleAsync(x => x.Id == item.EvidenceId, ct);
        var original = await db.Set<EvidenceLocation>().SingleOrDefaultAsync(x => x.EvidenceId == item.EvidenceId && x.StorageId == item.SourceId && x.Variant == item.Variant, ct);
        if (evidence.State is not (EvidenceState.Prepared or EvidenceState.Published) || original is null || original.State is not (CopyState.Active or CopyState.Verified)
            || original.VerifiedAtUtc is null || original.Sha256 != item.ExpectedSha256 || original.Length != item.ExpectedLength
            || (item.Variant == "full" ? evidence.Sha256 : evidence.ThumbnailSha256) != item.ExpectedSha256
            || (item.Variant == "full" ? evidence.Length : evidence.ThumbnailLength) != item.ExpectedLength) failure = "evidence-changed";
        if (failure is not null)
        {
            if (failure is "checksum-mismatch" or "evidence-changed" or "storage-seal-changed" || item.Attempts > RetrySeconds.Length)
                await BlockAsync(job, item, failure, ct);
            else
            {
                item.Status = "Pending"; item.ErrorCode = failure; item.NextAttemptAtUtc = now.AddSeconds(RetrySeconds[item.Attempts - 1]);
                Release(item); job.Version++;
                await AuditAsync(job, item, "retry", failure, ct);
            }
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return false;
        }
        var destination = await db.Set<EvidenceLocation>().SingleOrDefaultAsync(x => x.EvidenceId == item.EvidenceId && x.StorageId == item.TargetId && x.Variant == item.Variant, ct);
        if (destination is not null && (destination.Sha256 != item.ExpectedSha256 || destination.Length != item.ExpectedLength || destination.State == CopyState.Quarantined))
        {
            await BlockAsync(job, item, "destination-quarantined", ct);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return false;
        }
        // Release the partial unique Active index first; the publication constraint is deferred until commit.
        original!.State = CopyState.Fallback; original.Version++;
        await db.SaveChangesAsync(ct);
        if (destination is null)
        {
            destination = new EvidenceLocation
            {
                Id = Guid.NewGuid(),
                EvidenceId = item.EvidenceId,
                StorageId = item.TargetId,
                Variant = item.Variant,
                ObjectKey = StorageObjectKey.Create(item.EvidenceId, item.Variant),
                Sha256 = item.ExpectedSha256,
                Length = item.ExpectedLength,
                State = CopyState.Active,
                VerifiedAtUtc = now,
                CreatedAtUtc = now
            };
            db.Add(destination);
        }
        else { destination.State = CopyState.Active; destination.VerifiedAtUtc ??= now; destination.Version++; }
        item.Status = "Completed"; item.ErrorCode = null; Release(item); job.Version++;
        await AuditAsync(job, item, "cutover", "verified-copy-source-retained", ct);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return true;
    }

    private async Task ReconcileCompletionAsync(Guid id, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var tx = await ScopeOperation.BeginAsync(db, ct);
        var job = await db.Set<MigrationJob>().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (job is null || job.Status is "Blocked" or "Completed") return;
        if (!await MigrationAuthority.AllowsAsync(db, job.RequestedBy, ct))
            await BlockAsync(job, null, "creator-authority-revoked", ct);
        else
        {
            var now = DirectoryQueries.Now(clock);
            await MigrationManifest.ReconcileAsync(db, job, now, ct);
            await db.SaveChangesAsync(ct);
            var reservations = db.Set<EvidenceObject>().Where(x => x.StorageId == job.SourceId && x.State == EvidenceState.Reserved);
            if (await reservations.AnyAsync(ct))
            {
                if (now >= job.CreatedAtUtc.AddSeconds(60) && await reservations.AnyAsync(x => x.LeaseUntilUtc == null || x.LeaseUntilUtc <= now, ct))
                    await BlockAsync(job, null, "unfinished-reservation", ct);
            }
            else if (!await db.Set<MigrationItem>().AnyAsync(x => x.JobId == id && x.Status != "Completed", ct)
                && !await db.Set<EvidenceLocation>().AnyAsync(x => x.StorageId == job.SourceId && x.State == CopyState.Active, ct))
            {
                job.Status = "Completed"; job.Version++;
                await AuditAsync(job, null, "completed", "source-retained-not-approved-for-removal", ct);
            }
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }

    private async Task BlockAsync(MigrationJob job, MigrationItem? item, string reason, CancellationToken ct)
    {
        job.Status = "Blocked"; job.Version++;
        if (item is not null) { item.Status = "Blocked"; item.ErrorCode = reason; Release(item); }
        await AuditAsync(job, item, "blocked", reason, ct);
    }
    private static void Release(MigrationItem item) { item.LeaseOwner = null; item.LeaseUntilUtc = null; item.Version++; }
    private static void Verify(byte[] bytes, MigrationItem item)
    {
        if (bytes.LongLength != item.ExpectedLength || Convert.ToHexStringLower(SHA256.HashData(bytes)) != item.ExpectedSha256)
            throw new IOException("storage-checksum-mismatch");
    }
    private Task AuditAsync(MigrationJob job, MigrationItem? item, string action, string reason, CancellationToken ct) => audit.WriteAsync(new SecurityAuditRequest(
        job.RequestedBy, null, null, null, null, "attendance.storage.migration-" + action, "attendance-storage", job.Id,
        action is "blocked" or "retry" ? "failure" : "success", new Dictionary<string, string>
        {
            ["capability"] = StorageRegistry.Capability,
            ["reason"] = reason,
            ["source"] = job.SourceId.ToString(),
            ["destination-type"] = job.TargetId.ToString(),
            ["evidence-reference"] = item is null ? "manifest" : item.EvidenceId + ":" + item.Variant
        }), ct);
}
