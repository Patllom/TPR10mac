using TPR10.Api.FieldAdvances.Payments;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Policies;
using static TPR10.Api.FieldAdvances.Requests.RequestCommandExecutor;

namespace TPR10.Api.FieldAdvances.Requests;

public sealed class RequestSubmissionService(Tpr10DbContext db, FinanceAccess access, FinancePersonResolver people,
    RequestAccess requestAccess, RequestDraftService drafts, ApprovalRouteResolver routes, IAuditEventWriter audit, TimeProvider clock)
{
    private readonly RequestCommandExecutor executor = new(db, requestAccess, audit, clock);

    public async Task<FinanceResult<ConfirmationQuote>> QuoteAsync(Guid actorId, Guid requestId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct);
            var request = await executor.LockAsync(requestId, ct);
            if (request is null || !(await requestAccess.CheckAsync(actorId, requestId, "read", ct)).Allowed)
                return new(403, "finance-access-denied", null);
            var revision = await CurrentAsync(requestId, ct);
            if (request.State is not ("Draft" or "AwaitingOwner") || revision is null || revision.SubmittedAtUtc is not null)
                return new(409, "request-conflict", null);
            var result = await QuoteLockedAsync(request, revision, ct);
            if (!(await requestAccess.CheckAsync(actorId, requestId, "read", ct)).Allowed)
                return new(403, "finance-access-denied", null);
            return result;
        }
        finally { db.ChangeTracker.Clear(); }
    }

    public Task<FinanceResult<RequestView>> SendToOwnerAsync(Guid actorId, Guid requestId, RequestCommand meta, CancellationToken ct)
        => executor.ExecuteAsync(async () =>
        {
            if (!RequestDraftService.ValidMeta(meta)) return Invalid();
            var request = await executor.LockAsync(requestId, ct);
            if (request is null || !await AuthorityAsync(actorId, request, owner: false, ct)) return Denied();
            const string action = "request.send-to-owner";
            var scope = new FinanceScope(request.WorkspaceId, request.ProjectId);
            var hash = Hash(new { action, actorId, requestId, meta });
            var replay = await executor.ReplayAsync(actorId, scope, action, meta, hash, ct);
            if (replay is not null) return replay;
            if (!(await requestAccess.CheckAsync(actorId, requestId, "send-to-owner", ct)).Allowed) return Denied();
            if (request.Version != meta.ExpectedVersion || request.Version == long.MaxValue) return Conflict();
            var revision = await CurrentAsync(requestId, ct);
            if (revision is null || revision.SubmittedAtUtc is not null) return Conflict();
            var quote = await QuoteLockedAsync(request, revision, ct);
            if (quote.Value is null) return new(quote.Status, quote.Code, null);
            var content = JsonSerializer.Deserialize<RequestContent>(revision.ContentJson)!;
            await StoreEstimateAsync(request, revision, quote.Value, ct);
            request.State = "AwaitingOwner"; request.Version++;
            return await executor.FinishAsync(actorId, request.DelegatePersonId!.Value, request, revision, action,
                "SentToOwner", meta, hash, content, quote.Value.Estimate, 200,
                () => AuthorityAsync(actorId, request, owner: false, ct), ct);
        }, ct);

    public Task<FinanceResult<RequestView>> ConfirmAsync(Guid actorId, Guid requestId, string confirmationHash, RequestCommand meta, CancellationToken ct)
        => executor.ExecuteAsync(async () =>
        {
            if (!RequestDraftService.ValidMeta(meta)) return Invalid();
            var request = await executor.LockAsync(requestId, ct);
            // Authority is independent of state for replay. A readable report or known hash is not authority.
            if (request is null || !await AuthorityAsync(actorId, request, owner: true, ct)) return Denied();
            const string action = "request.confirm";
            var scope = new FinanceScope(request.WorkspaceId, request.ProjectId);
            var hash = Hash(new { action, actorId, requestId, confirmationHash, meta });
            var replay = await executor.ReplayAsync(actorId, scope, action, meta, hash, ct);
            if (replay is not null) return replay;
            if (!(await requestAccess.CheckAsync(actorId, requestId, "confirm", ct)).Allowed) return Denied();
            if (request.Version != meta.ExpectedVersion || request.Version == long.MaxValue) return Conflict();
            var revision = await CurrentAsync(requestId, ct);
            if (revision is null || revision.SubmittedAtUtc is not null) return Conflict();
            if (!await CeilingAmendmentService.CanProgressAsync(db, request.Id, ct)) return Conflict();
            var calculated = await QuoteLockedAsync(request, revision, ct);
            // A previously displayed quote is no longer confirmable, including when the
            // current policy disables a category or cannot cover the total. Quote reports
            // the specific correction needed; confirmation must never accept stale data.
            if (calculated.Value is null) return Conflict();
            var quote = calculated.Value;
            if (!string.Equals(confirmationHash, quote.ConfirmationHash, StringComparison.Ordinal)) return Conflict();
            var content = JsonSerializer.Deserialize<RequestContent>(revision.ContentJson)!;
            await StoreEstimateAsync(request, revision, quote, ct);
            revision.CompanyPolicyRevisionId = quote.Policy.CompanyVersionId;
            revision.ProjectPolicyRevisionId = quote.Policy.ProjectVersionId;
            revision.PolicyHash = quote.Policy.ContentHash;
            revision.PolicySnapshotJson = JsonSerializer.Serialize(quote.Policy);
            var steps = quote.Levels.Select((level, index) => new AdvanceApprovalStep
            {
                Id = Guid.NewGuid(),
                WorkspaceId = request.WorkspaceId,
                ProjectId = request.ProjectId,
                RequestId = request.Id,
                RevisionId = revision.Id,
                StepOrder = index + 1,
                LevelCode = level
            }).ToArray();
            db.AddRange(steps);
            // Resolver reads persisted current revision; all children precede the submission guard.
            await db.SaveChangesAsync(ct);
            var route = await routes.ResolveAsync(request.Id, revision.Id, steps[0].LevelCode, ct);
            steps[0].AssigneePersonId = route.PersonId;
            steps[0].HoldCode = route.HoldCode;
            steps[0].AssignmentReason = route.HoldCode ?? "initial-submission";
            request.State = route.PersonId is null ? "Held" : "PendingApproval";
            request.HoldCode = route.HoldCode;
            request.Version++;
            revision.ConfirmedByPersonId = request.OwnerPersonId;
            revision.SubmittedAtUtc = clock.GetUtcNow();
            return await executor.FinishAsync(actorId, request.OwnerPersonId, request, revision, action, "Confirmed",
                meta, hash, content, quote.Estimate, 200, async () =>
                {
                    if (!await AuthorityAsync(actorId, request, owner: true, ct)) return false;
                    // Time-based eligibility may expire during audit even while mutation locks are held.
                    var current = await routes.ResolveAsync(request.Id, revision.Id, steps[0].LevelCode, ct);
                    return current.PersonId == route.PersonId && current.AccountId == route.AccountId && current.HoldCode == route.HoldCode;
                }, ct);
        }, ct);

    private Task<AdvanceRequestRevision?> CurrentAsync(Guid requestId, CancellationToken ct) =>
        db.Set<AdvanceRequestRevision>().SingleOrDefaultAsync(x => x.RequestId == requestId && x.IsCurrent, ct);

    private async Task<bool> AuthorityAsync(Guid actor, AdvanceRequest request, bool owner, CancellationToken ct)
    {
        var person = await people.ResolveAsync(actor, request.WorkspaceId, ct);
        var expected = owner ? request.OwnerPersonId : request.DelegatePersonId;
        if (person is null || expected is null || person != expected) return false;
        var scope = new FinanceScope(request.WorkspaceId, request.ProjectId);
        return (await access.CheckAsync(actor, scope, owner ? "request.create" : "request.delegate", ct)).Allowed
            && (owner || await drafts.OwnerEligibleAsync(scope, request.OwnerPersonId, ct));
    }

    private async Task<FinanceResult<ConfirmationQuote>> QuoteLockedAsync(AdvanceRequest request, AdvanceRequestRevision revision, CancellationToken ct)
    {
        RequestContent? content;
        try { content = JsonSerializer.Deserialize<RequestContent>(revision.ContentJson); }
        catch (JsonException) { return new(400, "invalid-request-content", null); }
        var calculated = await drafts.CalculateAsync(new(request.WorkspaceId, request.ProjectId), content!, ct);
        if (calculated.Value is null) return new(calculated.Status, calculated.Code, null);
        var (estimate, policy) = calculated.Value;
        var levels = ApprovalLevelEvaluator.Evaluate(estimate.Total, policy);
        if (levels.Value is null) return new(levels.Status, levels.Code, null);
        var hash = Hash(new
        {
            request.Id,
            request.WorkspaceId,
            request.ProjectId,
            request.OwnerPersonId,
            request.DelegatePersonId,
            RevisionId = revision.Id,
            request.Version,
            revision.ContentHash,
            Content = content,
            Estimate = estimate,
            Policy = policy,
            Levels = levels.Value
        });
        return new(200, "ok", new(request.Id, revision.Id, request.Version, hash, estimate, policy, levels.Value));
    }

    private async Task StoreEstimateAsync(AdvanceRequest request, AdvanceRequestRevision revision, ConfirmationQuote quote, CancellationToken ct)
    {
        await db.Set<AdvanceRequestLine>().Where(x => x.RevisionId == revision.Id).ExecuteDeleteAsync(ct);
        revision.Total = quote.Estimate.Total;
        drafts.AddLines(request, revision, quote.Estimate, quote.Policy);
    }
}
