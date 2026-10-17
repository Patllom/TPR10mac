using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Access;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Attendance.Recording;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.Identity.Authorization;
using TPR10.Api.Scopes;

namespace TPR10.Api.Attendance.Corrections;

public sealed class CorrectionSubmission(Tpr10DbContext db, ScopeAccess access, IAttendanceAccess attendance,
    PermissionMutationGuard guard, IAttendanceRouteResolver resolver, IAuditEventWriter audit, TimeProvider clock)
{
    public Task<CorrectionResult<CorrectionView>> SubmitAsync(Guid id, CorrectionCommand command, CancellationToken ct) => WriteAsync(id, command, false, ct);
    public Task<CorrectionResult<CorrectionView>> RerouteAsync(Guid id, CorrectionCommand command, CancellationToken ct) => WriteAsync(id, command, true, ct);

    private async Task<CorrectionResult<CorrectionView>> WriteAsync(Guid id, CorrectionCommand command, bool reroute, CancellationToken ct)
    {
        try
        {
            await using var tx = await ScopeOperation.BeginAsync(db, ct);
            var identity = await access.ValidateSessionAsync(reroute, ct);
            if (identity.Status is { } denied) return Failure(denied);
            var actor = identity.ActorId;
            if (id == Guid.Empty || command is null || command.CommandId == Guid.Empty
                || command.ExpectedVersion < 1 || command.Revision < 1
                || (reroute && command.Reason?.Trim().Length is not (>= 1 and <= 2000))) return Failure(400);
            var row = await db.Set<CorrectionRequest>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row is null) return Failure(404);
            await AttendanceEmployeeLock.AcquireAsync(db, row.EmployeeId, ct);
            var routing = new CorrectionRouting(db, guard, resolver, clock);
            if (reroute)
            {
                if (!await routing.CanRerouteAsync(actor, row, ct)) return Failure(403);
            }
            else
            {
                if (row.EmployeeId != actor) return Failure(404);
                if (!(await attendance.DescribeAsync(ct)).CanRecord) return Failure(403);
            }
            var action = reroute ? "reroute" : "submit";
            var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { id, command, action })));
            var receipt = await db.Set<CorrectionCommandReceipt>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.ActorId == actor && x.CommandId == command.CommandId, ct);
            if (receipt is not null)
                return receipt.RequestId == id && receipt.Action == action && receipt.PayloadSha256 == hash
                    ? new(JsonSerializer.Deserialize<CorrectionView>(receipt.ResultJson), receipt.Status, null) : Failure(409);
            if (row.Version != command.ExpectedVersion || row.CurrentRevision != command.Revision) return Failure(409);
            if (reroute ? row.State is not (CorrectionState.PendingSupervisor or CorrectionState.PendingHr)
                : row.State is not (CorrectionState.Draft or CorrectionState.Returned)) return Failure(409);
            if (!reroute && clock.GetUtcNow() >= row.DeadlineUtc)
            {
                row.State = CorrectionState.Expired; row.Version++;
                await Audit(actor, row, "expired", command.CommandId, ct);
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
                return Failure(400);
            }
            var revision = await db.Set<CorrectionRevision>().AsNoTracking().SingleAsync(x => x.RequestId == id && x.Revision == row.CurrentRevision, ct);
            var route = await routing.ResolveAsync(row, ct);
            if (route.Status is { } invalid) return Failure(invalid);
            if (reroute || await db.Set<CorrectionRouteSnapshot>().AnyAsync(x => x.RequestId == id && x.Revision == row.CurrentRevision, ct))
            {
                // A previously submitted revision and its approvals remain immutable history.
                revision.Revision = row.CurrentRevision + 1; revision.CreatedAtUtc = DirectoryQueries.Now(clock);
                db.Add(revision); await db.SaveChangesAsync(ct);
                row.CurrentRevision = revision.Revision;
            }
            db.Add(new CorrectionRouteSnapshot
            {
                RequestId = id,
                Revision = row.CurrentRevision,
                SupervisorId = route.SupervisorId!.Value,
                HrCandidateIds = route.HrCandidateIds,
                MembershipVersion = route.MembershipVersion,
                ReportingVersion = route.ReportingVersion!.Value,
                CreatedAtUtc = DirectoryQueries.Now(clock)
            });
            row.State = CorrectionState.PendingSupervisor; row.Version++;
            var view = new CorrectionView(row.Id, row.EmployeeId, row.OriginalWorkDate, row.Version, row.State,
                row.DeadlineUtc, row.CurrentRevision, Proposal(revision), revision.BaselineDayVersion,
                reroute ? ["reroute"] : [], reroute ? "Hr" : "Own");
            var resultJson = JsonSerializer.SerializeToNode(view)!;
            // Retain the full reason privately; the audit references this immutable command receipt.
            // Security audit metadata has a smaller limit and must not contain arbitrary free text.
            if (reroute) resultJson["CommandReason"] = command.Reason!.Trim();
            db.Add(new CorrectionCommandReceipt
            {
                ActorId = actor,
                CommandId = command.CommandId,
                RequestId = id,
                Action = action,
                PayloadSha256 = hash,
                Status = 200,
                ResultJson = resultJson.ToJsonString(),
                CreatedAtUtc = DirectoryQueries.Now(clock)
            });
            await Audit(actor, row, action, command.CommandId, ct);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return new(view, 200, null);
        }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return Failure(409); }
        catch (Exception error) when (ScopeOperation.IsDatabaseFault(error)) { db.ChangeTracker.Clear(); return Failure(503); }
    }

    private Task Audit(Guid actor, CorrectionRequest row, string action, Guid commandId, CancellationToken ct) =>
        audit.WriteAsync(new SecurityAuditRequest(actor, null, row.WorkspaceId, null, null,
            "attendance.correction." + action, "correction-request", row.Id, "success",
            new Dictionary<string, string> { ["evidence-reference"] = $"revision:{row.CurrentRevision};command:{commandId}" }), ct);

    private static CorrectionProposal Proposal(CorrectionRevision r) => new(r.WorkDate, r.InUtc, r.OutUtc,
        new(r.InWorkspaceId, r.InProjectId, r.InSiteId),
        r.OutSiteId is { } site ? new(r.OutWorkspaceId!.Value, r.OutProjectId!.Value, site) : null, r.Reason);
    private static CorrectionResult<CorrectionView> Failure(int status) => new(null, status, ScopeOperation.ProblemType(status));
}
