using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Access;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Scopes;

namespace TPR10.Api.Attendance.Recording;

public sealed class CaptureChallengeService(Tpr10DbContext db, ScopeAccess access, RequestSession current,
    IAttendanceAccess attendance, IAuditEventWriter audit, TimeProvider clock)
{
    public async Task<RecordingResult<CaptureChallengeView>> IssueAsync(CaptureChallengeRequest request, CancellationToken ct)
    {
        if (request is null || !CaptureValidation.ExactSite(request.Site) || request.IdempotencyKey == Guid.Empty
            || !ValidAction(request.Action)) return Failure(400);
        try
        {
            await using var tx = await ScopeOperation.BeginAsync(db, ct);
            var identity = await AuthorizeAsync(request.Site, ct);
            if (identity.Status is { } status) return Failure(status);
            var now = DirectoryQueries.Now(clock);
            var existing = await db.Set<CaptureChallenge>().AsNoTracking().SingleOrDefaultAsync(x => x.ActorId == identity.Actor
                && x.Action == request.Action && x.IdempotencyKey == request.IdempotencyKey, ct);
            if (existing is not null)
            {
                if (existing.SessionId != current.Entity!.Id || new ScopeKey(existing.WorkspaceId, existing.ProjectId, existing.SiteId) != request.Site)
                    return Failure(409);
                if (now >= existing.ExpiresAtUtc) return Failure(400, "challenge-expired");
                if (existing.ConsumedOperationId is not null) return Failure(409);
                return new(new(existing.Id, now, existing.ExpiresAtUtc), 200, null, null);
            }
            var challenge = new CaptureChallenge
            {
                Id = Guid.NewGuid(),
                ActorId = identity.Actor,
                SessionId = current.Entity!.Id,
                Action = request.Action,
                WorkspaceId = request.Site.WorkspaceId,
                ProjectId = request.Site.ProjectId!.Value,
                SiteId = request.Site.SiteId!.Value,
                IdempotencyKey = request.IdempotencyKey,
                IssuedAtUtc = now,
                ExpiresAtUtc = now.AddSeconds(120)
            };
            db.Add(challenge);
            await audit.WriteAsync(new SecurityAuditRequest(identity.Actor, null, challenge.WorkspaceId, challenge.ProjectId, challenge.SiteId,
                "attendance.capture.challenge", "capture-challenge", challenge.Id, "success", new Dictionary<string, string>
                { ["capability"] = "attendance:record" }), ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new(new(challenge.Id, now, challenge.ExpiresAtUtc), 201, null, null);
        }
        catch (Exception error) when (ScopeOperation.IsDatabaseFault(error))
        { db.ChangeTracker.Clear(); return Failure(503); }
    }

    // Caller holds the identity transaction; re-read permissions only after acquiring both locks.
    internal async Task<(Guid Actor, int? Status)> AuthorizeAsync(ScopeKey site, CancellationToken ct)
    {
        var identity = await access.ValidateSessionAsync(false, ct);
        if (identity.Status is { } status) return (Guid.Empty, status);
        await AttendanceEmployeeLock.AcquireAsync(db, identity.ActorId, ct);
        var decision = await access.ResolveAsync(site, "attendance:record", false, ct);
        if (decision.Status is { } denied) return (Guid.Empty, denied);
        var employment = await attendance.CurrentEmploymentAsync(identity.ActorId, DirectoryQueries.Now(clock), ct);
        return employment is null ? (Guid.Empty, 403) : (identity.ActorId, null);
    }

    internal static bool ValidAction(EvidenceAction action) => action is EvidenceAction.CheckIn or EvidenceAction.CheckOut;
    private static RecordingResult<CaptureChallengeView> Failure(int status, string? code = null) => new(null, status, code ?? ScopeOperation.ProblemType(status), null);
}
