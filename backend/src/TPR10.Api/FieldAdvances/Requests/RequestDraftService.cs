using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Policies;
using static TPR10.Api.FieldAdvances.Requests.RequestCommandExecutor;

namespace TPR10.Api.FieldAdvances.Requests;

public sealed class RequestDraftService(Tpr10DbContext db, FinanceAccess access, FinancePersonResolver people,
    RequestAccess requestAccess, PolicyResolver policies, IAuditEventWriter audit, TimeProvider clock)
{
    private readonly RequestCommandExecutor executor = new(db, requestAccess, audit, clock);

    public Task<FinanceResult<RequestView>> CreateAsync(Guid actorId, CreateRequest input, RequestCommand meta, CancellationToken ct)
        => executor.ExecuteAsync(async () =>
        {
            if (input is null || !ValidMeta(meta)) return Invalid();
            var person = await AuthorizeCreateAsync(actorId, input.Scope, input.OwnerPersonId, ct);
            if (person is null) return Denied();
            const string action = "draft.create";
            var hash = Hash(new { action, actorId, input, meta });
            var replay = await executor.ReplayAsync(actorId, input.Scope, action, meta, hash, ct);
            if (replay is not null) return replay;
            if (meta.ExpectedVersion != 0) return Conflict();
            var calculated = await CalculateAsync(input.Scope, input.Content, ct);
            if (calculated.Value is null) return new(calculated.Status, calculated.Code, null);
            var (estimate, policy) = calculated.Value;
            var now = clock.GetUtcNow();
            var request = new AdvanceRequest
            {
                Id = Guid.NewGuid(),
                WorkspaceId = input.Scope.WorkspaceId,
                ProjectId = input.Scope.ProjectId!.Value,
                OwnerPersonId = input.OwnerPersonId,
                DelegatePersonId = person == input.OwnerPersonId ? null : person,
                CreatedBy = actorId,
                CreatedAtUtc = now
            };
            var revision = new AdvanceRequestRevision
            {
                Id = Guid.NewGuid(),
                WorkspaceId = request.WorkspaceId,
                ProjectId = request.ProjectId,
                RequestId = request.Id,
                RevisionNumber = 1,
                IsCurrent = true,
                ContentJson = JsonSerializer.Serialize(input.Content),
                ContentHash = Hash(input.Content),
                Total = estimate.Total,
                CreatedBy = actorId,
                CreatedByPersonId = person.Value,
                CreatedAtUtc = now
            };
            db.AddRange(request, revision);
            AddParticipant(request.OwnerPersonId, "Owner");
            if (request.DelegatePersonId is { } delegated) AddParticipant(delegated, "Delegate");
            AddLines(request, revision, estimate, policy);
            return await executor.FinishAsync(actorId, person.Value, request, revision, action, "DraftCreated", meta, hash,
                input.Content, estimate, 201, async () => await AuthorizeCreateAsync(actorId, input.Scope, input.OwnerPersonId, ct) == person, ct);

            void AddParticipant(Guid participant, string role) => db.Add(new AdvanceRequestParticipant
            {
                Id = Guid.NewGuid(),
                WorkspaceId = request.WorkspaceId,
                ProjectId = request.ProjectId,
                RequestId = request.Id,
                PersonId = participant,
                Role = role,
                CreatedBy = actorId,
                CreatedAtUtc = now
            });
        }, ct);

    public Task<FinanceResult<RequestView>> UpdateAsync(Guid actorId, Guid requestId, RequestContent content, RequestCommand meta, CancellationToken ct)
        => executor.ExecuteAsync(async () =>
        {
            if (!ValidMeta(meta)) return Invalid();
            var request = await executor.LockAsync(requestId, ct);
            if (request is null || !await AuthorizeAuthorAsync(actorId, request, ct)) return Denied();
            var scope = new FinanceScope(request.WorkspaceId, request.ProjectId);
            const string action = "draft.update";
            var hash = Hash(new { action, actorId, requestId, scope, request.OwnerPersonId, request.DelegatePersonId, content, meta });
            var replay = await executor.ReplayAsync(actorId, scope, action, meta, hash, ct);
            if (replay is not null) return replay;
            if (!(await requestAccess.CheckAsync(actorId, requestId, "edit", ct)).Allowed) return Denied();
            if (request.Version != meta.ExpectedVersion || request.Version == long.MaxValue) return Conflict();
            var revision = await db.Set<AdvanceRequestRevision>().SingleOrDefaultAsync(x => x.RequestId == requestId && x.IsCurrent, ct);
            if (revision is null || revision.SubmittedAtUtc is not null) return Conflict();
            var calculated = await CalculateAsync(scope, content, ct);
            if (calculated.Value is null) return new(calculated.Status, calculated.Code, null);
            var (estimate, policy) = calculated.Value;
            // Delete before inserting to preserve revision/line-order uniqueness. All stays in this transaction.
            await db.Set<AdvanceRequestLine>().Where(x => x.RevisionId == revision.Id).ExecuteDeleteAsync(ct);
            revision.ContentJson = JsonSerializer.Serialize(content);
            revision.ContentHash = Hash(content);
            revision.Total = estimate.Total;
            request.Version++;
            AddLines(request, revision, estimate, policy);
            var person = await people.ResolveAsync(actorId, request.WorkspaceId, ct);
            if (person is null) return Denied();
            return await executor.FinishAsync(actorId, person.Value, request, revision, action, "DraftUpdated", meta, hash,
                content, estimate, 200, () => AuthorizeAuthorAsync(actorId, request, ct), ct);
        }, ct);

    private async Task<Guid?> AuthorizeCreateAsync(Guid actor, FinanceScope scope, Guid owner, CancellationToken ct)
    {
        if (scope is null || scope.ProjectId is null || scope.ProjectId == Guid.Empty || owner == Guid.Empty) return null;
        var person = await people.ResolveAsync(actor, scope.WorkspaceId, ct);
        if (person is null || !(await access.CheckAsync(actor, scope, person == owner ? "request.create" : "request.delegate", ct)).Allowed)
            return null;
        return await OwnerEligibleAsync(scope, owner, ct) ? person : null;
    }

    private async Task<bool> AuthorizeAuthorAsync(Guid actor, AdvanceRequest request, CancellationToken ct)
    {
        var scope = new FinanceScope(request.WorkspaceId, request.ProjectId);
        var person = await people.ResolveAsync(actor, request.WorkspaceId, ct);
        if (person != (request.DelegatePersonId ?? request.OwnerPersonId)) return false;
        return (await access.CheckAsync(actor, scope, request.DelegatePersonId is null ? "request.create" : "request.delegate", ct)).Allowed
            && await OwnerEligibleAsync(scope, request.OwnerPersonId, ct);
    }

    // Shares FinanceAccess grant validity, without requiring the owner's interactive session.
    // Each eligible grant must belong to one currently active, unambiguously linked account.
    private async Task<bool> OwnerEligibleAsync(FinanceScope scope, Guid owner, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var accounts = await FinanceGrantQueries.Current(db, scope, "request.create", now)
            .Where(proposal => proposal.RecipientPersonId == owner)
            .Select(proposal => proposal.RecipientId).Distinct().ToArrayAsync(ct);
        foreach (var account in accounts)
            if (await people.ResolveAsync(account, scope.WorkspaceId, ct) == owner) return true;
        return false;
    }

    private async Task<FinanceResult<Tuple<Estimate, PolicySnapshot>>> CalculateAsync(FinanceScope scope, RequestContent content, CancellationToken ct)
    {
        if (content is null || content.Kind is not ("Individual" or "Team") || string.IsNullOrWhiteSpace(content.Purpose)
            || content.FieldStart > content.FieldEnd || content.TeamPersonIds is null
            || content.TeamPersonIds.Any(x => x == Guid.Empty) || content.TeamPersonIds.Distinct().Count() != content.TeamPersonIds.Length
            || content.Kind == "Individual" && content.TeamPersonIds.Length != 0
            || content.Kind == "Team" && content.TeamPersonIds.Length == 0
            || content.Lines is not { Length: > 0 } || content.Lines.Any(x => x is null || x.Id == Guid.Empty || string.IsNullOrWhiteSpace(x.Description))
            || content.Lines.Select(x => x.Id).Distinct().Count() != content.Lines.Length)
            return new(400, "invalid-request-content", null);
        if (await db.Set<FinancePerson>().CountAsync(x => x.WorkspaceId == scope.WorkspaceId && content.TeamPersonIds.Contains(x.Id), ct)
            != content.TeamPersonIds.Length) return new(400, "invalid-team", null);
        var policy = await policies.ResolveAuthorizedAsync(scope, clock.GetUtcNow(), ct);
        if (policy.Value is null) return new(policy.Status, policy.Code, null);
        var estimate = EstimateCalculator.Calculate(content.Lines, policy.Value);
        return estimate.Value is null ? new(estimate.Status, estimate.Code, null) : new(200, "ok", Tuple.Create(estimate.Value, policy.Value));
    }

    private void AddLines(AdvanceRequest request, AdvanceRequestRevision revision, Estimate estimate, PolicySnapshot policy)
    {
        for (var i = 0; i < estimate.Lines.Length; i++)
        {
            var line = estimate.Lines[i];
            var category = policy.Content.Categories.Single(x => x.Id == line.CategoryId);
            // Storage IDs are server-owned and globally unique; client IDs remain in ContentJson/Estimate.
            db.Add(new AdvanceRequestLine
            {
                Id = Guid.NewGuid(),
                WorkspaceId = request.WorkspaceId,
                ProjectId = request.ProjectId,
                RequestId = request.Id,
                RevisionId = revision.Id,
                LineOrder = i + 1,
                CategoryId = line.CategoryId,
                CategoryName = category.Name,
                Unit = category.Unit,
                Description = line.Description,
                Mode = line.Mode,
                Quantity = line.Quantity,
                UnitRate = line.UnitRate,
                Amount = line.Amount
            });
        }
    }

    private static bool ValidMeta(RequestCommand meta) => meta is not null && meta.CommandId != Guid.Empty
        && meta.ExpectedVersion >= 0 && meta.Reason is not null && meta.Reason.Length <= 2000;
}
