using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Access;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Auditing;
using TPR10.Api.Scopes;

namespace TPR10.Api.Attendance.Storage;

public sealed class StorageOperationException(int status) : Exception("storage-operation-" + status)
{
    public int Status { get; } = status;
}

public sealed partial class StorageRegistry
{
    // Internal application contract only. 6C must validate the camera challenge and exact Site first.
    // No filesystem I/O here: the writer revalidates the pinned mount before doing any I/O.
    public async Task<EvidenceReservation> PinAsync(Guid operationId, EmploymentSnapshot subject, StampRequest stamp, CancellationToken ct)
    {
        try
        {
            await using var tx = await ScopeOperation.BeginAsync(db, ct);
            var identity = await session.ValidateSessionAsync(false, ct);
            if (identity.Status is { } status) throw new StorageOperationException(status);
            if (subject.EmployeeId != identity.ActorId) throw new StorageOperationException(403);
            if (operationId == Guid.Empty || !Enum.IsDefined(stamp.Action) || subject.OccurredAtUtc != stamp.OccurredAtUtc
                || stamp.OccurredAtUtc > clock.GetUtcNow() || stamp.OccurredAtUtc.UtcTicks % 10 != 0)
                throw new StorageOperationException(400);
            if (!await DirectoryQueries.Employment(db, subject.OccurredAtUtc).AnyAsync(x => x.Id == subject.MembershipId
                    && x.UserId == identity.ActorId && x.WorkspaceId == subject.WorkspaceId && x.DepartmentId == subject.DepartmentId, ct))
                throw new StorageOperationException(403);
            var row = await db.Set<EvidenceObject>().SingleOrDefaultAsync(x => x.OperationId == operationId, ct);
            if (row is not null)
            {
                if (row.OwnerId != identity.ActorId || row.MembershipId != subject.MembershipId || row.WorkspaceId != subject.WorkspaceId
                    || row.DepartmentId != subject.DepartmentId || row.SnapshotAtUtc != subject.OccurredAtUtc
                    || row.OccurredAtUtc != stamp.OccurredAtUtc || row.Action != stamp.Action)
                    throw new StorageOperationException(409);
            }
            else
            {
                var target = await db.Set<StorageWriteTarget>().AsNoTracking().SingleOrDefaultAsync(ct);
                var storage = target?.StorageId is { } id ? await db.Set<StorageLocation>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) : null;
                if (storage is null || !runtime.Ready(storage)) throw new StorageOperationException(503);
                var evidenceId = Guid.NewGuid();
                row = new EvidenceObject
                {
                    Id = evidenceId,
                    OperationId = operationId,
                    OwnerId = identity.ActorId,
                    MembershipId = subject.MembershipId,
                    WorkspaceId = subject.WorkspaceId,
                    DepartmentId = subject.DepartmentId,
                    SnapshotAtUtc = subject.OccurredAtUtc,
                    OccurredAtUtc = stamp.OccurredAtUtc,
                    Action = stamp.Action,
                    StorageId = storage.Id,
                    StorageVersion = storage.Version,
                    ObjectKey = StorageObjectKey.Create(evidenceId, "full")[..^9],
                    CreatedAtUtc = DirectoryQueries.Now(clock)
                };
                db.Add(row);
            }
            await audit.WriteAsync(new SecurityAuditRequest(identity.ActorId, null, null, null, null,
                "attendance.evidence.pin", "attendance-evidence", row.Id, "success", new Dictionary<string, string>()), ct);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return new(row.Id, row.OperationId, row.StorageId, row.StorageVersion, row.ObjectKey);
        }
        catch (StorageOperationException error)
        {
            db.ChangeTracker.Clear();
            try
            {
                await using var denied = await ScopeOperation.BeginAsync(db, ct);
                await audit.WriteAsync(new SecurityAuditRequest(Actor == Guid.Empty ? null : Actor, null, null, null, null,
                    "attendance.evidence.pin", "attendance-evidence", null, "denied",
                    new Dictionary<string, string> { ["reason"] = "request-denied-" + error.Status }), ct);
                await db.SaveChangesAsync(ct); await denied.CommitAsync(ct);
            }
            catch (Exception fault) when (ScopeOperation.IsDatabaseFault(fault)) { db.ChangeTracker.Clear(); throw new StorageOperationException(503); }
            throw;
        }
        catch (Exception error) when (ScopeOperation.IsDatabaseFault(error) || error is IOException)
        { db.ChangeTracker.Clear(); throw new StorageOperationException(503); }
    }
}
