using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Access;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Attendance.Recording;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.Scopes;

namespace TPR10.Api.Attendance.Corrections;

public sealed class CorrectionApproval(Tpr10DbContext db, ScopeAccess access, IAttendanceRouteResolver resolver,
    IAuditEventWriter audit, TimeProvider clock)
{
    public async Task<CorrectionResult<CorrectionView>> DecideAsync(Guid id, CorrectionCommand command, string decision, CancellationToken ct)
    {
        try
        {
            await using var tx = await ScopeOperation.BeginAsync(db, ct);
            var identity = await access.ValidateSessionAsync(true, ct);
            if (identity.Status is { } denied) return Failure(denied);
            var actor = identity.ActorId;
            if (id == Guid.Empty || command is null || command.CommandId == Guid.Empty || command.ExpectedVersion < 1
                || command.Revision < 1 || decision is not ("supervisor-approve" or "hr-approve" or "return")
                || (decision == "return" && command.Reason?.Trim().Length is not (>= 1 and <= 2000))) return Failure(400);
            var row = await db.Set<CorrectionRequest>().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row is null) return Failure(404);
            await AttendanceEmployeeLock.AcquireAsync(db, row.EmployeeId, ct);
            if (actor == row.EmployeeId) return Failure(403);
            var route = await db.Set<CorrectionRouteSnapshot>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.RequestId == id && x.Revision == row.CurrentRevision, ct);
            if (route is null) return Failure(409);
            var current = await resolver.ResolveAsync(new(row.MembershipId, row.EmployeeId, row.WorkspaceId,
                row.DepartmentId, row.SnapshotAtUtc), ct);
            if (current.Status is { } invalid) return Failure(invalid);
            if (current.SupervisorId != route.SupervisorId) return Failure(409);
            var isHead = actor == route.SupervisorId;
            var isHr = !isHead && route.HrCandidateIds.Contains(actor) && current.HrCandidateIds.Contains(actor);
            if (!(decision == "supervisor-approve" ? isHead : decision == "hr-approve" ? isHr : isHead || isHr)) return Failure(403);

            // Current authorization precedes receipt replay; immutable snapshots are not grants.
            var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { id, command, decision })));
            var receipt = await db.Set<CorrectionCommandReceipt>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.ActorId == actor && x.CommandId == command.CommandId, ct);
            if (receipt is not null)
                return receipt.RequestId == id && receipt.Action == decision && receipt.PayloadSha256 == hash
                    ? new(JsonSerializer.Deserialize<CorrectionView>(receipt.ResultJson), receipt.Status, null) : Failure(409);
            if (row.Version != command.ExpectedVersion || row.CurrentRevision != command.Revision) return Failure(409);
            if (row.State is not (CorrectionState.PendingSupervisor or CorrectionState.PendingHr)) return Failure(409);
            var headStep = row.State == CorrectionState.PendingSupervisor;
            if (headStep ? !isHead || decision == "hr-approve" : !isHr || decision == "supervisor-approve") return Failure(403);
            if (!headStep && !await db.Set<CorrectionApprovalAction>().AnyAsync(x => x.RequestId == id
                && x.Revision == row.CurrentRevision && x.Step == "Supervisor" && x.Outcome == "Approved"
                && x.ActorId == route.SupervisorId && x.ActorId != actor, ct)) return Failure(409);
            var revision = await db.Set<CorrectionRevision>().AsNoTracking().SingleAsync(x => x.RequestId == id && x.Revision == row.CurrentRevision, ct);
            var now = DirectoryQueries.Now(clock);
            db.Add(new CorrectionApprovalAction
            {
                RequestId = id,
                Revision = row.CurrentRevision,
                Step = headStep ? "Supervisor" : "Hr",
                ActorId = actor,
                Outcome = decision == "return" ? "Returned" : "Approved",
                Reason = decision == "return" ? command.Reason!.Trim() : null,
                OccurredAtUtc = now
            });
            // Persist the decision before the deadline guard checks its provenance, still in the same transaction.
            await db.SaveChangesAsync(ct);
            row.State = decision == "return" ? CorrectionState.Returned : headStep ? CorrectionState.PendingHr : CorrectionState.Approved;
            row.Version++;
            if (decision == "return") row.DeadlineUtc = CorrectionDeadline.Returned(now);
            if (row.State == CorrectionState.Approved
                && await new CorrectionAdjustmentWriter(db, clock).ApplyAsync(id, row.CurrentRevision, ct) is { } conflict) return Failure(conflict);
            var view = new CorrectionView(id, row.EmployeeId, row.OriginalWorkDate, row.Version, row.State, row.DeadlineUtc,
                row.CurrentRevision, Proposal(revision), revision.BaselineDayVersion, [], isHead ? "Supervisor" : "Hr");
            db.Add(new CorrectionCommandReceipt
            {
                ActorId = actor,
                CommandId = command.CommandId,
                RequestId = id,
                Action = decision,
                PayloadSha256 = hash,
                Status = 200,
                ResultJson = JsonSerializer.Serialize(view),
                CreatedAtUtc = now
            });
            await audit.WriteAsync(new SecurityAuditRequest(actor, null, row.WorkspaceId, null, null,
                "attendance.correction." + decision, "correction-request", id, "success",
                new Dictionary<string, string> { ["evidence-reference"] = $"revision:{row.CurrentRevision};command:{command.CommandId}" }), ct);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return new(view, 200, null);
        }
        catch (DbUpdateConcurrencyException) { return Failure(409); }
        catch (Exception error) when (ScopeOperation.IsDatabaseFault(error)) { return Failure(503); }
        finally { db.ChangeTracker.Clear(); }
    }

    internal static CorrectionProposal Proposal(CorrectionRevision r) => new(r.WorkDate, r.InUtc, r.OutUtc,
        new(r.InWorkspaceId, r.InProjectId, r.InSiteId),
        r.OutSiteId is { } site ? new(r.OutWorkspaceId!.Value, r.OutProjectId!.Value, site) : null, r.Reason);
    private static CorrectionResult<CorrectionView> Failure(int status) => new(null, status, ScopeOperation.ProblemType(status));
}
