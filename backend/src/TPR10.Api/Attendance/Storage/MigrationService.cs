using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Data;

namespace TPR10.Api.Attendance.Storage;

// Reuse the registry's transaction, live MFA/capability checks and denial audit boundary.
public sealed class MigrationService(StorageRegistry registry)
{
    public Task<IResult> StartAsync(StartMigration request, CancellationToken ct) => registry.StartMigrationAsync(request, ct);
    public Task<IResult> ResumeAsync(Guid id, ResumeMigration request, CancellationToken ct) => registry.ResumeMigrationAsync(id, request, ct);
    public Task<IResult> ListAsync(int offset, int limit, CancellationToken ct) => registry.ListMigrationsAsync(offset, limit, ct);
    public Task<IResult> GetAsync(Guid id, CancellationToken ct) => registry.GetMigrationAsync(id, ct);
}

public sealed partial class StorageRegistry
{
    internal Task<IResult> StartMigrationAsync(StartMigration request, CancellationToken ct) => RunAsync("migration-start", async () =>
    {
        if (request.RequestId == Guid.Empty || request.SourceId == Guid.Empty || request.TargetId == Guid.Empty
            || request.SourceId == request.TargetId || request.ExpectedSourceVersion < 1 || request.ExpectedTargetVersion < 1
            || !DirectoryRules.ValidReason(request.Reason)) return Reject(400);
        var existing = await db.Set<MigrationJob>().SingleOrDefaultAsync(x => x.RequestId == request.RequestId, ct);
        if (existing is not null)
            return existing.RequestedBy == Actor && existing.SourceId == request.SourceId && existing.TargetId == request.TargetId
                && existing.SourceVersion == request.ExpectedSourceVersion && existing.TargetVersion == request.ExpectedTargetVersion && existing.Reason == request.Reason.Trim()
                ? new(Results.Json(await MigrationManifest.ViewAsync(db, existing, ct), statusCode: 202), existing.Id, request.Reason, AuditAction: "migration-replay") : Reject(409);
        var source = await db.Set<StorageLocation>().SingleOrDefaultAsync(x => x.Id == request.SourceId, ct);
        var target = await db.Set<StorageLocation>().SingleOrDefaultAsync(x => x.Id == request.TargetId, ct);
        if (source is null || target is null) return Reject(404);
        if (source.Version != request.ExpectedSourceVersion || target.Version != request.ExpectedTargetVersion || source.Version == long.MaxValue
            || await db.Set<StorageWriteTarget>().AnyAsync(x => x.StorageId == source.Id, ct)
            || await db.Set<MigrationJob>().AnyAsync(x => x.Status != "Completed" && (x.SourceId == source.Id || x.TargetId == source.Id || x.SourceId == target.Id), ct)) return Reject(409);
        if (!runtime.Matches(source) || !runtime.Matches(target)) return Reject(503);
        if (runtime.Health(source).Status is not ("ready" or "warning") || !runtime.Ready(target)) return Reject(409);
        source.AcceptWrites = false; source.Version++;
        var job = new MigrationJob
        {
            Id = Guid.NewGuid(),
            RequestId = request.RequestId,
            SourceId = source.Id,
            TargetId = target.Id,
            SourceVersion = request.ExpectedSourceVersion,
            TargetVersion = request.ExpectedTargetVersion,
            RequestedBy = Actor,
            Reason = request.Reason.Trim(),
            CreatedAtUtc = DirectoryQueries.Now(clock)
        };
        db.Add(job);
        // Persist the FK principal, then seed the archive in PostgreSQL without materializing it in the process.
        // The enclosing transaction still rolls back seal/job/manifest together if the audit fails.
        await db.SaveChangesAsync(ct);
        await MigrationManifest.SeedAsync(db, job, DirectoryQueries.Now(clock), ct);
        return new(Results.Json(await MigrationManifest.ViewAsync(db, job, ct), statusCode: 202), job.Id, request.Reason);
    }, ct);

    internal Task<IResult> ResumeMigrationAsync(Guid id, ResumeMigration request, CancellationToken ct) => RunAsync("migration-resume", async () =>
    {
        if (id == Guid.Empty || request.ExpectedVersion < 1 || !DirectoryRules.ValidReason(request.Reason)) return Reject(400);
        var job = await db.Set<MigrationJob>().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (job is null) return Reject(404);
        if (job.Version != request.ExpectedVersion || job.Status != "Blocked") return Reject(409);
        var source = await db.Set<StorageLocation>().SingleAsync(x => x.Id == job.SourceId, ct);
        var target = await db.Set<StorageLocation>().SingleAsync(x => x.Id == job.TargetId, ct);
        if (source.AcceptWrites || await db.Set<StorageWriteTarget>().AnyAsync(x => x.StorageId == source.Id, ct)) return Reject(409);
        if (!runtime.Matches(source) || !runtime.Matches(target)) return Reject(503);
        if (runtime.Health(source).Status is not ("ready" or "warning") || !runtime.Ready(target)) return Reject(409);
        if (!await MigrationAuthority.AllowsAsync(db, job.RequestedBy, ct)) return Reject(403);
        // Reset the whole unfinished manifest atomically without tracking an archive in memory.
        await db.Set<MigrationItem>().Where(x => x.JobId == id && x.Status != "Completed")
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.Status, "Pending").SetProperty(x => x.Attempts, 0)
                .SetProperty(x => x.ErrorCode, (string?)null).SetProperty(x => x.NextAttemptAtUtc, (DateTimeOffset?)null)
                .SetProperty(x => x.LeaseOwner, (Guid?)null).SetProperty(x => x.LeaseUntilUtc, (DateTimeOffset?)null)
                .SetProperty(x => x.FencingVersion, x => x.FencingVersion + 1)
                .SetProperty(x => x.Version, x => x.Version + 1), ct);
        job.Status = "Pending"; job.Version++;
        await MigrationManifest.ReconcileAsync(db, job, DirectoryQueries.Now(clock), ct);
        await db.SaveChangesAsync(ct);
        return new(Results.Json(await MigrationManifest.ViewAsync(db, job, ct), statusCode: 202), id, request.Reason);
    }, ct);

    internal Task<IResult> ListMigrationsAsync(int offset, int limit, CancellationToken ct) => RunAsync("migration-list", async () =>
    {
        if (!ValidPage(offset, limit)) return Reject(400);
        var jobs = await db.Set<MigrationJob>().AsNoTracking().OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).Skip(offset).Take(limit + 1).ToArrayAsync(ct);
        var result = new List<MigrationView>();
        foreach (var job in jobs.Take(limit)) result.Add(await MigrationManifest.ViewAsync(db, job, ct));
        return new(Results.Ok(new Page<MigrationView>(result.ToArray(), offset, jobs.Length > limit)));
    }, ct);

    internal Task<IResult> GetMigrationAsync(Guid id, CancellationToken ct) => RunAsync("migration-get", async () =>
    {
        var job = await db.Set<MigrationJob>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return job is null ? Reject(404) : new(Results.Ok(await MigrationManifest.ViewAsync(db, job, ct)));
    }, ct);
}

internal static class MigrationManifest
{
    internal static Task<int> SeedAsync(Tpr10DbContext db, MigrationJob job, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO evidence_migration_items
                (id, job_id, evidence_id, variant, expected_sha256, expected_length, source_id, target_id,
                 status, attempts, fencing_version, version, created_at_utc)
            SELECT gen_random_uuid(), {job.Id}, copy.evidence_id, copy.variant, copy.sha256, copy.length,
                   {job.SourceId}, {job.TargetId}, 'Pending', 0, 1, 1, {now}
            FROM evidence_locations copy JOIN evidence_objects evidence ON evidence.id=copy.evidence_id
            WHERE copy.storage_id={job.SourceId} AND copy.state IN ('Active','Verified')
                  AND evidence.state IN ('Prepared','Published')
            ON CONFLICT (job_id,evidence_id,variant) DO NOTHING
            """, ct);

    internal static async Task ReconcileAsync(Tpr10DbContext db, MigrationJob job, DateTimeOffset now, CancellationToken ct)
    {
        var copies = await (from copy in db.Set<EvidenceLocation>()
                            join evidence in db.Set<EvidenceObject>() on copy.EvidenceId equals evidence.Id
                            where copy.StorageId == job.SourceId && (copy.State == CopyState.Active || copy.State == CopyState.Verified)
                                && (evidence.State == EvidenceState.Prepared || evidence.State == EvidenceState.Published)
                                && !db.Set<MigrationItem>().Any(x => x.JobId == job.Id && x.EvidenceId == copy.EvidenceId && x.Variant == copy.Variant)
                            select copy).AsNoTracking().OrderBy(x => x.Id).Take(100).ToArrayAsync(ct);
        foreach (var copy in copies) db.Add(new MigrationItem
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            EvidenceId = copy.EvidenceId,
            Variant = copy.Variant,
            ExpectedSha256 = copy.Sha256,
            ExpectedLength = copy.Length,
            SourceId = job.SourceId,
            TargetId = job.TargetId,
            CreatedAtUtc = now
        });
    }

    internal static async Task<MigrationView> ViewAsync(Tpr10DbContext db, MigrationJob job, CancellationToken ct)
    {
        var counts = await db.Set<MigrationItem>().Where(x => x.JobId == job.Id).GroupBy(x => x.Status)
            .Select(x => new { Status = x.Key, Count = x.Count() }).ToArrayAsync(ct);
        return new(job.Id, job.Status, job.Version, counts.Sum(x => x.Count), counts.Where(x => x.Status is "Verified" or "Completed").Sum(x => x.Count),
            counts.Where(x => x.Status == "Blocked").Sum(x => x.Count));
    }
}
