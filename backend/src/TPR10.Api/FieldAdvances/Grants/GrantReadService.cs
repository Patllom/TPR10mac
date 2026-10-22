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
        var candidates = await db.Set<GrantProposal>().AsNoTracking().Where(x => x.RecipientId == Actor)
            .Select(x => new { x.WorkspaceId, x.ProjectId }).Distinct().OrderBy(x => x.WorkspaceId).ThenBy(x => x.ProjectId).ToArrayAsync(ct);
        var rows = new List<GrantScopeChoice>();
        foreach (var c in candidates)
        {
            var scope = new FinanceScope(c.WorkspaceId, c.ProjectId);
            var caps = await Capabilities(scope, ct);
            if (caps.Length == 0) continue;
            var company = await db.Set<Workspace>().AsNoTracking().SingleAsync(x => x.Id == c.WorkspaceId, ct);
            var project = c.ProjectId is null ? null : await db.Set<Project>().AsNoTracking().SingleAsync(x => x.Id == c.ProjectId && x.WorkspaceId == c.WorkspaceId, ct);
            rows.Add(new(scope, company.Name, project?.Name, caps));
        }
        foreach (var row in rows) if (!(await Capabilities(row.Scope, ct)).SequenceEqual(row.Capabilities)) return Denied<GrantScopeChoice[]>();
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
        var rows = new List<GrantRecord>();
        foreach (var p in selected) rows.Add(await Record(p, ct));
        if (!(await Capabilities(scope, ct)).SequenceEqual(caps)) return Denied<GrantRecord[]>();
        foreach (var p in selected) if (!await Readable(p, queue, ct)) return Denied<GrantRecord[]>();
        return new(200, "ok", rows.ToArray());
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
