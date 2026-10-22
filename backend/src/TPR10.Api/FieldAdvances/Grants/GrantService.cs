using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;

namespace TPR10.Api.FieldAdvances.Grants;

public sealed class GrantService(Tpr10DbContext db, FinanceAccess access, FinancePersonResolver people,
    IAuditEventWriter audit, TimeProvider clock)
{
    public Task<FinanceResult<GrantView>> ProposeAsync(Guid actorId, GrantSpec spec, CommandMeta meta, CancellationToken ct) =>
        ExecuteAsync(async () =>
        {
            if (!Valid(meta) || meta.ExpectedVersion != 1 || !Valid(spec)) return Invalid();
            // Own the arrays throughout the async operation; caller changes cannot alter checked content.
            spec = spec with { Capabilities = [.. spec.Capabilities], DelegableCapabilities = [.. spec.DelegableCapabilities] };
            if (!(await access.CheckAsync(actorId, spec.Scope, "grant.propose", ct)).Allowed) return Denied();
            var hash = Hash(new { spec, meta });
            if (await ReplayAsync(actorId, spec, "grant.propose", meta, hash, ct) is { } replay) return replay;
            var proposer = await people.ResolveAsync(actorId, spec.Scope.WorkspaceId, ct);
            var recipient = await people.ResolveAsync(spec.RecipientId, spec.Scope.WorkspaceId, ct);
            if (proposer is null || recipient is null || proposer == recipient) return Denied();
            if (!await CoversAsync(actorId, spec, "grant.propose", true, ct)) return Denied();
            var proposal = new GrantProposal
            {
                Id = Guid.NewGuid(),
                WorkspaceId = spec.Scope.WorkspaceId,
                ProjectId = spec.Scope.ProjectId,
                RecipientId = spec.RecipientId,
                RecipientPersonId = recipient.Value,
                ProposedBy = actorId,
                ProposedByPersonId = proposer.Value,
                State = "Pending",
                Capabilities = spec.Capabilities,
                DelegableCapabilities = spec.DelegableCapabilities,
                ApprovalLimit = spec.ApprovalLimit,
                StartsAtUtc = spec.StartsAt.ToUniversalTime(),
                EndsAtUtc = spec.EndsAt?.ToUniversalTime(),
                CreatedAtUtc = clock.GetUtcNow(),
                Reason = meta.Reason.Trim()
            };
            db.Add(proposal);
            return await FinishAsync(actorId, spec.Scope, proposal.Id, "grant.propose", "proposed", meta, hash,
                new(201, "grant-proposed", View(proposal)), async () =>
                    (await access.CheckAsync(actorId, spec.Scope, "grant.propose", ct)).Allowed
                    && await people.ResolveAsync(spec.RecipientId, spec.Scope.WorkspaceId, ct) == recipient
                    && await CoversAsync(actorId, spec, "grant.propose", true, ct), ct);
        }, ct);

    public Task<FinanceResult<GrantView>> ReviewAsync(Guid actorId, Guid proposalId, string decision, CommandMeta meta, CancellationToken ct) =>
        ExecuteAsync(async () =>
        {
            if (!Valid(meta) || proposalId == Guid.Empty || decision is not ("approve" or "return" or "reject")) return Invalid();
            var proposal = await db.Set<GrantProposal>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == proposalId, ct);
            // Same denial for unknown IDs and inaccessible scopes; never disclose another scope's state.
            if (proposal is null) return Denied();
            var spec = Spec(proposal);
            if (!(await access.CheckAsync(actorId, spec.Scope, "grant.review", ct)).Allowed) return Denied();
            var hash = Hash(new { proposalId, decision, meta });
            if (await ReplayAsync(actorId, spec, "grant.review", meta, hash, ct) is { } replay) return replay;
            if (proposal.State != "Pending" || proposal.Version != meta.ExpectedVersion || proposal.Version == long.MaxValue)
                return Conflict();
            async Task<bool> AuthorizedAsync()
            {
                if (!(await access.CheckAsync(actorId, spec.Scope, "grant.review", ct)).Allowed) return false;
                var reviewer = await people.ResolveAsync(actorId, spec.Scope.WorkspaceId, ct);
                var recipient = await people.ResolveAsync(proposal.RecipientId, spec.Scope.WorkspaceId, ct);
                var proposer = await people.ResolveAsync(proposal.ProposedBy, spec.Scope.WorkspaceId, ct);
                return reviewer is not null && recipient == proposal.RecipientPersonId && proposer == proposal.ProposedByPersonId
                    && reviewer != recipient && reviewer != proposer && proposer != recipient
                    && await CoversAsync(actorId, spec, "grant.review", decision == "approve", ct)
                    && (decision != "approve" || await CoversAsync(proposal.ProposedBy, spec, "grant.propose", true, ct));
            }
            if (!await AuthorizedAsync()) return Denied();
            var reviewerPerson = await people.ResolveAsync(actorId, spec.Scope.WorkspaceId, ct);
            var outcome = decision switch { "approve" => "Approved", "return" => "Returned", _ => "Rejected" };
            var review = new GrantReview
            {
                Id = Guid.NewGuid(),
                WorkspaceId = proposal.WorkspaceId,
                ProposalId = proposal.Id,
                ReviewerId = actorId,
                ReviewerPersonId = reviewerPerson!.Value,
                Outcome = outcome,
                CreatedAtUtc = clock.GetUtcNow(),
                Reason = meta.Reason.Trim()
            };
            db.Attach(proposal);
            proposal.State = decision == "return" ? "Draft" : outcome;
            proposal.Version++;
            db.Add(review);
            if (decision == "approve")
                db.Add(new GrantAssignment
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = proposal.WorkspaceId,
                    ProjectId = proposal.ProjectId,
                    ProposalId = proposal.Id,
                    ReviewId = review.Id,
                    CreatedBy = actorId,
                    CreatedAtUtc = clock.GetUtcNow(),
                    Reason = meta.Reason.Trim()
                });
            return await FinishAsync(actorId, spec.Scope, proposal.Id, "grant.review", outcome.ToLowerInvariant(), meta, hash,
                new(200, "grant-reviewed", View(proposal)), AuthorizedAsync, ct);
        }, ct);

    internal Task<FinanceResult<GrantView>> RevokeAsync(Guid actorId, Guid grantId, CommandMeta meta, CancellationToken ct) =>
        ExecuteAsync(async () =>
        {
            if (!Valid(meta) || grantId == Guid.Empty) return Invalid();
            var grant = await db.Set<GrantAssignment>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == grantId, ct);
            if (grant is null) return Denied();
            var proposal = await db.Set<GrantProposal>().AsNoTracking().SingleAsync(x => x.Id == grant.ProposalId, ct);
            var spec = Spec(proposal);
            if (!(await access.CheckAsync(actorId, spec.Scope, "grant.revoke", ct)).Allowed) return Denied();
            var hash = Hash(new { grantId, meta });
            if (await ReplayAsync(actorId, spec, "grant.revoke", meta, hash, ct) is { } replay) return replay;
            if (grant.RevokedAtUtc is not null || grant.Version != meta.ExpectedVersion || grant.Version == long.MaxValue)
                return Conflict();
            async Task<bool> AuthorizedAsync() =>
                (await access.CheckAsync(actorId, spec.Scope, "grant.revoke", ct)).Allowed
                && await CoversAsync(actorId, spec, "grant.revoke", false, ct);
            if (!await AuthorizedAsync()) return Denied();
            db.Attach(grant);
            grant.RevokedAtUtc = clock.GetUtcNow();
            grant.RevokedBy = actorId;
            grant.RevocationReason = meta.Reason.Trim();
            grant.Version++;
            return await FinishAsync(actorId, spec.Scope, grant.Id, "grant.revoke", "revoked", meta, hash,
                new(200, "grant-revoked", new(grant.Id, "Revoked", grant.Version, spec)), AuthorizedAsync, ct);
        }, ct);

    // All account/session, organization and grant mutations serialize on this existing lock.
    // A failed operation clears its tracked changes so reusing a context cannot save rolled-back state.
    private async Task<FinanceResult<GrantView>> ExecuteAsync(Func<Task<FinanceResult<GrantView>>> action, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var committed = false;
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct);
            var result = await action();
            if (result.Status is >= 200 and < 300)
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                committed = true;
            }
            return result;
        }
        finally
        {
            if (!committed) db.ChangeTracker.Clear();
        }
    }

    internal async Task<bool> CoversAsync(Guid actor, GrantSpec target, string operation, bool coverPeriod, CancellationToken ct)
    {
        var sources = await CoverageSources(actor, target.Scope, ct);
        if (coverPeriod && target.EndsAt <= clock.GetUtcNow()) return false;
        return Covers(sources, target, operation, coverPeriod);
    }

    // Batched filtering stays inside the reader's lock. The selected page is rechecked
    // with current authority before disclosure, including expiry during the read.
    internal async Task<bool[]> CoveredAsync(Guid actor, FinanceScope scope, GrantSpec[] targets, string[] operations, CancellationToken ct)
    {
        var sources = await CoverageSources(actor, scope, ct);
        return targets.Select(target => target.Scope == scope && operations.Any(operation => Covers(sources, target, operation, false))).ToArray();
    }

    private async Task<GrantProposal[]> CoverageSources(Guid actor, FinanceScope scope, CancellationToken ct)
    {
        var person = await people.ResolveAsync(actor, scope.WorkspaceId, ct);
        if (person is null) return [];
        var now = clock.GetUtcNow();
        return await (from assignment in db.Set<GrantAssignment>().AsNoTracking()
                      join proposal in db.Set<GrantProposal>() on assignment.ProposalId equals proposal.Id
                      join review in db.Set<GrantReview>() on assignment.ReviewId equals review.Id
                      where assignment.WorkspaceId == scope.WorkspaceId && assignment.ProjectId == scope.ProjectId
                         && proposal.WorkspaceId == assignment.WorkspaceId && proposal.ProjectId == assignment.ProjectId
                         && review.WorkspaceId == assignment.WorkspaceId && review.ProposalId == proposal.Id
                         && assignment.RevokedAtUtc == null && assignment.CreatedAtUtc <= now
                         && proposal.State == "Approved" && review.Outcome == "Approved"
                         && proposal.CreatedAtUtc <= now && review.CreatedAtUtc <= now
                         && proposal.RecipientId == actor && proposal.RecipientPersonId == person
                         && proposal.ProposedByPersonId != proposal.RecipientPersonId
                         && review.ReviewerPersonId != proposal.RecipientPersonId
                         && review.ReviewerPersonId != proposal.ProposedByPersonId
                         && proposal.StartsAtUtc <= now && (proposal.EndsAtUtc == null || now < proposal.EndsAtUtc)
                      select proposal).ToArrayAsync(ct);
    }

    private static bool Covers(GrantProposal[] sources, GrantSpec target, string operation, bool coverPeriod)
    {
        if (!sources.Any(x => x.Capabilities.Contains(operation))) return false;
        // Each capability needs one source covering the WHOLE amount and period.
        // Never sum amounts, combine an amount from one source with a capability from another,
        // or interpret a null monetary limit as unlimited.
        return target.Capabilities.Concat(target.DelegableCapabilities).Distinct().All(capability =>
            sources.Any(source => source.DelegableCapabilities.Contains(capability)
                && (target.ApprovalLimit is null || source.ApprovalLimit is { } limit && limit >= target.ApprovalLimit)
                && (!coverPeriod || source.EndsAtUtc is null || target.EndsAt is { } end && end <= source.EndsAtUtc)));
    }

    private async Task<FinanceResult<GrantView>?> ReplayAsync(Guid actor, GrantSpec target, string action,
        CommandMeta meta, string hash, CancellationToken ct)
    {
        var receipt = await db.Set<FinanceCommandReceipt>().AsNoTracking().SingleOrDefaultAsync(x => x.CommandId == meta.CommandId, ct);
        if (receipt is null) return null;
        if (receipt.ActorId != actor || receipt.WorkspaceId != target.Scope.WorkspaceId || receipt.Action != action || receipt.PayloadSha256 != hash)
            return Conflict();
        // Receipts disclose protected target data. Historical replay needs current coverage,
        // but not a still-pending target or authority covering its historical time period.
        if (!await CoversAsync(actor, target, action, false, ct)) return Denied();
        return JsonSerializer.Deserialize<FinanceResult<GrantView>>(receipt.ResultJson)
            ?? throw new InvalidOperationException("Invalid retained grant receipt.");
    }

    private async Task<FinanceResult<GrantView>> FinishAsync(Guid actor, FinanceScope scope, Guid targetId, string action,
        string eventName, CommandMeta meta, string hash, FinanceResult<GrantView> result, Func<Task<bool>> recheck, CancellationToken ct)
    {
        await audit.WriteAsync(new SecurityAuditRequest(actor, null, scope.WorkspaceId, scope.ProjectId, null,
            "field-advance.grant." + eventName, "field-advance-grant", targetId, "success",
            new Dictionary<string, string> { ["reason"] = meta.Reason.Trim(), ["scope-validation"] = "authorized" }), ct);
        // Audit may perform asynchronous work; expiry and current authority must still hold before saving.
        if (!await recheck()) return Denied();
        db.Add(new FinanceCommandReceipt
        {
            CommandId = meta.CommandId,
            WorkspaceId = scope.WorkspaceId,
            ActorId = actor,
            Action = action,
            PayloadSha256 = hash,
            Status = result.Status,
            ResultJson = JsonSerializer.Serialize(result),
            CreatedAtUtc = clock.GetUtcNow()
        });
        return result;
    }

    private static bool Valid(CommandMeta? meta) => meta is not null && meta.CommandId != Guid.Empty && meta.ExpectedVersion >= 1
        && !string.IsNullOrWhiteSpace(meta.Reason) && meta.Reason.Length <= 1000 && !meta.Reason.Any(char.IsControl);
    private static bool Valid(GrantSpec? spec) => spec is not null && spec.RecipientId != Guid.Empty
        && spec.Scope is not null && spec.Scope.WorkspaceId != Guid.Empty && spec.Scope.ProjectId != Guid.Empty
        && spec.Capabilities is { Length: > 0 and <= 10 } && spec.DelegableCapabilities is { Length: <= 10 }
        && spec.Capabilities.Concat(spec.DelegableCapabilities).All(FinanceCapabilities.Contains)
        && spec.Capabilities.Distinct().Count() == spec.Capabilities.Length
        && spec.DelegableCapabilities.Distinct().Count() == spec.DelegableCapabilities.Length
        && (spec.ApprovalLimit is null || spec.ApprovalLimit is >= 0 and <= 9999999999999999.99m
            && decimal.Round(spec.ApprovalLimit.Value, 2) == spec.ApprovalLimit)
        && spec.StartsAt != DateTimeOffset.MinValue && spec.StartsAt != DateTimeOffset.MaxValue
        && (spec.EndsAt is null || spec.EndsAt > spec.StartsAt && spec.EndsAt != DateTimeOffset.MaxValue);
    private static string Hash<T>(T payload) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload)));
    internal static GrantSpec Spec(GrantProposal proposal) => new(proposal.RecipientId, new(proposal.WorkspaceId, proposal.ProjectId),
        proposal.Capabilities, proposal.DelegableCapabilities, proposal.ApprovalLimit, proposal.StartsAtUtc, proposal.EndsAtUtc);
    private static GrantView View(GrantProposal proposal) => new(proposal.Id, proposal.State, proposal.Version, Spec(proposal));
    private static FinanceResult<GrantView> Invalid() => new(400, "invalid-request", null);
    private static FinanceResult<GrantView> Denied() => new(403, "finance-access-denied", null);
    private static FinanceResult<GrantView> Conflict() => new(409, "grant-command-conflict", null);
}
