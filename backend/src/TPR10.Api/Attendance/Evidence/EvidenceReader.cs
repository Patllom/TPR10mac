using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Access;
using TPR10.Api.Attendance.Storage;
using TPR10.Api.Correlation;
using TPR10.Api.Data;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Scopes;
using TPR10.Api.Auditing;

namespace TPR10.Api.Attendance.Evidence;

public sealed class EvidenceReader(Tpr10DbContext db, ScopeAccess session, IAttendanceAccess access, StorageRuntime storage,
    RequestSession current, ICorrelationContext correlation, TimeProvider clock, DbContextOptions<Tpr10DbContext> options, IAuditEventWriter audit)
{
    public async Task<IResult> ReadAsync(Guid id, string variant, bool download, CancellationToken ct)
    {
        try
        {
            EvidenceLocation[] copies;
            StorageLocation[] locations;
            await using (var tx = await ScopeOperation.BeginAsync(db, ct))
            {
                var authorization = await AuthorizeAsync(db, session, access, id, ct);
                if (authorization.Status is { } denial)
                {
                    await audit.WriteAsync(new SecurityAuditRequest(current.Entity?.UserId, null, null, null, null,
                        "attendance.evidence.read", "attendance-evidence", id, "denied", new Dictionary<string, string>()), ct);
                    await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
                    return ScopeOperation.Problem(denial);
                }
                if (variant is not ("full" or "thumbnail")) return ScopeOperation.Problem(404);
                copies = await db.Set<EvidenceLocation>().AsNoTracking().Where(x => x.EvidenceId == id && x.Variant == variant
                    && (x.State == CopyState.Active || x.State == CopyState.Fallback) && x.VerifiedAtUtc != null)
                    .OrderBy(x => x.State == CopyState.Active ? 0 : 1).ThenBy(x => x.Id).Take(16).ToArrayAsync(ct);
                var ids = copies.Select(x => x.StorageId).ToArray();
                locations = await db.Set<StorageLocation>().AsNoTracking().Where(x => ids.Contains(x.Id)).ToArrayAsync(ct);
                await tx.CommitAsync(ct);
            }
            // Materialize bounded, checksum-verified bytes outside all identity locks.
            foreach (var copy in copies)
            {
                if (copy.Length is <= 0 or > ImageLimits.MaxBytes) continue;
                try
                {
                    var location = locations.Single(x => x.Id == copy.StorageId);
                    var bytes = await storage.Resolve(location).ReadVerifiedAsync(copy.ObjectKey, copy.Sha256, copy.Length, ct);
                    return new AuthorizedImageResult(options, db.Database.GetConnectionString()!, current, correlation, clock, copy, bytes, download);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Only registered, verified fallback copies may be tried. */ }
            }
            return ScopeOperation.Problem(503);
        }
        catch (Exception error) when (ScopeOperation.IsDatabaseFault(error) || error is IOException or UnauthorizedAccessException)
        { return ScopeOperation.Problem(503); }
    }

    internal sealed record Authorization(int? Status, AttendanceReadBasis Basis, Guid? WorkspaceId);

    internal static async Task<Authorization> AuthorizeAsync(Tpr10DbContext db, ScopeAccess session, IAttendanceAccess access, Guid id, CancellationToken ct)
    {
        var identity = await session.ValidateSessionAsync(false, ct);
        if (identity.Status is { } status) return new(status, AttendanceReadBasis.None, null);
        var row = await db.Set<EvidenceObject>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.State == EvidenceState.Published, ct);
        if (row is null || !await db.Set<EvidenceBinding>().AnyAsync(x => x.EvidenceId == id, ct)) return new(404, AttendanceReadBasis.None, null);
        var decision = await access.ReadAsync(new(row.MembershipId, row.OwnerId, row.WorkspaceId, row.DepartmentId, row.SnapshotAtUtc), ct);
        return decision.Status is null && decision.CanReadPhoto
            ? new(null, decision.Basis, row.WorkspaceId)
            : new(decision.Status ?? 404, AttendanceReadBasis.None, null);
    }
}
