using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Access;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Data;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Scopes;

namespace TPR10.Api.Attendance.Recording;

public sealed class CaptureOperationStore(Tpr10DbContext db, ScopeAccess access, RequestSession current,
    CaptureChallengeService challenges, TimeProvider clock)
{
    private static readonly JsonSerializerOptions ReceiptJson = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static string ComputeDigest(CaptureMetadata metadata, string imageSha256)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            metadata.ChallengeId,
            metadata.IdempotencyKey,
            metadata.Site.WorkspaceId,
            metadata.Site.ProjectId,
            metadata.Site.SiteId,
            DeviceCapturedAtUtc = metadata.DeviceCapturedAtUtc.UtcTicks,
            Latitude = metadata.Gps.Latitude == 0 ? 0 : metadata.Gps.Latitude,
            Longitude = metadata.Gps.Longitude == 0 ? 0 : metadata.Gps.Longitude,
            metadata.Gps.AccuracyMeters,
            SampledAtUtc = metadata.Gps.SampledAtUtc.UtcTicks,
            metadata.ConfirmContinuousWork,
            metadata.ExpectedDayVersion,
            ImageSha256 = imageSha256.ToLowerInvariant()
        });
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    // Caller owns SaveChanges/commit. This stages only the operation, never attendance/evidence or a receipt.
    public async Task<RecordingResult<AttendanceOperation>> ClaimAsync(EvidenceAction action, CaptureMetadata metadata,
        string imageSha256, EmploymentSnapshot subject, Guid dayId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Capture claim requires a caller-owned identity transaction.");
        if (!CaptureChallengeService.ValidAction(action) || metadata is null || !CaptureValidation.ExactSite(metadata.Site)
            || imageSha256 is null || imageSha256.Length != 64 || imageSha256.Any(c => !Uri.IsHexDigit(c))) return Failure(400);
        var identity = await challenges.AuthorizeAsync(metadata.Site, ct);
        if (identity.Status is { } status) return Failure(status);
        var now = DirectoryQueries.Now(clock);
        var operation = await db.Set<AttendanceOperation>().SingleOrDefaultAsync(x => x.ActorId == identity.Actor
            && x.Action == action && x.IdempotencyKey == metadata.IdempotencyKey, ct);
        // EF identity resolution may return a previous transaction's tracked entity.
        if (operation is not null) await db.Entry(operation).ReloadAsync(ct);
        var invalid = CaptureValidation.Validate(metadata, operation?.OccurredAtUtc ?? subject.OccurredAtUtc);
        if (invalid is not null) return Failure(400, invalid);
        var digest = ComputeDigest(metadata, imageSha256);
        if (operation is not null && (operation.SessionId != current.Entity!.Id || operation.PayloadSha256 != digest)) return Failure(409);
        var challenge = await db.Set<CaptureChallenge>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == metadata.ChallengeId
            && x.ActorId == identity.Actor && x.SessionId == current.Entity!.Id && x.Action == action
            && x.IdempotencyKey == metadata.IdempotencyKey && x.WorkspaceId == metadata.Site.WorkspaceId
            && x.ProjectId == metadata.Site.ProjectId && x.SiteId == metadata.Site.SiteId, ct);
        if (challenge is null) return Failure(400);
        if (operation?.State == AttendanceOperationState.Committed)
            return ReadReceipt(operation) is null ? Failure(503) : new(operation, 200, null, null);
        if (now >= challenge.ExpiresAtUtc || operation?.State == AttendanceOperationState.Expired) return Failure(400, "challenge-expired");
        if (challenge.ConsumedOperationId is not null || operation?.State == AttendanceOperationState.Failed) return Failure(409);
        if (operation is not null)
        {
            if (operation.LeaseUntilUtc > now) return new(null, 409, "operation-processing", 2);
            operation.LeaseUntilUtc = now.AddSeconds(60);
            operation.FencingVersion = checked(operation.FencingVersion + 1);
            operation.Version = checked(operation.Version + 1);
            return new(operation, 201, null, null);
        }
        if (subject.EmployeeId != identity.Actor || dayId == Guid.Empty || subject.OccurredAtUtc < challenge.IssuedAtUtc
            || subject.OccurredAtUtc > now || metadata.DeviceCapturedAtUtc < challenge.IssuedAtUtc
            || (action == EvidenceAction.CheckIn ? metadata.ExpectedDayVersion is not null : metadata.ExpectedDayVersion is null)
            || !await AttendanceAccess.ValidSnapshotAsync(db, subject, now, ct)) return Failure(400);
        operation = new AttendanceOperation
        {
            Id = Guid.NewGuid(),
            ChallengeId = challenge.Id,
            ActorId = identity.Actor,
            SessionId = current.Entity!.Id,
            Action = action,
            WorkspaceId = challenge.WorkspaceId,
            ProjectId = challenge.ProjectId,
            SiteId = challenge.SiteId,
            IdempotencyKey = challenge.IdempotencyKey,
            PayloadSha256 = digest,
            OccurredAtUtc = subject.OccurredAtUtc,
            MembershipId = subject.MembershipId,
            MembershipWorkspaceId = subject.WorkspaceId,
            DepartmentId = subject.DepartmentId,
            TargetDayId = dayId,
            ExpectedDayVersion = metadata.ExpectedDayVersion,
            LeaseUntilUtc = now.AddSeconds(60)
        };
        db.Add(operation);
        return new(operation, 201, null, null);
    }

    public async Task<RecordingResult<AttendanceOperation>> ValidateForCommitAsync(Guid operationId, long fence, CancellationToken ct)
    {
        var identity = await access.ValidateSessionAsync(false, ct);
        if (identity.Status is { } status) return Failure(status);
        await AttendanceEmployeeLock.AcquireAsync(db, identity.ActorId, ct);
        // Reload so a context retained across preparation cannot use a stale fencing token.
        var operation = await db.Set<AttendanceOperation>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == operationId
            && x.ActorId == identity.ActorId && x.SessionId == current.Entity!.Id, ct);
        if (operation is null) return Failure(404);
        var allowed = await challenges.AuthorizeAsync(new(operation.WorkspaceId, operation.ProjectId, operation.SiteId), ct);
        if (allowed.Status is { } denied) return Failure(denied);
        var now = DirectoryQueries.Now(clock);
        var challenge = await db.Set<CaptureChallenge>().AsNoTracking().SingleAsync(x => x.Id == operation.ChallengeId, ct);
        if (now >= challenge.ExpiresAtUtc) return Failure(400, "challenge-expired");
        if (operation.State != AttendanceOperationState.Processing || challenge.ConsumedOperationId is not null
            || operation.FencingVersion != fence || operation.LeaseUntilUtc <= now) return Failure(409);
        return new(operation, 200, null, null);
    }

    public async Task<RecordingResult<CaptureOperationView>> GetAsync(Guid key, EvidenceAction action, CancellationToken ct)
    {
        try
        {
            await using var tx = await ScopeOperation.BeginAsync(db, ct);
            var identity = await access.ValidateSessionAsync(false, ct);
            if (identity.Status is { } status) return ViewFailure(status);
            if (key == Guid.Empty || !CaptureChallengeService.ValidAction(action)) return ViewFailure(400);
            var operation = await db.Set<AttendanceOperation>().AsNoTracking().SingleOrDefaultAsync(x => x.ActorId == identity.ActorId
                && x.SessionId == current.Entity!.Id && x.Action == action && x.IdempotencyKey == key, ct);
            if (operation is null) return ViewFailure(404);
            if (operation.State == AttendanceOperationState.Committed)
            {
                var receipt = ReadReceipt(operation);
                return receipt is null ? ViewFailure(503) : new(new("Committed", receipt), 200, null, null);
            }
            var state = operation.State;
            if (state == AttendanceOperationState.Processing && await db.Set<CaptureChallenge>()
                .AnyAsync(x => x.Id == operation.ChallengeId && x.ExpiresAtUtc <= DirectoryQueries.Now(clock), ct)) state = AttendanceOperationState.Expired;
            return new(new(state.ToString(), null), 200, null, null);
        }
        catch (Exception error) when (ScopeOperation.IsDatabaseFault(error)) { return ViewFailure(503); }
    }

    private static CaptureReceipt? ReadReceipt(AttendanceOperation operation)
    {
        try
        {
            var receipt = JsonSerializer.Deserialize<CaptureReceipt>(operation.ResultJson ?? "null", ReceiptJson);
            return receipt is not null && receipt.OperationId == operation.Id && receipt.DayId == operation.TargetDayId
                && receipt.EventId == operation.ResultEventId && receipt.OccurredAtUtc == operation.OccurredAtUtc
                && receipt.EvidenceId != Guid.Empty && receipt.DayVersion > 0 && receipt.WorkDate != default ? receipt : null;
        }
        catch (JsonException) { return null; }
    }
    private static RecordingResult<AttendanceOperation> Failure(int status, string? code = null) => new(null, status, code ?? ScopeOperation.ProblemType(status), null);
    private static RecordingResult<CaptureOperationView> ViewFailure(int status) => new(null, status, ScopeOperation.ProblemType(status), null);
}
