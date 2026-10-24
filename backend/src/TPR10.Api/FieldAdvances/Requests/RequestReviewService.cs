using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using static TPR10.Api.FieldAdvances.Requests.RequestCommandExecutor;

namespace TPR10.Api.FieldAdvances.Requests;

public sealed class RequestReviewService(Tpr10DbContext db, FinanceAccess access, FinancePersonResolver people,
    RequestAccess requestAccess, RequestDraftService drafts, ApprovalRouteResolver routes, IAuditEventWriter audit, TimeProvider clock)
{
    private readonly RequestCommandExecutor executor = new(db, requestAccess, audit, clock);

    public Task<FinanceResult<RequestView>> DecideAsync(Guid actorId, Guid requestId, RequestDecision decision, RequestCommand meta, CancellationToken ct)
        => executor.ExecuteAsync(async () =>
        {
            if (!RequestDraftService.ValidMeta(meta) || decision is null || decision.StepId == Guid.Empty
                || decision.Outcome is not ("Approve" or "Return" or "Reject")
                || decision.Outcome != "Approve" && string.IsNullOrWhiteSpace(meta.Reason)) return Invalid();
            var request = await executor.LockAsync(requestId, ct);
            if (request is null) return Denied();
            var step = await db.Set<AdvanceApprovalStep>().SingleOrDefaultAsync(x => x.Id == decision.StepId && x.RequestId == requestId, ct);
            if (step is null) return Denied();
            var revision = await db.Set<AdvanceRequestRevision>().SingleAsync(x => x.Id == step.RevisionId, ct);
            var person = await AuthorityAsync(actorId, request, revision, step, ct);
            if (person is null) return Denied();
            var action = "request.decide";
            var hash = Hash(new { action, actorId, requestId, decision, meta });
            var replay = await executor.ReplayAsync(actorId, new(request.WorkspaceId, request.ProjectId), action, meta, hash, ct);
            if (replay is not null) return replay;
            if (request.Version != meta.ExpectedVersion || request.Version == long.MaxValue) return Conflict();
            if (!revision.IsCurrent || revision.SubmittedAtUtc is null || !(await requestAccess.CheckAsync(actorId, requestId, "review", ct)).Allowed)
                return Denied();
            var pending = await db.Set<AdvanceApprovalStep>().Where(x => x.RevisionId == revision.Id && x.Outcome == null)
                .OrderBy(x => x.StepOrder).ToArrayAsync(ct);
            if (pending.Length == 0 || pending[0].Id != step.Id) return Conflict();
            var route = await routes.ResolveAsync(requestId, revision.Id, step.LevelCode, ct);
            if (route.PersonId != person || step.AssigneePersonId != person || route.HoldCode is not null) return Denied();
            step.Outcome = decision.Outcome switch { "Approve" => "Approved", "Return" => "Returned", _ => "Rejected" };
            step.DecidedBy = actorId; step.DecidedByPersonId = person; step.DecidedAtUtc = clock.GetUtcNow();
            step.DecisionReason = string.IsNullOrWhiteSpace(meta.Reason) ? "อนุมัติ" : meta.Reason;
            if (step.Outcome == "Approved" && !await db.Set<AdvanceRequestParticipant>().AnyAsync(x => x.RequestId == requestId
                && x.PersonId == person && x.Role == "Approver", ct))
                db.Add(new AdvanceRequestParticipant
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = request.WorkspaceId,
                    ProjectId = request.ProjectId,
                    RequestId = requestId,
                    PersonId = person.Value,
                    Role = "Approver",
                    CreatedBy = actorId,
                    CreatedAtUtc = clock.GetUtcNow()
                });
            // Resolver ต้องเห็นผลที่เพิ่งลง เพื่อบังคับคนละคนในฉบับเดียวกัน
            await db.SaveChangesAsync(ct);
            RouteChoice? nextRoute = null;
            var next = pending.Skip(1).FirstOrDefault();
            if (step.Outcome == "Approved" && next is not null)
            {
                nextRoute = await routes.ResolveAsync(requestId, revision.Id, next.LevelCode, ct);
                Assign(request, next, nextRoute);
            }
            else
            {
                request.State = step.Outcome; request.HoldCode = null;
                foreach (var remaining in pending.Skip(1))
                {
                    remaining.AssigneePersonId = null; remaining.HoldCode = null;
                    remaining.AssignmentReason = "ปิดขั้นค้างเมื่อจบรอบอนุมัติฉบับนี้";
                }
            }
            request.Version++;
            var result = await executor.FinishAsync(actorId, person.Value, request, revision, action, step.Outcome, meta, hash,
                JsonSerializer.Deserialize<RequestContent>(revision.ContentJson)!, await drafts.StoredEstimateAsync(revision, ct), 200,
                async () => await AuthorityAsync(actorId, request, revision, step, ct) == person
                    && SameRoute(route, await routes.RecheckDecisionAsync(requestId, revision.Id, step.LevelCode, step.Id, ct))
                    && (nextRoute is null || SameRoute(nextRoute, await routes.ResolveAsync(requestId, revision.Id, next!.LevelCode, ct))), ct);
            var history = db.ChangeTracker.Entries<AdvanceRequestEvent>().Single(x => x.Entity.CommandId == meta.CommandId).Entity;
            history.StepId = step.Id;
            return result;
        }, ct);

    // ไม่มี actor/session และไม่มีทางลงผลอนุมัติ ใช้เฉพาะ worker ภายใน
    internal async Task<bool> ReevaluateAsync(Guid requestId, CancellationToken ct)
    {
        var changed = false;
        var result = await executor.ExecuteAsync(async () =>
        {
            var request = await executor.LockAsync(requestId, ct);
            if (request is null || request.State is not ("PendingApproval" or "Held") || request.Version == long.MaxValue)
                return new(200, "unchanged", null);
            var revision = await db.Set<AdvanceRequestRevision>().SingleOrDefaultAsync(x => x.RequestId == requestId && x.IsCurrent, ct);
            if (revision?.SubmittedAtUtc is null) return new(200, "unchanged", null);
            var step = await db.Set<AdvanceApprovalStep>().Where(x => x.RevisionId == revision.Id && x.Outcome == null)
                .OrderBy(x => x.StepOrder).FirstOrDefaultAsync(ct);
            if (step is null) return new(200, "unchanged", null);
            var route = await routes.ResolveAsync(requestId, revision.Id, step.LevelCode, ct);
            var state = route.PersonId is null ? "Held" : "PendingApproval";
            if (step.AssigneePersonId == route.PersonId && step.HoldCode == route.HoldCode
                && request.State == state && request.HoldCode == route.HoldCode) return new(200, "unchanged", null);
            var before = new { request.State, request.HoldCode, step.AssigneePersonId };
            Assign(request, step, route); request.Version++;
            // ประวัติระบบใช้ audit ที่รองรับ actor null ไม่สวมสิทธิ์บุคคลเพื่อสร้าง receipt/event
            await audit.WriteAsync(new SecurityAuditRequest(null, null, request.WorkspaceId, request.ProjectId, null,
                "field-advance.request.reevaluate", "field-advance-request", request.Id, "success", new Dictionary<string, string>
                {
                    ["actor-type"] = "system",
                    ["source"] = "request-routing-worker",
                    ["reason"] = route.HoldCode ?? "system-re-evaluation",
                    ["assignment-id"] = step.Id.ToString(),
                    ["evidence-reference"] = JsonSerializer.Serialize(new
                    {
                        revisionId = revision.Id,
                        version = request.Version,
                        before,
                        after = new { request.State, request.HoldCode, step.AssigneePersonId }
                    })
                }), ct);
            if (!SameRoute(route, await routes.ResolveAsync(requestId, revision.Id, step.LevelCode, ct))) return Conflict();
            changed = true;
            return new(200, "reevaluated", null);
        }, ct);
        return changed && result.Status == 200;
    }

    private async Task<Guid?> AuthorityAsync(Guid actor, AdvanceRequest request, AdvanceRequestRevision revision, AdvanceApprovalStep step, CancellationToken ct)
    {
        var person = await people.ResolveAsync(actor, request.WorkspaceId, ct);
        var scope = new FinanceScope(request.WorkspaceId, request.ProjectId);
        if (person is null || person == request.OwnerPersonId || person == request.DelegatePersonId
            || !(await access.CheckAsync(actor, scope, "request.approve", ct)).Allowed) return null;
        return await FinanceGrantQueries.Current(db, scope, "request.approve", clock.GetUtcNow())
            .AnyAsync(x => x.RecipientId == actor && x.RecipientPersonId == person && x.ApprovalLevels.Contains(step.LevelCode)
                && x.ApprovalLimit != null && x.ApprovalLimit >= revision.Total, ct) ? person : null;
    }

    private static void Assign(AdvanceRequest request, AdvanceApprovalStep step, RouteChoice route)
    {
        step.AssigneePersonId = route.PersonId; step.HoldCode = route.HoldCode;
        step.AssignmentReason = route.HoldCode ?? "ประเมินผู้รับงานตามสิทธิ์ปัจจุบัน";
        request.State = route.PersonId is null ? "Held" : "PendingApproval"; request.HoldCode = route.HoldCode;
    }
    private static bool SameRoute(RouteChoice left, RouteChoice right) => left.PersonId == right.PersonId
        && left.AccountId == right.AccountId && left.HoldCode == right.HoldCode;
}
