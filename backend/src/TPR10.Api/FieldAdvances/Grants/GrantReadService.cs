using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Organization.Data;
using TPR10.Api.Scopes;

namespace TPR10.Api.FieldAdvances.Grants;

public sealed record GrantScopeChoice(FinanceScope Scope, string WorkspaceName, string? ProjectName, string[] Capabilities);
public sealed record GrantPersonChoice(Guid Id, string Name);
public sealed record GrantHistory(Guid ProposalId, string Outcome, Guid ActorId, DateTimeOffset At, string Reason);
public sealed record GrantRecord(Guid Id, string State, long Version, GrantWireSpec Spec, Guid ProposedBy,
    string RecipientName, Guid? AssignmentId, long? AssignmentVersion, string EffectiveState,
    bool CanReview, bool CanApprove, bool CanRevoke);

// Every read owns the identity lock through both the query and the final authorization check.
public sealed class GrantReadService(Tpr10DbContext db, FinanceAccess access, GrantService grants,
    FinancePersonResolver people, ScopeAccess sessions, RequestSession session, TimeProvider clock)
{
    private static readonly string[] Management = ["grant.propose", "grant.review", "grant.revoke"];
    private Guid Actor => session.Entity?.UserId ?? Guid.Empty;
    private static FinanceResult<T> Denied<T>() => new(403, "finance-access-denied", default);
    private static FinanceResult<T> Invalid<T>() => new(400, "invalid-request", default);
    private static bool Page(int page, int size) => page > 0 && size is > 0 and <= 100 && (long)(page - 1) * size <= int.MaxValue;
    private async Task<string[]> Capabilities(FinanceScope scope, CancellationToken ct)
    {
        var result = new List<string>();
        foreach (var c in Management) if ((await access.CheckAsync(Actor, scope, c, ct)).Allowed) result.Add(c);
        return result.ToArray();
    }
    private async Task<bool> Readable(GrantProposal p, bool queue, CancellationToken ct)
    {
        var spec = GrantService.Spec(p);
        foreach (var c in queue ? ["grant.review"] : Management)
            if ((await access.CheckAsync(Actor, spec.Scope, c, ct)).Allowed && await grants.CoversAsync(Actor, spec, c, false, ct)) return true;
        return false;
    }
    private async Task<FinanceResult<T>> Locked<T>(Func<Task<FinanceResult<T>>> read, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct);
        var identity = await sessions.ValidateSessionAsync(true, ct);
        if (Actor == Guid.Empty || identity.Status is not null || identity.ActorId != Actor) return Denied<T>();
        var result = await read();
        if ((await sessions.ValidateSessionAsync(true, ct)).Status is not null) return Denied<T>();
        await tx.CommitAsync(ct);
        return result;
    }
    public Task<FinanceResult<GrantScopeChoice[]>> ScopesAsync(int page, int size, CancellationToken ct) => Locked(async () =>
    {
        if (!Page(page, size)) return Invalid<GrantScopeChoice[]>();
        var candidates = await FinanceScopeQueries.Candidates(db, Actor).ToArrayAsync(ct);
        var allowed = await access.CheckManyAsync(Actor, candidates.Select(x => new FinanceScope(x.WorkspaceId, x.ProjectId)).ToArray(), Management, ct);
        var rows = new List<GrantScopeChoice>();
        foreach (var c in candidates)
        {
            var scope = new FinanceScope(c.WorkspaceId, c.ProjectId);
            var caps = allowed[scope];
            if (caps.Length == 0) continue;
            rows.Add(new(scope, c.WorkspaceName, c.ProjectName, caps));
        }
        var final = await access.CheckManyAsync(Actor, rows.Select(x => x.Scope).ToArray(), Management, ct);
        foreach (var row in rows) if (!final[row.Scope].SequenceEqual(row.Capabilities)) return Denied<GrantScopeChoice[]>();
        return new(200, "ok", rows.Skip((page - 1) * size).Take(size).ToArray());
    }, ct);
    public Task<FinanceResult<GrantPersonChoice[]>> RecipientsAsync(FinanceScope scope, string prefix, int page, int size, CancellationToken ct) => Locked(async () =>
    {
        if (!(await access.CheckAsync(Actor, scope, "grant.propose", ct)).Allowed) return Denied<GrantPersonChoice[]>();
        if (!Page(page, size) || prefix.Length > 100 || prefix.Any(char.IsControl)) return Invalid<GrantPersonChoice[]>();
        var self = await people.ResolveAsync(Actor, scope.WorkspaceId, ct);
        if (self is null) return Denied<GrantPersonChoice[]>();
        var search = prefix.Trim().ToUpperInvariant();
        var candidates = await (from u in db.Set<IdentityUser>().AsNoTracking()
                                where u.IsActive && u.NormalizedUsername.StartsWith(search)
                                    && db.Set<FinanceAccountLink>().Any(l => l.UserId == u.Id && l.WorkspaceId == scope.WorkspaceId && l.EndedAtUtc == null)
                                orderby u.NormalizedUsername, u.Id
                                select new GrantPersonChoice(u.Id, u.Username)).ToArrayAsync(ct);
        var rows = new List<GrantPersonChoice>();
        foreach (var c in candidates)
            if (await people.ResolveAsync(c.Id, scope.WorkspaceId, ct) is { } person && person != self) rows.Add(c);
        var selected = rows.Skip((page - 1) * size).Take(size).ToArray();
        if (!(await access.CheckAsync(Actor, scope, "grant.propose", ct)).Allowed) return Denied<GrantPersonChoice[]>();
        foreach (var c in selected)
            if (await people.ResolveAsync(c.Id, scope.WorkspaceId, ct) is not { } person || person == self) return Denied<GrantPersonChoice[]>();
        return new(200, "ok", selected);
    }, ct);
    public Task<FinanceResult<GrantRecord[]>> ListAsync(FinanceScope scope, bool queue, int page, int size, CancellationToken ct) => Locked(async () =>
    {
        var caps = await Capabilities(scope, ct);
        if (caps.Length == 0 || queue && !caps.Contains("grant.review")) return Denied<GrantRecord[]>();
        if (!Page(page, size)) return Invalid<GrantRecord[]>();
        var candidates = await db.Set<GrantProposal>().AsNoTracking().Where(x => x.WorkspaceId == scope.WorkspaceId && x.ProjectId == scope.ProjectId && (!queue || x.State == "Pending"))
            .OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id).ToArrayAsync(ct);
        var covered = await grants.CoveredAsync(Actor, scope, candidates.Select(GrantService.Spec).ToArray(), queue ? ["grant.review"] : caps, ct);
        var selected = candidates.Where((_, i) => covered[i]).Skip((page - 1) * size).Take(size).ToArray();
        var rows = await Records(scope, selected, ct);
        if (!(await Capabilities(scope, ct)).SequenceEqual(caps)) return Denied<GrantRecord[]>();
        var final = await grants.CoveredAsync(Actor, scope, selected.Select(GrantService.Spec).ToArray(), queue ? ["grant.review"] : caps, ct);
        if (final.Any(x => !x)) return Denied<GrantRecord[]>();
        return new(200, "ok", rows);
    }, ct);
    private Task<GrantProposal?> Find(FinanceScope scope, Guid id, CancellationToken ct) => db.Set<GrantProposal>().AsNoTracking()
        .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == scope.WorkspaceId && x.ProjectId == scope.ProjectId, ct);
    public Task<FinanceResult<GrantRecord>> DetailsAsync(FinanceScope scope, Guid id, CancellationToken ct) => Locked(async () =>
    {
        var p = await Find(scope, id, ct);
        if (p is null || !await Readable(p, false, ct)) return Denied<GrantRecord>();
        var row = await Record(p, ct);
        if (!await Readable(p, false, ct)) return Denied<GrantRecord>();
        return new(200, "ok", row);
    }, ct);
    public Task<FinanceResult<GrantHistory[]>> HistoryAsync(FinanceScope scope, Guid id, int page, int size, CancellationToken ct) => Locked(async () =>
    {
        var p = await Find(scope, id, ct);
        if (p is null || !await Readable(p, false, ct)) return Denied<GrantHistory[]>();
        if (!Page(page, size)) return Invalid<GrantHistory[]>();
        var events = new List<GrantHistory> { new(p.Id, "Proposed", p.ProposedBy, p.CreatedAtUtc, p.Reason) };
        var reviews = await db.Set<GrantReview>().AsNoTracking().Where(x => x.ProposalId == p.Id && x.WorkspaceId == p.WorkspaceId).OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).ToArrayAsync(ct);
        events.AddRange(reviews.Select(x => new GrantHistory(p.Id, x.Outcome, x.ReviewerId, x.CreatedAtUtc, x.Reason)));
        var assignment = await Assignment(p, ct);
        if (assignment?.RevokedAtUtc is { } at) events.Add(new(p.Id, "Revoked", assignment.RevokedBy!.Value, at, assignment.RevocationReason!));
        if (!await Readable(p, false, ct)) return Denied<GrantHistory[]>();
        return new(200, "ok", events.Skip((page - 1) * size).Take(size).ToArray());
    }, ct);
    private Task<GrantAssignment?> Assignment(GrantProposal p, CancellationToken ct) => db.Set<GrantAssignment>().AsNoTracking()
        .SingleOrDefaultAsync(x => x.ProposalId == p.Id && x.WorkspaceId == p.WorkspaceId && x.ProjectId == p.ProjectId, ct);
    private async Task<GrantRecord[]> Records(FinanceScope scope, GrantProposal[] proposals, CancellationToken ct)
    {
        var ids = proposals.Select(x => x.Id).ToArray();
        var assignments = await db.Set<GrantAssignment>().AsNoTracking().Where(x => ids.Contains(x.ProposalId)
            && x.WorkspaceId == scope.WorkspaceId && x.ProjectId == scope.ProjectId).ToDictionaryAsync(x => x.ProposalId, ct);
        var recipients = proposals.Select(x => x.RecipientId).Distinct().ToArray();
        var names = await db.Set<IdentityUser>().AsNoTracking().Where(x => recipients.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Username, ct);
        var identities = await people.ResolveManyAsync(proposals.SelectMany(x => new[] { x.RecipientId, x.ProposedBy }).Append(Actor)
            .Distinct().Select(x => (x, scope.WorkspaceId)).ToArray(), ct);
        var sources = await grants.CoverageSourcesManyAsync(proposals.Select(x => x.ProposedBy).Append(Actor).ToArray(), scope, ct);
        var caps = (await access.CheckManyAsync(Actor, [scope], Management, ct))[scope];
        var now = clock.GetUtcNow();
        return proposals.Select(p =>
        {
            var spec = GrantService.Spec(p);
            assignments.TryGetValue(p.Id, out var a);
            var state = a?.RevokedAtUtc is not null ? "Revoked" : p.State != "Approved" ? p.State
                : a is null ? "Unavailable" : p.EndsAtUtc <= now ? "Expired" : p.StartsAtUtc > now ? "Scheduled" : "Active";
            var separated = identities.TryGetValue((Actor, scope.WorkspaceId), out var actor) && actor != p.RecipientPersonId
                && actor != p.ProposedByPersonId && p.RecipientPersonId != p.ProposedByPersonId
                && identities.TryGetValue((p.RecipientId, scope.WorkspaceId), out var recipient) && recipient == p.RecipientPersonId
                && identities.TryGetValue((p.ProposedBy, scope.WorkspaceId), out var proposer) && proposer == p.ProposedByPersonId;
            var review = p.State == "Pending" && separated && caps.Contains("grant.review")
                && GrantService.Covers(sources[Actor], spec, "grant.review", false);
            var approve = review && !(spec.EndsAt <= now) && GrantService.Covers(sources[Actor], spec, "grant.review", true)
                && GrantService.Covers(sources[p.ProposedBy], spec, "grant.propose", true);
            var revoke = a is { RevokedAtUtc: null } && caps.Contains("grant.revoke")
                && GrantService.Covers(sources[Actor], spec, "grant.revoke", false);
            return new GrantRecord(p.Id, p.State, p.Version, GrantWireSpec.From(spec), p.ProposedBy, names[p.RecipientId],
                a?.Id, a?.Version, state, review, approve, revoke);
        }).ToArray();
    }

    private async Task<GrantRecord> Record(GrantProposal p, CancellationToken ct)
    {
        var spec = GrantService.Spec(p);
        var a = await Assignment(p, ct);
        var now = clock.GetUtcNow();
        var state = a?.RevokedAtUtc is not null ? "Revoked" : p.State != "Approved" ? p.State
            : a is null ? "Unavailable" : p.EndsAtUtc <= now ? "Expired" : p.StartsAtUtc > now ? "Scheduled" : "Active";
        var actor = await people.ResolveAsync(Actor, p.WorkspaceId, ct);
        var separated = actor is not null && actor != p.RecipientPersonId && actor != p.ProposedByPersonId && p.RecipientPersonId != p.ProposedByPersonId
            && await people.ResolveAsync(p.RecipientId, p.WorkspaceId, ct) == p.RecipientPersonId
            && await people.ResolveAsync(p.ProposedBy, p.WorkspaceId, ct) == p.ProposedByPersonId;
        var review = p.State == "Pending" && separated && (await access.CheckAsync(Actor, spec.Scope, "grant.review", ct)).Allowed
            && await grants.CoversAsync(Actor, spec, "grant.review", false, ct);
        var approve = review && await grants.CoversAsync(Actor, spec, "grant.review", true, ct)
            && await grants.CoversAsync(p.ProposedBy, spec, "grant.propose", true, ct);
        var revoke = a is { RevokedAtUtc: null } && (await access.CheckAsync(Actor, spec.Scope, "grant.revoke", ct)).Allowed
            && await grants.CoversAsync(Actor, spec, "grant.revoke", false, ct);
        var name = await db.Set<IdentityUser>().AsNoTracking().Where(x => x.Id == p.RecipientId).Select(x => x.Username).SingleAsync(ct);
        return new(p.Id, p.State, p.Version, GrantWireSpec.From(spec), p.ProposedBy, name, a?.Id, a?.Version, state, review, approve, revoke);
    }
}
