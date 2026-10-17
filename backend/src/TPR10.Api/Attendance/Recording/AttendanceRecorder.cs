using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Access;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Attendance.Storage;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.Scopes;

namespace TPR10.Api.Attendance.Recording;

public sealed class AttendanceRecorder(Tpr10DbContext db, CaptureChallengeService challenges, CaptureOperationStore operations,
    IAttendanceAccess access, StorageRegistry registry, EvidenceWriter evidence, IAuditEventWriter audit, TimeProvider clock)
{
    private static readonly JsonSerializerOptions ReceiptJson = new(JsonSerializerDefaults.Web);

    public async Task<RecordingResult<CaptureReceipt>> RecordAsync(EvidenceAction action, CaptureMetadata metadata, Stream photo, CancellationToken ct)
    {
        try
        {
            if (!CaptureChallengeService.ValidAction(action) || metadata is null || !CaptureValidation.ExactSite(metadata.Site)) return Failure(400);
            await using (var authorize = await ScopeOperation.BeginAsync(db, ct))
            {
                var identity = await challenges.AuthorizeAsync(metadata.Site, ct);
                if (identity.Status is { } denied) return Failure(denied);
            }
            // Bound and hash the actual request bytes without holding any database lock.
            var bytes = await ReadInputAsync(photo, ct);
            var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            AttendanceOperation operation;
            await using (var claim = await ScopeOperation.BeginAsync(db, ct))
            {
                var identity = await challenges.AuthorizeAsync(metadata.Site, ct);
                if (identity.Status is { } denied) return Failure(denied);
                var existing = await db.Set<AttendanceOperation>().AsNoTracking().SingleOrDefaultAsync(x =>
                    x.ActorId == identity.Actor && x.Action == action && x.IdempotencyKey == metadata.IdempotencyKey, ct);
                EmploymentSnapshot subject;
                Guid dayId;
                if (existing is not null)
                {
                    // Replay must precede pairing: a committed check-in has already opened its day.
                    subject = Subject(existing); dayId = existing.TargetDayId;
                }
                else
                {
                    var now = DirectoryQueries.Now(clock);
                    if (CaptureValidation.Validate(metadata, now) is { } invalid) return Failure(400, invalid);
                    var employment = await access.CurrentEmploymentAsync(identity.Actor, now, ct);
                    if (employment is null) return Failure(404);
                    subject = employment;
                    var pair = await AttendancePairing.ValidateAsync(db, identity.Actor, action, now, metadata, null, ct);
                    if (pair.Status != 200) return Failure(pair.Status, pair.Code);
                    dayId = pair.Value?.Id ?? Guid.NewGuid();
                }
                var result = await operations.ClaimAsync(action, metadata, digest, subject, dayId, ct);
                if (result.Status == 200)
                    return new(JsonSerializer.Deserialize<CaptureReceipt>(result.Value!.ResultJson!, ReceiptJson), 200, null, null);
                if (result.Status != 201) return new(null, result.Status, result.Code, result.RetryAfterSeconds);
                operation = result.Value!;
                await db.SaveChangesAsync(ct); await claim.CommitAsync(ct);
            }
            db.ChangeTracker.Clear();
            var pinnedSubject = Subject(operation);
            var stamp = new StampRequest(operation.OccurredAtUtc, action);
            var reservation = await registry.PinAsync(operation.Id, pinnedSubject, stamp, ct);
            using var input = new MemoryStream(bytes, writable: false);
            var prepared = await evidence.PrepareAsync(reservation, input, stamp, ct);
            db.ChangeTracker.Clear();

            await using var commit = await ScopeOperation.BeginAsync(db, ct);
            var valid = await operations.ValidateForCommitAsync(operation.Id, operation.FencingVersion, ct);
            if (valid.Status != 200) return Failure(valid.Status, valid.Code);
            operation = valid.Value!;
            if (!await AttendanceAccess.ValidSnapshotAsync(db, pinnedSubject, DirectoryQueries.Now(clock), ct)) return Failure(409);
            var finalPair = await AttendancePairing.ValidateAsync(db, operation.ActorId, action, operation.OccurredAtUtc, metadata, operation.TargetDayId, ct);
            if (finalPair.Status != 200) return Failure(finalPair.Status, finalPair.Code);
            var day = finalPair.Value;
            if (action == EvidenceAction.CheckIn)
            {
                day = new AttendanceDay
                {
                    Id = operation.TargetDayId,
                    EmployeeId = operation.ActorId,
                    WorkDate = AttendancePairing.WorkDate(operation.OccurredAtUtc),
                    MembershipId = operation.MembershipId,
                    WorkspaceId = operation.MembershipWorkspaceId,
                    DepartmentId = operation.DepartmentId,
                    SnapshotAtUtc = operation.OccurredAtUtc,
                    EffectiveInUtc = operation.OccurredAtUtc
                };
                db.Add(day);
            }
            else { day!.EffectiveOutUtc = operation.OccurredAtUtc; day.Version = checked(day.Version + 1); }
            var record = new AttendanceEvent
            {
                Id = Guid.NewGuid(),
                DayId = day!.Id,
                ActorId = operation.ActorId,
                Action = action,
                OccurredAtUtc = operation.OccurredAtUtc,
                DeviceCapturedAtUtc = metadata.DeviceCapturedAtUtc,
                WorkspaceId = operation.WorkspaceId,
                ProjectId = operation.ProjectId,
                SiteId = operation.SiteId,
                Latitude = metadata.Gps.Latitude,
                Longitude = metadata.Gps.Longitude,
                AccuracyMeters = metadata.Gps.AccuracyMeters,
                GpsSampledAtUtc = metadata.Gps.SampledAtUtc,
                EvidenceId = prepared.EvidenceId
            };
            db.Add(record);
            await evidence.StagePublicationAsync(new(prepared.EvidenceId, operation.Id, record.Id, pinnedSubject, stamp), ct);
            var receipt = new CaptureReceipt(operation.Id, day.Id, record.Id, day.WorkDate, operation.OccurredAtUtc, prepared.EvidenceId, day.Version);
            db.Attach(operation);
            operation.State = AttendanceOperationState.Committed; operation.ResultEventId = record.Id;
            operation.ResultJson = JsonSerializer.Serialize(receipt, ReceiptJson); operation.Version = checked(operation.Version + 1);
            var challenge = await db.Set<CaptureChallenge>().SingleAsync(x => x.Id == operation.ChallengeId, ct);
            challenge.ConsumedOperationId = operation.Id;
            await audit.WriteAsync(new SecurityAuditRequest(operation.ActorId, null, operation.WorkspaceId, operation.ProjectId, operation.SiteId,
                "attendance.record.commit", "attendance-event", record.Id, "success", new Dictionary<string, string>()), ct);
            // Publication/audit persistence can consume the remaining lease after validation.
            // Keep the save and COMMIT bounded, and roll back if persistence crossed expiry.
            var commitNow = DirectoryQueries.Now(clock);
            if (commitNow >= challenge.ExpiresAtUtc) return Failure(400, "challenge-expired");
            if (commitNow >= operation.LeaseUntilUtc) return Failure(409);
            var deadline = challenge.ExpiresAtUtc < operation.LeaseUntilUtc ? challenge.ExpiresAtUtc : operation.LeaseUntilUtc;
            using var deadlineCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadlineCancellation.CancelAfter(deadline - commitNow);
            await db.SaveChangesAsync(deadlineCancellation.Token);
            commitNow = DirectoryQueries.Now(clock);
            if (commitNow >= challenge.ExpiresAtUtc) return Failure(400, "challenge-expired");
            if (commitNow >= operation.LeaseUntilUtc) return Failure(409);
            await commit.CommitAsync(deadlineCancellation.Token);
            return new(receipt, 201, null, null);
        }
        catch (StorageOperationException error) { return Failure(error.Status); }
        catch (ImageProcessingException error)
        {
            return new(null, error.Code == "image-too-large" ? 413 : error.Code == "image-invalid" ? 415 : error.StatusCode, error.Code, error.RetryAfterSeconds);
        }
        catch (Exception error) when (ScopeOperation.IsDatabaseFault(error) || error is IOException or UnauthorizedAccessException)
        { return Failure(503); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Failure(503); }
        finally { db.ChangeTracker.Clear(); }
    }

    private static EmploymentSnapshot Subject(AttendanceOperation operation) => new(operation.MembershipId, operation.ActorId,
        operation.MembershipWorkspaceId, operation.DepartmentId, operation.OccurredAtUtc);
    private static RecordingResult<CaptureReceipt> Failure(int status, string? code = null) => new(null, status, code ?? ScopeOperation.ProblemType(status), null);
    private static async Task<byte[]> ReadInputAsync(Stream photo, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var count = await photo.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, ImageLimits.MaxBytes + 1 - (int)output.Length)), ct);
            if (count == 0) return output.ToArray();
            output.Write(buffer, 0, count);
            if (output.Length > ImageLimits.MaxBytes) throw new ImageProcessingException("image-too-large");
        }
    }
}
