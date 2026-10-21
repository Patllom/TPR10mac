using System.Security.Cryptography;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;

namespace TPR10.Api.FieldAdvances.Policies;

public sealed class PolicyService(Tpr10DbContext db, FinanceAccess access, FinancePersonResolver people,
    IAuditEventWriter audit, TimeProvider clock)
{
    public Task<FinanceResult<PolicyView>> CreateDraftAsync(Guid actorId, FinanceScope scope, Guid? baseCompanyVersionId,
        PolicyContent content, CommandMeta meta, CancellationToken ct) => Execute(async () =>
    {
        if (!Valid(meta) || meta.ExpectedVersion != 1 || !ValidScope(scope)
            || !ValidContent(content) || !ValidMode(scope, meta.Policy!.Mode, baseCompanyVersionId)
            || meta.Policy.BaseCompanyVersionId != baseCompanyVersionId) return Invalid();
        content = Copy(content);
        var capability = EditCapability(scope);
        if (!await Authorized(actorId, scope, capability, ct)) return Denied();
        var hash = Hash(new { scope, baseCompanyVersionId, content, meta });
        if (await Replay(actorId, scope, "policy.create", meta, hash, ct) is { } replay) return replay;
        if (!await ActiveMatches(scope, meta.Policy!, ct) || !await ValidBase(scope, meta.Policy.Mode, baseCompanyVersionId, content, ct)) return Conflict();
        PolicyRevision? source = null;
        if (meta.Policy.SourcePolicyRevisionId is { } sourceId)
        {
            source = await db.Set<PolicyRevision>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == sourceId
                && x.WorkspaceId == scope.WorkspaceId && x.ProjectId == scope.ProjectId, ct);
            if (source is null) return Denied();
        }
        else if (await db.Set<PolicyRevision>().AnyAsync(x => x.WorkspaceId == scope.WorkspaceId && x.ProjectId == scope.ProjectId, ct))
            return Invalid();
        var person = (await people.ResolveAsync(actorId, scope.WorkspaceId, ct))!.Value;
        var row = new PolicyRevision
        {
            Id = Guid.NewGuid(),
            WorkspaceId = scope.WorkspaceId,
            ProjectId = scope.ProjectId,
            BaseCompanyVersionId = baseCompanyVersionId,
            Mode = meta.Policy.Mode,
            SourcePolicyRevisionId = source?.Id,
            ExpectedActiveRevisionId = meta.Policy.ExpectedActiveRevisionId,
            ExpectedActiveVersion = meta.Policy.ExpectedActiveVersion,
            State = "Draft",
            ClearingDays = content.ClearingDays,
            ContentHash = ContentHash(scope, meta.Policy.Mode, baseCompanyVersionId, content),
            CreatedBy = actorId,
            CreatedByPersonId = person,
            CreatedAtUtc = clock.GetUtcNow(),
            Reason = meta.Reason.Trim()
        };
        db.Add(row);
        SetContent(row, content);
        // Company base authors are not contributors to a separate project-policy lineage.
        if (source is not null)
            foreach (var c in await LineageContributors(source, ct)) AddContributor(row, c.Person, c.User, "สืบผู้ร่วมแก้จากฉบับต้นทาง");
        AddContributor(row, person, actorId, meta.Reason);
        return await Finish(actorId, row, "policy.create", "created", meta, hash, content, 201,
            () => AuthorizedPerson(actorId, scope, capability, person, ct), ct);
    }, ct);

    public Task<FinanceResult<PolicyView>> UpdateDraftAsync(Guid actorId, Guid id, PolicyContent content,
        CommandMeta meta, CancellationToken ct) => Execute(async () =>
    {
        if (!Valid(meta) || id == Guid.Empty || !ValidContent(content)) return Invalid();
        content = Copy(content);
        var row = await Find(id, ct);
        if (row is null) return Denied();
        var scope = Scope(row);
        var capability = EditCapability(scope);
        if (!await Authorized(actorId, scope, capability, ct)) return Denied();
        var hash = Hash(new { id, content, meta });
        if (await Replay(actorId, scope, "policy.update", meta, hash, ct) is { } replay) return replay;
        if (row.State is not ("Draft" or "Returned") || !VersionMatches(row, meta)) return Conflict();
        var options = meta.Policy!;
        if (options.SourcePolicyRevisionId != row.SourcePolicyRevisionId || !ValidMode(scope, options.Mode, options.BaseCompanyVersionId)) return Invalid();
        if (!await ActiveMatches(scope, options, ct) || !await ValidBase(scope, options.Mode, options.BaseCompanyVersionId, content, ct)) return Conflict();
        db.Attach(row);
        row.Mode = options.Mode; row.BaseCompanyVersionId = options.BaseCompanyVersionId;
        row.ExpectedActiveRevisionId = options.ExpectedActiveRevisionId; row.ExpectedActiveVersion = options.ExpectedActiveVersion;
        row.ClearingDays = content.ClearingDays; row.ContentHash = ContentHash(scope, options.Mode, row.BaseCompanyVersionId, content);
        row.State = "Draft"; row.Version++;
        // Remove old composite keys before adding their replacements, within the same transaction.
        db.RemoveRange(await db.Set<PolicyApprovalBand>().Where(x => x.PolicyRevisionId == id).ToArrayAsync(ct));
        db.RemoveRange(await db.Set<PolicyCategoryRule>().Where(x => x.PolicyRevisionId == id).ToArrayAsync(ct));
        await db.SaveChangesAsync(ct);
        SetContent(row, content);
        var person = (await people.ResolveAsync(actorId, scope.WorkspaceId, ct))!.Value;
        AddContributor(row, person, actorId, meta.Reason);
        return await Finish(actorId, row, "policy.update", "updated", meta, hash, content, 200,
            () => AuthorizedPerson(actorId, scope, capability, person, ct), ct);
    }, ct);

    public Task<FinanceResult<PolicyView>> SubmitAsync(Guid actorId, Guid id, CommandMeta meta, CancellationToken ct) => Execute(async () =>
    {
        if (!Valid(meta) || id == Guid.Empty) return Invalid();
        var row = await Find(id, ct);
        if (row is null) return Denied();
        var scope = Scope(row);
        var capability = EditCapability(scope);
        if (!await Authorized(actorId, scope, capability, ct)) return Denied();
        var hash = Hash(new { id, meta });
        if (await Replay(actorId, scope, "policy.submit", meta, hash, ct) is { } replay) return replay;
        if (row.State is not ("Draft" or "Returned") || !SeenMatches(row, meta)) return Conflict();
        var content = await Content(row, ct);
        if (!ValidContent(content)) return Invalid();
        if (!await ActiveMatches(scope, meta.Policy!, ct) || !await ValidBase(scope, Mode(row), row.BaseCompanyVersionId, content, ct)) return Conflict();
        db.Attach(row); row.State = "Pending"; row.Version++;
        var person = (await people.ResolveAsync(actorId, scope.WorkspaceId, ct))!.Value;
        return await Finish(actorId, row, "policy.submit", "submitted", meta, hash, content, 200,
            () => AuthorizedPerson(actorId, scope, capability, person, ct), ct);
    }, ct);

    public Task<FinanceResult<PolicyView>> ReviewAsync(Guid actorId, Guid id, string decision, CommandMeta meta, CancellationToken ct) => Execute(async () =>
    {
        if (!Valid(meta) || id == Guid.Empty || decision is not ("approve" or "return" or "reject")) return Invalid();
        var row = await Find(id, ct);
        if (row is null) return Denied();
        var scope = Scope(row);
        if (!await Authorized(actorId, scope, "policy.review", ct)) return Denied();
        var hash = Hash(new { id, decision, meta });
        if (await Replay(actorId, scope, "policy.review", meta, hash, ct) is { } replay) return replay;
        if (row.State != "Pending" || !SeenMatches(row, meta)) return Conflict();
        var person = (await people.ResolveAsync(actorId, scope.WorkspaceId, ct))!.Value;
        async Task<bool> CanReview() => await AuthorizedPerson(actorId, scope, "policy.review", person, ct)
            && !(await LineageContributors(row, ct)).Any(x => x.Person == person);
        if (!await CanReview()) return Denied();
        var content = await Content(row, ct);
        if (!ValidContent(content) || row.ContentHash != ContentHash(scope, Mode(row), row.BaseCompanyVersionId, content)) return Conflict();
        if (decision == "approve")
        {
            if (!await ActiveMatches(scope, meta.Policy!, ct) || !await ValidBase(scope, Mode(row), row.BaseCompanyVersionId, content, ct)) return Conflict();
            var active = await db.Set<ActivePolicy>().SingleOrDefaultAsync(x => x.WorkspaceId == scope.WorkspaceId && x.ProjectId == scope.ProjectId, ct);
            if (active?.Version == long.MaxValue) return Conflict();
            if (active is not null)
            {
                var old = await db.Set<PolicyRevision>().SingleAsync(x => x.Id == active.PolicyRevisionId, ct);
                if (old.State != "Active" || old.Version == long.MaxValue) return Conflict();
                old.State = "Retired"; old.Version++;
                active.PolicyRevisionId = row.Id; active.Version++;
            }
            else db.Add(new ActivePolicy
            {
                Id = Guid.NewGuid(),
                WorkspaceId = scope.WorkspaceId,
                ProjectId = scope.ProjectId,
                PolicyRevisionId = row.Id,
                CreatedBy = actorId,
                CreatedAtUtc = clock.GetUtcNow()
            });
        }
        var outcome = decision switch { "approve" => "Approved", "return" => "Returned", _ => "Rejected" };
        db.Add(new PolicyReview
        {
            Id = Guid.NewGuid(),
            WorkspaceId = scope.WorkspaceId,
            PolicyRevisionId = id,
            ReviewerId = actorId,
            ReviewerPersonId = person,
            Outcome = outcome,
            ReviewedVersion = row.Version,
            ReviewedContentHash = row.ContentHash,
            ReviewedContentJson = JsonSerializer.Serialize(View(row, content)),
            CreatedAtUtc = clock.GetUtcNow(),
            Reason = meta.Reason.Trim()
        });
        db.Attach(row); row.State = decision == "approve" ? "Active" : outcome; row.Version++;
        return await Finish(actorId, row, "policy.review", outcome.ToLowerInvariant(), meta, hash, content, 200, CanReview, ct);
    }, ct);

    private async Task<FinanceResult<PolicyView>> Execute(Func<Task<FinanceResult<PolicyView>>> action, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var committed = false;
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct);
            var result = await action();
            if (result.Status is >= 200 and < 300)
            {
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); committed = true;
            }
            return result;
        }
        catch (DbUpdateConcurrencyException) { return Conflict(); }
        finally { if (!committed) db.ChangeTracker.Clear(); }
    }
    private async Task<FinanceResult<PolicyView>> Finish(Guid actor, PolicyRevision row, string action,
        string outcome, CommandMeta meta, string hash, PolicyContent content, int status, Func<Task<bool>> recheck, CancellationToken ct)
    {
        await audit.WriteAsync(new SecurityAuditRequest(actor, null, row.WorkspaceId, row.ProjectId, null,
            "field-advance.policy." + outcome, "field-advance-policy", row.Id, "success",
            new Dictionary<string, string> { ["reason"] = meta.Reason.Trim(), ["scope-validation"] = "authorized" }), ct);
        if (!await recheck()) return Denied();
        var result = new FinanceResult<PolicyView>(status, "policy-" + outcome, View(row, content));
        db.Add(new FinanceCommandReceipt
        {
            CommandId = meta.CommandId,
            WorkspaceId = row.WorkspaceId,
            ActorId = actor,
            Action = action,
            PayloadSha256 = hash,
            Status = status,
            ResultJson = JsonSerializer.Serialize(result),
            CreatedAtUtc = clock.GetUtcNow()
        });
        return result;
    }
    private async Task<FinanceResult<PolicyView>?> Replay(Guid actor, FinanceScope scope, string action, CommandMeta meta, string hash, CancellationToken ct)
    {
        var receipt = await db.Set<FinanceCommandReceipt>().AsNoTracking().SingleOrDefaultAsync(x => x.CommandId == meta.CommandId, ct);
        if (receipt is null) return null;
        if (receipt.ActorId != actor || receipt.WorkspaceId != scope.WorkspaceId || receipt.Action != action || receipt.PayloadSha256 != hash) return Conflict();
        return JsonSerializer.Deserialize<FinanceResult<PolicyView>>(receipt.ResultJson) ?? throw new InvalidOperationException("Invalid retained policy receipt.");
    }
    private async Task<bool> ValidBase(FinanceScope scope, string mode, Guid? id, PolicyContent content, CancellationToken ct)
    {
        if (scope.ProjectId is null) return id is null && mode == "Company";
        var basis = await db.Set<PolicyRevision>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == scope.WorkspaceId && x.ProjectId == null, ct);
        if (basis is null || basis.State is not ("Active" or "Retired")) return false;
        if (mode == "Override") return true;
        return mode == "Inherit" && await db.Set<ActivePolicy>().AnyAsync(x => x.WorkspaceId == scope.WorkspaceId && x.ProjectId == null && x.PolicyRevisionId == id, ct)
            && Hash(Copy(await Content(basis, ct))) == Hash(Copy(content));
    }
    private async Task<bool> ActiveMatches(FinanceScope scope, PolicyCommandOptions options, CancellationToken ct)
    {
        var active = await db.Set<ActivePolicy>().AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == scope.WorkspaceId && x.ProjectId == scope.ProjectId, ct);
        return active is null ? options.ExpectedActiveRevisionId is null && options.ExpectedActiveVersion == 0
            : active.PolicyRevisionId == options.ExpectedActiveRevisionId && active.Version == options.ExpectedActiveVersion;
    }
    private Task<PolicyRevision?> Find(Guid id, CancellationToken ct) => db.Set<PolicyRevision>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    private async Task<bool> Authorized(Guid actor, FinanceScope scope, string capability, CancellationToken ct) => (await access.CheckAsync(actor, scope, capability, ct)).Allowed;
    private async Task<bool> AuthorizedPerson(Guid actor, FinanceScope scope, string capability, Guid person, CancellationToken ct) =>
        await Authorized(actor, scope, capability, ct) && await people.ResolveAsync(actor, scope.WorkspaceId, ct) == person;
    private async Task<List<(Guid Person, Guid User)>> LineageContributors(PolicyRevision row, CancellationToken ct)
    {
        // Include sibling/descendant revisions too: branching from an older revision cannot erase editors.
        // The query is exact-scope, so a project's company base never enters this lineage.
        var revisions = await db.Set<PolicyRevision>().AsNoTracking()
            .Where(x => x.WorkspaceId == row.WorkspaceId && x.ProjectId == row.ProjectId).ToDictionaryAsync(x => x.Id, ct);
        var visited = new HashSet<Guid>();
        var root = row;
        while (root.SourcePolicyRevisionId is { } parent)
        {
            if (!visited.Add(root.Id) || !revisions.TryGetValue(parent, out var ancestor))
                throw new InvalidOperationException("Invalid policy lineage.");
            root = ancestor;
        }
        var lineage = new HashSet<Guid> { root.Id };
        bool added;
        do
        {
            added = false;
            foreach (var revision in revisions.Values)
                if (revision.SourcePolicyRevisionId is { } parent && lineage.Contains(parent))
                    added |= lineage.Add(revision.Id);
        } while (added);
        var result = revisions.Values.Where(x => lineage.Contains(x.Id)).Select(x => (x.CreatedByPersonId, x.CreatedBy)).ToList();
        var contributors = await db.Set<PolicyContributor>().AsNoTracking()
            .Where(x => x.WorkspaceId == row.WorkspaceId && lineage.Contains(x.PolicyRevisionId)).ToArrayAsync(ct);
        result.AddRange(contributors.Select(x => (x.PersonId, x.UserId)));
        return result.DistinctBy(x => x.Item1).ToList();
    }
    private void AddContributor(PolicyRevision row, Guid person, Guid user, string reason)
    {
        if (db.Set<PolicyContributor>().Local.Any(x => x.PolicyRevisionId == row.Id && x.PersonId == person)) return;
        db.Add(new PolicyContributor
        {
            Id = Guid.NewGuid(),
            WorkspaceId = row.WorkspaceId,
            PolicyRevisionId = row.Id,
            PersonId = person,
            UserId = user,
            CreatedAtUtc = clock.GetUtcNow(),
            Reason = reason.Trim()
        });
    }
    private void SetContent(PolicyRevision row, PolicyContent content)
    {
        db.AddRange(content.Bands.Select(x => new PolicyApprovalBand { PolicyRevisionId = row.Id, Order = x.Order, LevelCode = x.LevelCode, UpperInclusive = x.UpperInclusive }));
        db.AddRange(content.Categories.Select(x => new PolicyCategoryRule { PolicyRevisionId = row.Id, Id = x.Id, Name = x.Name, Active = x.Active, Unit = x.Unit, StandardRate = x.StandardRate, EvidenceTypes = x.EvidenceTypes }));
    }
    private Task<PolicyContent> Content(PolicyRevision row, CancellationToken ct) =>
        PolicyReadData.Content(db, row, ct);
    // PostgreSQL numeric(18,2) adds trailing zeroes. Canonicalize scale without rounding values.
    private static decimal CanonicalMoney(decimal value) => decimal.Parse(value.ToString("G29", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    internal static PolicyContent Copy(PolicyContent content) => new(content.ClearingDays,
        content.Bands.Select(x => x with { UpperInclusive = CanonicalMoney(x.UpperInclusive) }).ToArray(),
        content.Categories.OrderBy(x => x.Id).Select(x => x with
        { StandardRate = x.StandardRate is { } rate ? CanonicalMoney(rate) : null, EvidenceTypes = [.. x.EvidenceTypes.Order(StringComparer.Ordinal)] }).ToArray());
    private static string Mode(PolicyRevision row) => row.Mode ?? (row.ProjectId is null ? "Company" : "Override");
    private static FinanceScope Scope(PolicyRevision row) => new(row.WorkspaceId, row.ProjectId);
    private static string EditCapability(FinanceScope scope) => scope.ProjectId is null ? "policy.edit.company" : "policy.edit.project";
    private static bool ValidScope(FinanceScope? scope) => scope is not null && scope.WorkspaceId != Guid.Empty && scope.ProjectId != Guid.Empty;
    private static bool ValidContent(PolicyContent content) => PolicyValidation.Validate(content).Length == 0;
    private static bool ValidMode(FinanceScope scope, string mode, Guid? basis) => scope.ProjectId is null ? mode == "Company" && basis is null
        : mode is "Override" or "Inherit" && basis is not null && basis != Guid.Empty;
    private static bool Valid(CommandMeta? meta) => meta is not null && meta.CommandId != Guid.Empty
        && meta.ExpectedVersion >= 1 && !string.IsNullOrWhiteSpace(meta.Reason) && meta.Reason.Length <= 1000 && !meta.Reason.Any(char.IsControl)
        && meta.Policy is { } p && p.Mode is "Company" or "Override" or "Inherit"
        && p.SourcePolicyRevisionId != Guid.Empty && p.BaseCompanyVersionId != Guid.Empty && p.ExpectedActiveRevisionId != Guid.Empty
        && (p.ExpectedActiveRevisionId is null ? p.ExpectedActiveVersion == 0 : p.ExpectedActiveVersion >= 1);
    private static bool VersionMatches(PolicyRevision row, CommandMeta meta) => row.Version == meta.ExpectedVersion && row.Version < long.MaxValue && row.ContentHash == meta.Policy!.ExpectedContentHash;
    private static bool SeenMatches(PolicyRevision row, CommandMeta meta) => VersionMatches(row, meta)
        && Mode(row) == meta.Policy!.Mode && row.BaseCompanyVersionId == meta.Policy.BaseCompanyVersionId
        && row.SourcePolicyRevisionId == meta.Policy.SourcePolicyRevisionId
        && row.ExpectedActiveRevisionId == meta.Policy.ExpectedActiveRevisionId && row.ExpectedActiveVersion == meta.Policy.ExpectedActiveVersion;
    private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    internal static string ContentHash(FinanceScope scope, string mode, Guid? basis, PolicyContent content) => Hash(new { scope, mode, basis, content = Copy(content) });
    private static PolicyView View(PolicyRevision row, PolicyContent content) => new(row.Id, Scope(row), row.State, row.Version, row.BaseCompanyVersionId, content, row.ContentHash)
    { Mode = Mode(row), SourcePolicyRevisionId = row.SourcePolicyRevisionId, ExpectedActiveRevisionId = row.ExpectedActiveRevisionId, ExpectedActiveVersion = row.ExpectedActiveVersion };
    private static FinanceResult<PolicyView> Invalid() => new(400, "policy-invalid", null);
    private static FinanceResult<PolicyView> Denied() => new(403, "finance-access-denied", null);
    private static FinanceResult<PolicyView> Conflict() => new(409, "policy-conflict", null);
}
