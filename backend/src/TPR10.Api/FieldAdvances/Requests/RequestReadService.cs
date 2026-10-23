using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.Identity.Data;
using TPR10.Api.Organization.Data;
using TPR10.Api.Scopes;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.FieldAdvances.Policies;

namespace TPR10.Api.FieldAdvances.Requests;

public sealed class RequestReadService(Tpr10DbContext db, FinanceAccess access, FinancePersonResolver people,
    RequestAccess requests, RequestDraftService drafts, ScopeAccess sessions, TimeProvider clock, PolicyResolver policies)
{
    private static readonly string[] Capabilities = ["request.create", "request.delegate", "request.approve", "report.read"];
    private static FinanceResult<T> Denied<T>() => new(403, "finance-access-denied", default);
    private static FinanceResult<T> Invalid<T>() => new(400, "invalid-request", default);
    private static bool Page(int page, int size) => page > 0 && size is > 0 and <= 100 && (long)(page - 1) * size <= int.MaxValue;
    private async Task<FinanceResult<T>> Locked<T>(Guid actor, Func<Task<FinanceResult<T>>> read, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct);
        var identity = await sessions.ValidateSessionAsync(true, ct);
        if (actor == Guid.Empty || identity.Status is not null || identity.ActorId != actor) return Denied<T>();
        var result = await read();
        identity = await sessions.ValidateSessionAsync(true, ct);
        if (identity.Status is not null || identity.ActorId != actor) return Denied<T>();
        await tx.CommitAsync(ct);
        return result;
    }
    private async Task<string[]> Allowed(Guid actor, FinanceScope scope, CancellationToken ct)
    {
        if (scope.ProjectId is null || scope.ProjectId == Guid.Empty) return [];
        var found = new List<string>();
        foreach (var capability in Capabilities)
            if ((await access.CheckAsync(actor, scope, capability, ct)).Allowed) found.Add(capability);
        return found.ToArray();
    }

    public Task<FinanceResult<PolicySnapshot>> ConfigurationAsync(Guid actor, FinanceScope scope, CancellationToken ct) => Locked(actor, async () =>
    {
        var caps = await Allowed(actor, scope, ct);
        if (!caps.Contains("request.create") && !caps.Contains("request.delegate")) return Denied<PolicySnapshot>();
        var result = await policies.ResolveAuthorizedAsync(scope, clock.GetUtcNow(), ct);
        if (!(await Allowed(actor, scope, ct)).SequenceEqual(caps)) return Denied<PolicySnapshot>();
        return result;
    }, ct);

    public Task<FinanceResult<RequestScreen>> ScreenAsync(Guid actor, Guid id, CancellationToken ct) => Locked(actor, async () =>
    {
        if (!(await requests.CheckAsync(actor, id, "read", ct)).Allowed) return Denied<RequestScreen>();
        var row = await View(id, ct);
        var revision = await db.Set<AdvanceRequestRevision>().AsNoTracking().SingleAsync(x => x.Id == row.RevisionId, ct);
        var previous = await db.Set<AdvanceRequestRevision>().AsNoTracking().Where(x => x.RequestId == id
            && x.WorkspaceId == row.Scope.WorkspaceId && x.ProjectId == row.Scope.ProjectId && x.RevisionNumber < revision.RevisionNumber)
            .OrderByDescending(x => x.RevisionNumber).FirstOrDefaultAsync(ct);
        var actions = new List<string>();
        foreach (var action in new[] { "edit", "send-to-owner", "confirm", "return-to-author", "review", "revise", "withdraw", "copy" })
            if ((await requests.CheckAsync(actor, id, action, ct)).Allowed) actions.Add(action);
        var frozen = revision.PolicySnapshotJson is null ? null : JsonSerializer.Deserialize<PolicySnapshot>(revision.PolicySnapshotJson);
        if (!(await requests.CheckAsync(actor, id, "read", ct)).Allowed) return Denied<RequestScreen>();
        return new(200, "ok", new(id, row.RevisionId, row.Version, revision.RevisionNumber,
            previous is null ? null : JsonSerializer.Deserialize<RequestContent>(previous.ContentJson),
            frozen is null ? null : new(frozen.CompanyVersionId, frozen.ProjectVersionId, PolicyWireContent.From(frozen.Content), frozen.ContentHash, frozen.Scope, frozen.RequiresReview), actions.ToArray()));
    }, ct);

    // Team selection grants no financial access. Only certified, currently employed aliases
    // in this company participate; public pages contain one deterministic label per person.
    public Task<FinanceResult<RequestPersonChoice[]>> TeamPeopleAsync(Guid actor, FinanceScope scope, string prefix, int page, int size, CancellationToken ct) => Locked(actor, async () =>
    {
        var caps = await Allowed(actor, scope, ct);
        if (!caps.Contains("request.create") && !caps.Contains("request.delegate")) return Denied<RequestPersonChoice[]>();
        if (!Page(page, size) || prefix.Length > 100 || prefix.Any(char.IsControl)) return Invalid<RequestPersonChoice[]>();
        var now = clock.GetUtcNow();
        var candidates = (from link in db.Set<FinanceAccountLink>().AsNoTracking()
                          join employee in DirectoryQueries.Employment(db, now) on link.UserId equals employee.UserId
                          where link.WorkspaceId == scope.WorkspaceId && employee.WorkspaceId == scope.WorkspaceId
                              && link.CreatedAtUtc <= now && link.EndedAtUtc == null
                          select link.PersonId).Distinct().OrderBy(x => x);
        var rows = new List<RequestPersonChoice>(); long seen = 0; var skip = (long)(page - 1) * size;
        for (var offset = 0; rows.Count < size; offset = checked(offset + 100))
        {
            var batch = await candidates.Skip(offset).Take(100).ToArrayAsync(ct);
            foreach (var person in batch)
            {
                var aliases = from link in db.Set<FinanceAccountLink>().AsNoTracking()
                              join u in db.Set<IdentityUser>().AsNoTracking() on link.UserId equals u.Id
                              where link.PersonId == person && link.WorkspaceId == scope.WorkspaceId && link.CreatedAtUtc <= now && link.EndedAtUtc == null
                              orderby u.Id
                              select new { u.Id, u.Username };
                string? name = null;
                for (var offsetAlias = 0; name is null; offsetAlias = checked(offsetAlias + 100))
                {
                    var pageAliases = await aliases.Skip(offsetAlias).Take(100).ToArrayAsync(ct);
                    foreach (var alias in pageAliases)
                        if (await people.ResolveAsync(alias.Id, scope.WorkspaceId, ct) == person) { name = alias.Username; break; }
                    if (pageAliases.Length < 100) break;
                }
                if (name is null || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || seen++ < skip) continue;
                rows.Add(new(person, name)); if (rows.Count == size) break;
            }
            if (batch.Length < 100) break;
        }
        if (!(await Allowed(actor, scope, ct)).SequenceEqual(caps)) return Denied<RequestPersonChoice[]>();
        // Time-based employment expiry can occur while the identity lock is held.
        foreach (var row in rows)
        {
            var valid = await (from link in db.Set<FinanceAccountLink>()
                               join u in db.Set<IdentityUser>() on link.UserId equals u.Id
                               where link.WorkspaceId == scope.WorkspaceId && link.PersonId == row.Id && u.Username == row.Name
                                   && link.CreatedAtUtc <= clock.GetUtcNow() && link.EndedAtUtc == null
                               select u.Id).FirstOrDefaultAsync(ct);
            if (await people.ResolveAsync(valid, scope.WorkspaceId, ct) != row.Id) return Denied<RequestPersonChoice[]>();
        }
        return new(200, "ok", rows.ToArray());
    }, ct);

    // Bounded candidate batches, then authoritative filtering BEFORE counting the public page.
    // Only IDs are scanned; request contents are loaded only for the authorized result page.
    public Task<FinanceResult<RequestView[]>> ListAsync(Guid actorId, FinanceScope scope, string mode, int page, int pageSize, CancellationToken ct) => Locked(actorId, async () =>
    {
        if (!Page(page, pageSize) || mode is not ("mine" or "delegated" or "review" or "report")) return Invalid<RequestView[]>();
        var capability = mode switch { "mine" => "request.create", "delegated" => "request.delegate", "review" => "request.approve", _ => "report.read" };
        if (scope.ProjectId is null || !(await access.CheckAsync(actorId, scope, capability, ct)).Allowed) return Denied<RequestView[]>();
        var person = await people.ResolveAsync(actorId, scope.WorkspaceId, ct);
        if (person is null) return Denied<RequestView[]>();
        var query = db.Set<AdvanceRequest>().AsNoTracking().Where(x => x.WorkspaceId == scope.WorkspaceId && x.ProjectId == scope.ProjectId);
        if (mode == "mine") query = query.Where(x => x.OwnerPersonId == person);
        if (mode == "delegated") query = query.Where(x => x.DelegatePersonId == person);
        if (mode == "review") query = query.Where(x => x.State == "PendingApproval" && db.Set<AdvanceRequestRevision>().Any(r =>
            r.RequestId == x.Id && r.IsCurrent && r.SubmittedAtUtc != null && db.Set<AdvanceApprovalStep>().Any(s =>
                s.RevisionId == r.Id && s.Outcome == null && s.HoldCode == null && s.AssigneePersonId == person
                && !db.Set<AdvanceApprovalStep>().Any(prior => prior.RevisionId == r.Id && prior.StepOrder < s.StepOrder && prior.Outcome != "Approved"))));
        var selected = new List<Guid>(); long seen = 0; var skip = (long)(page - 1) * pageSize;
        for (var offset = 0; selected.Count < pageSize; offset = checked(offset + 100))
        {
            var batch = await query.OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id).Skip(offset).Take(100).Select(x => x.Id).ToArrayAsync(ct);
            foreach (var id in batch)
                if ((await requests.CheckAsync(actorId, id, mode == "review" ? "review" : "read", ct)).Allowed && seen++ >= skip)
                { selected.Add(id); if (selected.Count == pageSize) break; }
            if (batch.Length < 100) break;
        }
        var rows = new List<RequestView>();
        foreach (var id in selected) rows.Add(await View(id, ct));
        if (!(await access.CheckAsync(actorId, scope, capability, ct)).Allowed) return Denied<RequestView[]>();
        foreach (var id in selected)
            if (!(await requests.CheckAsync(actorId, id, mode == "review" ? "review" : "read", ct)).Allowed) return Denied<RequestView[]>();
        return new(200, "ok", rows.ToArray());
    }, ct);

    public Task<FinanceResult<RequestView>> DetailsAsync(Guid actorId, Guid id, CancellationToken ct) => Locked(actorId, async () =>
    {
        if (!(await requests.CheckAsync(actorId, id, "read", ct)).Allowed) return Denied<RequestView>();
        var row = await View(id, ct);
        if (!(await requests.CheckAsync(actorId, id, "read", ct)).Allowed) return Denied<RequestView>();
        return new(200, "ok", row);
    }, ct);
    public Task<FinanceResult<RequestRevisionSummary[]>> RevisionsAsync(Guid actorId, Guid id, int page, int size, CancellationToken ct) => Locked(actorId, async () =>
    {
        if (!(await requests.CheckAsync(actorId, id, "read", ct)).Allowed) return Denied<RequestRevisionSummary[]>();
        if (!Page(page, size)) return Invalid<RequestRevisionSummary[]>();
        var rows = await db.Set<AdvanceRequestRevision>().AsNoTracking().Where(x => x.RequestId == id)
            .OrderByDescending(x => x.RevisionNumber).Skip((page - 1) * size).Take(size)
            .Select(x => new RequestRevisionSummary(id, x.Id, x.RevisionNumber, x.SubmittedAtUtc)).ToArrayAsync(ct);
        if (!(await requests.CheckAsync(actorId, id, "read", ct)).Allowed) return Denied<RequestRevisionSummary[]>();
        return new(200, "ok", rows);
    }, ct);
    public Task<FinanceResult<RequestRevisionDetail>> RevisionAsync(Guid actorId, Guid id, Guid revisionId, CancellationToken ct) => Locked(actorId, async () =>
    {
        if (!(await requests.CheckAsync(actorId, id, "read", ct)).Allowed) return Denied<RequestRevisionDetail>();
        var request = await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == id, ct);
        var revision = await db.Set<AdvanceRequestRevision>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == revisionId
            && x.RequestId == id && x.WorkspaceId == request.WorkspaceId && x.ProjectId == request.ProjectId, ct);
        if (revision is null) return Denied<RequestRevisionDetail>();
        // Load one exact revision, never every revision's contents for a history page.
        // No actions or mutable assignment are projected onto historical evidence.
        var steps = await db.Set<AdvanceApprovalStep>().AsNoTracking().Where(x => x.RequestId == id && x.RevisionId == revisionId)
            .OrderBy(x => x.StepOrder).Select(x => new RequestRevisionStep(x.Id, x.LevelCode, x.StepOrder, x.Outcome,
                x.DecidedByPersonId, x.DecidedAtUtc, x.DecisionReason)).ToArrayAsync(ct);
        var frozen = revision.PolicySnapshotJson is null ? null : JsonSerializer.Deserialize<PolicySnapshot>(revision.PolicySnapshotJson);
        var estimate = await drafts.StoredEstimateAsync(revision, ct);
        var row = new RequestRevisionDetail(id, revisionId, revision.RevisionNumber, new(request.WorkspaceId, request.ProjectId),
            revision.SubmittedAtUtc, JsonSerializer.Deserialize<RequestContent>(revision.ContentJson)!,
            estimate is null ? null : RequestEstimateHttp.From(estimate),
            frozen is null ? null : new(frozen.CompanyVersionId, frozen.ProjectVersionId, PolicyWireContent.From(frozen.Content), frozen.ContentHash, frozen.Scope, frozen.RequiresReview), steps);
        if (!(await requests.CheckAsync(actorId, id, "read", ct)).Allowed) return Denied<RequestRevisionDetail>();
        return new(200, "ok", row);
    }, ct);
    private async Task<RequestView> View(Guid id, CancellationToken ct)
    {
        var request = await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == id, ct);
        var revision = await db.Set<AdvanceRequestRevision>().AsNoTracking().SingleAsync(x => x.RequestId == id && x.IsCurrent, ct);
        RequestStepView? step = null;
        if (request.State is "PendingApproval" or "Held" && revision.SubmittedAtUtc is not null)
        {
            var next = await db.Set<AdvanceApprovalStep>().AsNoTracking().Where(x => x.RevisionId == revision.Id && x.Outcome != "Approved")
                .OrderBy(x => x.StepOrder).FirstOrDefaultAsync(ct);
            if (next is { Outcome: null }) step = new(next.Id, next.LevelCode, next.StepOrder, next.AssigneePersonId, next.HoldCode);
        }
        return new(id, revision.Id, request.Version, request.State, new(request.WorkspaceId, request.ProjectId),
            request.OwnerPersonId, request.DelegatePersonId, JsonSerializer.Deserialize<RequestContent>(revision.ContentJson)!,
            await drafts.StoredEstimateAsync(revision, ct), request.HoldCode)
        { CurrentStep = step };
    }

    public Task<FinanceResult<RequestHistory[]>> HistoryAsync(Guid actorId, Guid id, int page, int size, CancellationToken ct) => Locked(actorId, async () =>
    {
        if (!(await requests.CheckAsync(actorId, id, "read", ct)).Allowed) return Denied<RequestHistory[]>();
        if (!Page(page, size)) return Invalid<RequestHistory[]>();
        var request = await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == id, ct);
        var human = db.Set<AdvanceRequestEvent>().AsNoTracking().Where(x => x.RequestId == id
            && x.WorkspaceId == request.WorkspaceId && x.ProjectId == request.ProjectId);
        var system = db.AuditEvents.AsNoTracking().Where(x => x.TargetId == id && x.TargetType == "field-advance-request"
            && x.EventType == "field-advance.request.reevaluate" && x.ActorId == null && x.Outcome == "success"
            && x.WorkspaceId == request.WorkspaceId && x.ProjectId == request.ProjectId);
        var keys = await human.Select(x => new { x.Id, Time = x.CreatedAtUtc, System = false })
            .Concat(system.Select(x => new { x.Id, Time = x.OccurredAtUtc, System = true }))
            .OrderBy(x => x.Time).ThenBy(x => x.Id).Skip((page - 1) * size).Take(size).ToArrayAsync(ct);
        var rows = new List<RequestHistory>();
        foreach (var key in keys)
        {
            if (!key.System)
            {
                var e = await human.SingleAsync(x => x.Id == key.Id, ct);
                // Historical numeric domain evidence is exposed as strings, never JS money numbers.
                using var data = JsonDocument.Parse(e.DataJson);
                var metadata = JsonSerializer.SerializeToElement(data.RootElement.EnumerateObject().ToDictionary(x => x.Name,
                    x => x.Value.ValueKind == JsonValueKind.String ? x.Value.GetString() : x.Value.GetRawText()));
                rows.Add(new(e.Id, e.RevisionId, e.Kind, e.ActorId, e.CreatedAtUtc, e.Reason, e.StepId, e.AggregateVersion, metadata));
            }
            else
            {
                var metadata = await db.AuditMetadata.AsNoTracking().Where(x => x.AuditEventId == key.Id
                    && (x.Key == "evidence-reference" || x.Key == "assignment-id" || x.Key == "reason")).ToDictionaryAsync(x => x.Key, x => x.Value, ct);
                var evidence = JsonSerializer.Deserialize<JsonElement>(metadata["evidence-reference"]);
                rows.Add(new(key.Id, evidence.GetProperty("revisionId").GetGuid(), "field-advance.request.reevaluate", null,
                    key.Time, metadata["reason"], Guid.Parse(metadata["assignment-id"]), evidence.GetProperty("version").GetInt64(), evidence));
            }
        }
        if (!(await requests.CheckAsync(actorId, id, "read", ct)).Allowed) return Denied<RequestHistory[]>();
        return new(200, "ok", rows.ToArray());
    }, ct);

    public Task<FinanceResult<RequestScopeChoice[]>> ScopesAsync(Guid actorId, int page, int size, CancellationToken ct) => Locked(actorId, async () =>
    {
        if (!Page(page, size)) return Invalid<RequestScopeChoice[]>();
        var candidates = db.Set<GrantProposal>().AsNoTracking().Where(x => x.RecipientId == actorId && x.ProjectId != null)
            .Select(x => new { x.WorkspaceId, ProjectId = x.ProjectId!.Value }).Distinct().OrderBy(x => x.WorkspaceId).ThenBy(x => x.ProjectId);
        var rows = new List<RequestScopeChoice>(); long seen = 0; var skip = (long)(page - 1) * size;
        for (var offset = 0; rows.Count < size; offset = checked(offset + 100))
        {
            var batch = await candidates.Skip(offset).Take(100).ToArrayAsync(ct);
            foreach (var c in batch)
            {
                var scope = new FinanceScope(c.WorkspaceId, c.ProjectId); var caps = await Allowed(actorId, scope, ct);
                if (caps.Length == 0 || seen++ < skip) continue;
                var name = await db.Set<Workspace>().Where(x => x.Id == c.WorkspaceId).Select(x => x.Name).SingleAsync(ct);
                var project = await db.Set<Project>().Where(x => x.Id == c.ProjectId && x.WorkspaceId == c.WorkspaceId).Select(x => x.Name).SingleAsync(ct);
                rows.Add(new(scope, name, project, caps)); if (rows.Count == size) break;
            }
            if (batch.Length < 100) break;
        }
        foreach (var row in rows) if (!(await Allowed(actorId, row.Scope, ct)).SequenceEqual(row.Capabilities)) return Denied<RequestScopeChoice[]>();
        return new(200, "ok", rows.ToArray());
    }, ct);

    // Owner picker: person IDs, not account IDs. Alias accounts are collapsed before public paging.
    public Task<FinanceResult<RequestPersonChoice[]>> PeopleAsync(Guid actorId, FinanceScope scope, string prefix, int page, int size, CancellationToken ct) => Locked(actorId, async () =>
    {
        var caps = await Allowed(actorId, scope, ct);
        if (!caps.Contains("request.create") && !caps.Contains("request.delegate")) return Denied<RequestPersonChoice[]>();
        if (!Page(page, size) || prefix.Length > 100 || prefix.Any(char.IsControl)) return Invalid<RequestPersonChoice[]>();
        var self = await people.ResolveAsync(actorId, scope.WorkspaceId, ct);
        if (self is null) return Denied<RequestPersonChoice[]>();
        var grants = FinanceGrantQueries.Current(db, scope, "request.create", clock.GetUtcNow());
        var candidates = (from g in grants
                          join u in db.Set<IdentityUser>().AsNoTracking() on g.RecipientId equals u.Id
                          where caps.Contains("request.delegate") || g.RecipientPersonId == self
                          select new { g.RecipientPersonId }).Distinct().OrderBy(x => x.RecipientPersonId);
        var rows = new List<RequestPersonChoice>(); long seen = 0; var skip = (long)(page - 1) * size;
        for (var offset = 0; rows.Count < size; offset = checked(offset + 100))
        {
            var batch = await candidates.Skip(offset).Take(100).ToArrayAsync(ct);
            foreach (var c in batch)
            {
                if (!await drafts.OwnerEligibleAsync(scope, c.RecipientPersonId, ct)) continue;
                // Resolve a current certified alias for a display label; never return raw identity catalogs.
                var accounts = from g in grants
                               join u in db.Set<IdentityUser>() on g.RecipientId equals u.Id
                               where g.RecipientPersonId == c.RecipientPersonId && u.Username.StartsWith(prefix)
                               orderby u.Id
                               select new { u.Id, u.Username };
                string? name = null;
                for (var accountOffset = 0; name is null; accountOffset = checked(accountOffset + 100))
                {
                    var aliases = await accounts.Skip(accountOffset).Take(100).ToArrayAsync(ct);
                    foreach (var a in aliases) if (await people.ResolveAsync(a.Id, scope.WorkspaceId, ct) == c.RecipientPersonId) { name = a.Username; break; }
                    if (aliases.Length < 100) break;
                }
                if (name is null || seen++ < skip) continue;
                rows.Add(new(c.RecipientPersonId, name)); if (rows.Count == size) break;
            }
            if (batch.Length < 100) break;
        }
        if (!(await Allowed(actorId, scope, ct)).SequenceEqual(caps)) return Denied<RequestPersonChoice[]>();
        foreach (var row in rows) if (!await drafts.OwnerEligibleAsync(scope, row.Id, ct)) return Denied<RequestPersonChoice[]>();
        return new(200, "ok", rows.ToArray());
    }, ct);
}
