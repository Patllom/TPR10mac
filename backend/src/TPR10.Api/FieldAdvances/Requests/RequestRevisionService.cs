using TPR10.Api.FieldAdvances.Payments;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using static TPR10.Api.FieldAdvances.Requests.RequestCommandExecutor;

namespace TPR10.Api.FieldAdvances.Requests;

public sealed class RequestRevisionService(Tpr10DbContext db, FinanceAccess access, FinancePersonResolver people,
    RequestAccess requestAccess, RequestDraftService drafts, IAuditEventWriter audit, TimeProvider clock)
{
    private readonly RequestCommandExecutor executor = new(db, requestAccess, audit, clock);

    public Task<FinanceResult<RequestView>> ReturnToAuthorAsync(Guid actorId, Guid requestId, RequestCommand meta, CancellationToken ct)
        => ChangeAsync(actorId, requestId, "return-to-author", meta, ct);
    public Task<FinanceResult<RequestView>> ReviseAsync(Guid actorId, Guid requestId, RequestCommand meta, CancellationToken ct)
        => ChangeAsync(actorId, requestId, "revise", meta, ct);
    public Task<FinanceResult<RequestView>> WithdrawAsync(Guid actorId, Guid requestId, RequestCommand meta, CancellationToken ct)
        => ChangeAsync(actorId, requestId, "withdraw", meta, ct);
    public Task<FinanceResult<RequestView>> CopyAsync(Guid actorId, Guid requestId, RequestCommand meta, CancellationToken ct)
        => ChangeAsync(actorId, requestId, "copy", meta, ct);

    private Task<FinanceResult<RequestView>> ChangeAsync(Guid actor, Guid id, string operation, RequestCommand meta, CancellationToken ct)
        => executor.ExecuteAsync(async () =>
        {
            if (!RequestDraftService.ValidMeta(meta) || operation is "return-to-author" or "withdraw" && string.IsNullOrWhiteSpace(meta.Reason))
                return Invalid();
            var request = await executor.LockAsync(id, ct);
            if (request is null || !await AuthorityAsync(actor, request, operation, ct)) return Denied();
            var scope = new FinanceScope(request.WorkspaceId, request.ProjectId);
            var action = "request." + operation;
            var hash = Hash(new { action, actor, id, meta });
            var replay = await executor.ReplayAsync(actor, scope, action, meta, hash, ct);
            if (replay is not null) return replay;
            if (!(await requestAccess.CheckAsync(actor, id, operation, ct)).Allowed) return Denied();
            if (request.Version != meta.ExpectedVersion || request.Version == long.MaxValue) return Conflict();
            var revision = await db.Set<AdvanceRequestRevision>().SingleOrDefaultAsync(x => x.RequestId == id && x.IsCurrent, ct);
            if (revision is null) return Conflict();
            var person = await people.ResolveAsync(actor, request.WorkspaceId, ct);
            if (person is null) return Denied();
            var content = JsonSerializer.Deserialize<RequestContent>(revision.ContentJson)!;
            var estimate = await drafts.StoredEstimateAsync(revision, ct);
            var target = request;
            var kind = operation switch { "return-to-author" => "ReturnedToAuthor", "withdraw" => "Withdrawn", "revise" => "Revised", _ => "Copied" };
            if (operation == "copy")
            {
                var calculated = await drafts.CalculateAsync(scope, content, ct);
                if (calculated.Value is null) return new(calculated.Status, calculated.Code, null);
                var (currentEstimate, policy) = calculated.Value;
                estimate = currentEstimate;
                target = new AdvanceRequest
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = request.WorkspaceId,
                    ProjectId = request.ProjectId,
                    OwnerPersonId = request.OwnerPersonId,
                    DelegatePersonId = person == request.OwnerPersonId ? null : person,
                    SourceRequestId = request.Id,
                    CreatedBy = actor,
                    CreatedAtUtc = clock.GetUtcNow()
                };
                revision = NewRevision(target, content, estimate, 1, actor, person.Value);
                db.AddRange(target, revision);
                AddParticipant(target, target.OwnerPersonId, "Owner", actor);
                if (target.DelegatePersonId is { } delegated) AddParticipant(target, delegated, "Delegate", actor);
                drafts.AddLines(target, revision, estimate, policy);
            }
            else
            {
                if (operation == "revise")
                {
                    if (!await CeilingAmendmentService.CanProgressAsync(db, request.Id, ct)) return Conflict();
                    if (revision.SubmittedAtUtc is null || revision.RevisionNumber == int.MaxValue) return Conflict();
                    var lines = await db.Set<AdvanceRequestLine>().AsNoTracking().Where(x => x.RevisionId == revision.Id).ToArrayAsync(ct);
                    revision.IsCurrent = false;
                    // ปลด unique current ก่อนเพิ่มฉบับใหม่ โดยยังอยู่ใน transaction เดียวกัน
                    await db.SaveChangesAsync(ct);
                    revision = NewRevision(request, content, estimate, revision.RevisionNumber + 1, actor, person.Value);
                    revision.ApprovalBasisAmount = estimate.Total;
                    revision.ApprovalBasisJson = JsonSerializer.Serialize(new ApprovalBasis(estimate.Total,
                        [new(request.Id, revision.Id, estimate.Total)]));
                    db.Add(revision);
                    foreach (var line in lines)
                    {
                        line.Id = Guid.NewGuid(); line.RevisionId = revision.Id; db.Add(line);
                    }
                    request.State = "Draft";
                }
                else if (operation == "return-to-author")
                {
                    if (revision.SubmittedAtUtc is not null) return Conflict();
                    request.State = "Draft";
                }
                else
                {
                    request.State = "Withdrawn";
                    await SupplementService.ReleaseAsync(db, request, ct);
                    var pending = await db.Set<AdvanceApprovalStep>().Where(x => x.RevisionId == revision.Id && x.Outcome == null).ToArrayAsync(ct);
                    foreach (var step in pending)
                    {
                        step.AssigneePersonId = null; step.HoldCode = null; step.AssignmentReason = "ปิดงานเนื่องจากผู้รับผิดชอบถอนคำขอ";
                    }
                }
                request.HoldCode = null; request.Version++;
            }
            return await executor.FinishAsync(actor, person.Value, target, revision, action, kind, meta, hash,
                content, estimate, operation == "copy" ? 201 : 200, () => AuthorityAsync(actor, request, operation, ct), ct);
        }, ct);

    private async Task<bool> AuthorityAsync(Guid actor, AdvanceRequest request, string operation, CancellationToken ct)
    {
        if (operation == "revise") return await drafts.AuthorizeAuthorAsync(actor, request, ct);
        if (operation == "copy") return (await requestAccess.CheckAsync(actor, request.Id, "read", ct)).Allowed
            && await drafts.AuthorizeCreateAsync(actor, new(request.WorkspaceId, request.ProjectId), request.OwnerPersonId, ct) is not null;
        return await people.ResolveAsync(actor, request.WorkspaceId, ct) == request.OwnerPersonId
            && (await access.CheckAsync(actor, new(request.WorkspaceId, request.ProjectId), "request.create", ct)).Allowed;
    }

    private AdvanceRequestRevision NewRevision(AdvanceRequest request, RequestContent content, Estimate estimate, int number, Guid actor, Guid person)
        => new()
        {
            Id = Guid.NewGuid(),
            WorkspaceId = request.WorkspaceId,
            ProjectId = request.ProjectId,
            RequestId = request.Id,
            RevisionNumber = number,
            IsCurrent = true,
            ContentJson = JsonSerializer.Serialize(content),
            ContentHash = Hash(content),
            Total = estimate.Total,
            CreatedBy = actor,
            CreatedByPersonId = person,
            CreatedAtUtc = clock.GetUtcNow()
        };

    private void AddParticipant(AdvanceRequest request, Guid person, string role, Guid actor)
        => db.Add(new AdvanceRequestParticipant
        {
            Id = Guid.NewGuid(),
            WorkspaceId = request.WorkspaceId,
            ProjectId = request.ProjectId,
            RequestId = request.Id,
            PersonId = person,
            Role = role,
            CreatedBy = actor,
            CreatedAtUtc = clock.GetUtcNow()
        });
}
