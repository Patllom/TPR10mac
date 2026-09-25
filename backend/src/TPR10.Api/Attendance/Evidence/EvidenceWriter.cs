using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Access;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Attendance.Storage;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.Scopes;

namespace TPR10.Api.Attendance.Evidence;

// Internal composition contract for 6C, deliberately not an HTTP upload/publish API.
public sealed class EvidenceWriter(Tpr10DbContext db, ScopeAccess session, IImageStampService images,
    StorageRuntime storage, IAuditEventWriter audit, TimeProvider clock)
{
    public async Task<PreparedEvidence> PrepareAsync(EvidenceReservation reservation, Stream image, StampRequest stamp, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null) throw new InvalidOperationException("Prepare must run outside the caller transaction.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(50));
        ct = deadline.Token;
        var bytes = await ReadInputAsync(image, ct);
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        long fence;
        StorageLocation location;
        await using (var tx = await ScopeOperation.BeginAsync(db, ct))
        {
            var row = await LoadAsync(reservation.EvidenceId, ct);
            await ValidateOwnerAsync(row, ct);
            if (row.OperationId != reservation.OperationId || row.StorageId != reservation.StorageId || row.StorageVersion != reservation.StorageVersion
                || row.ObjectKey != reservation.ObjectKey || row.OccurredAtUtc != stamp.OccurredAtUtc || row.Action != stamp.Action
                || row.InputSha256 is { } original && original != digest || row.State == EvidenceState.Orphan)
                throw new StorageOperationException(409);
            if (row.State is EvidenceState.Prepared or EvidenceState.Published)
                return await PreparedAsync(row, ct);
            if (row.LeaseUntilUtc > clock.GetUtcNow()) throw new StorageOperationException(409);
            row.InputSha256 = digest;
            row.LeaseUntilUtc = DirectoryQueries.Now(clock).AddMinutes(1);
            row.FencingVersion++; row.Version++;
            fence = row.FencingVersion;
            location = await db.Set<StorageLocation>().AsNoTracking().SingleAsync(x => x.Id == row.StorageId, ct);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }

        // Native decoding and filesystem work never hold the identity/revocation lock.
        var full = await images.StampAsync(new MemoryStream(bytes, writable: false), stamp, ct);
        ct.ThrowIfCancellationRequested();
        var thumbnail = images.Thumbnail(full);
        ct.ThrowIfCancellationRequested();
        var adapter = storage.Resolve(location);
        await adapter.WriteImmutableAsync(StorageObjectKey.Create(reservation.EvidenceId, "full"), full.Jpeg, ct);
        await adapter.WriteImmutableAsync(StorageObjectKey.Create(reservation.EvidenceId, "thumbnail"), thumbnail.Jpeg, ct);

        await using var finish = await ScopeOperation.BeginAsync(db, ct);
        var evidence = await LoadAsync(reservation.EvidenceId, ct);
        await ValidateOwnerAsync(evidence, ct);
        if (evidence.State != EvidenceState.Reserved || evidence.FencingVersion != fence || evidence.LeaseUntilUtc <= clock.GetUtcNow())
            throw new StorageOperationException(409);
        evidence.Sha256 = full.Sha256; evidence.Length = full.Jpeg.Length; evidence.Width = full.Width; evidence.Height = full.Height;
        evidence.ThumbnailSha256 = thumbnail.Sha256; evidence.ThumbnailLength = thumbnail.Jpeg.Length;
        evidence.ThumbnailWidth = thumbnail.Width; evidence.ThumbnailHeight = thumbnail.Height;
        evidence.State = EvidenceState.Prepared; evidence.LeaseUntilUtc = null; evidence.Version++;
        var fullCopy = Copy(evidence, full, "full");
        db.Add(fullCopy); db.Add(Copy(evidence, thumbnail, "thumbnail"));
        await AuditAsync(evidence, "prepare", ct);
        await db.SaveChangesAsync(ct); await finish.CommitAsync(ct);
        return new(evidence.Id, evidence.OperationId, fullCopy.Id, full.Sha256, full.Jpeg.Length, full.Width, full.Height);
    }

    // Caller rechecks challenge, exact Site and attendance pair, adds the event and commits all together.
    // Acquiring the same transaction-scoped lock is reentrant and protects direct service callers too.
    public async Task StagePublicationAsync(EvidencePublication publication, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Publication requires a caller transaction.");
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct);
        var row = await LoadAsync(publication.EvidenceId, ct);
        await ValidateOwnerAsync(row, ct);
        var snapshot = new EmploymentSnapshot(row.MembershipId, row.OwnerId, row.WorkspaceId, row.DepartmentId, row.SnapshotAtUtc);
        if (row.OperationId != publication.OperationId || publication.EventId == Guid.Empty || snapshot != publication.Subject
            || row.OccurredAtUtc != publication.Stamp.OccurredAtUtc || row.Action != publication.Stamp.Action)
            throw new StorageOperationException(409);
        if (row.State == EvidenceState.Published)
        {
            if (await db.Set<EvidenceBinding>().AnyAsync(x => x.EvidenceId == row.Id && x.EventId == publication.EventId, ct)) return;
            throw new StorageOperationException(409);
        }
        if (row.State != EvidenceState.Prepared) throw new StorageOperationException(409);
        var candidates = await db.Set<EvidenceLocation>().Where(x => x.EvidenceId == row.Id
            && (x.State == CopyState.Active || (x.StorageId == row.StorageId && x.State == CopyState.Verified))).ToArrayAsync(ct);
        var copies = candidates.GroupBy(x => x.Variant).Select(x => x.OrderByDescending(c => c.State == CopyState.Active).First()).ToArray();
        if (copies.Length != 2 || copies.Select(x => x.Variant).Distinct().Count() != 2) throw new StorageOperationException(503);
        foreach (var copy in copies.Where(x => x.State != CopyState.Active)) { copy.State = CopyState.Active; copy.Version++; }
        row.State = EvidenceState.Published; row.Version++;
        db.Add(new EvidenceBinding { EvidenceId = row.Id, EventId = publication.EventId, PublishedAtUtc = DirectoryQueries.Now(clock) });
        await AuditAsync(row, "publish", ct);
        // No SaveChanges or Commit: the caller owns the complete business transaction.
    }

    private async Task<EvidenceObject> LoadAsync(Guid id, CancellationToken ct)
    {
        var row = await db.Set<EvidenceObject>().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new StorageOperationException(404);
        await db.Entry(row).ReloadAsync(ct); // Never authorize a stale tracked reservation after I/O.
        return row;
    }
    private async Task ValidateOwnerAsync(EvidenceObject row, CancellationToken ct)
    {
        var identity = await session.ValidateSessionAsync(false, ct);
        if (identity.Status is { } status) throw new StorageOperationException(status);
        if (identity.ActorId != row.OwnerId) throw new StorageOperationException(404);
    }
    private async Task<PreparedEvidence> PreparedAsync(EvidenceObject row, CancellationToken ct)
    {
        var copy = await db.Set<EvidenceLocation>().AsNoTracking().SingleAsync(x => x.EvidenceId == row.Id && x.StorageId == row.StorageId && x.Variant == "full", ct);
        return new(row.Id, row.OperationId, copy.Id, row.Sha256!, row.Length!.Value, row.Width!.Value, row.Height!.Value);
    }
    private EvidenceLocation Copy(EvidenceObject row, StampedImage image, string variant) => new()
    {
        Id = Guid.NewGuid(),
        EvidenceId = row.Id,
        StorageId = row.StorageId,
        ObjectKey = StorageObjectKey.Create(row.Id, variant),
        Variant = variant,
        Sha256 = image.Sha256,
        Length = image.Jpeg.Length,
        State = CopyState.Verified,
        VerifiedAtUtc = DirectoryQueries.Now(clock),
        CreatedAtUtc = DirectoryQueries.Now(clock)
    };
    private Task AuditAsync(EvidenceObject row, string action, CancellationToken ct) => audit.WriteAsync(new SecurityAuditRequest(
        row.OwnerId, null, row.WorkspaceId, null, null, "attendance.evidence." + action, "attendance-evidence", row.Id, "success", new Dictionary<string, string>()), ct);
    private static async Task<byte[]> ReadInputAsync(Stream input, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, ImageLimits.MaxBytes + 1 - (int)output.Length)), ct);
            if (count == 0) return output.ToArray();
            output.Write(buffer, 0, count);
            if (output.Length > ImageLimits.MaxBytes) throw new ImageProcessingException("image-too-large");
        }
    }
}
