using Microsoft.EntityFrameworkCore;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Grants;
using TPR10.Api.Organization;
using TPR10.Api.Scopes;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvanceGrantTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceFinalReviewTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("propose")]
    [InlineData("review")]
    [InlineData("revoke")]
    public async Task Historical_replay_remains_readable_after_target_expiry(string operation)
    {
        await using var f = await ReadyAsync(postgres);
        var spec = Spec(f) with { EndsAt = f.Clock.GetUtcNow().AddSeconds(1) };
        var meta = Meta();
        var id = Guid.Empty;
        if (operation != "propose")
        {
            id = (await ProposeAsync(f, spec)).Value!.Id;
            if (operation == "revoke") { Assert.Equal(200, (await ReviewAsync(f, id)).Status); id = await AssignmentIdAsync(f, id); }
        }
        Task<FinanceResult<GrantView>> Run() => operation switch
        {
            "propose" => ProposeAsync(f, spec, meta),
            "review" => ReviewAsync(f, id, meta: meta),
            _ => RevokeAsync(f, id, meta)
        };
        var original = await Run();
        Assert.Equal(operation == "propose" ? 201 : 200, original.Status);
        f.Clock.Advance(TimeSpan.FromSeconds(2));
        var replay = await Run();
        Assert.Equal(original.Status, replay.Status);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(original), System.Text.Json.JsonSerializer.Serialize(replay));
    }

    [Theory]
    [InlineData("propose", "revoke")]
    [InlineData("review", "revoke")]
    [InlineData("revoke", "revoke")]
    [InlineData("propose", "expire")]
    [InlineData("review", "expire")]
    [InlineData("revoke", "expire")]
    public async Task Replay_requires_current_target_coverage_even_when_operation_capability_remains(string operation, string removal)
    {
        await using var f = await ReadyAsync(postgres);
        var actor = operation == "review" ? ReviewerId : AuthorId;
        var person = operation == "review" ? ReviewerPersonId : AuthorPersonId;
        Guid broad;
        await using (var lookup = f.CreateContext())
            broad = await (from a in lookup.Set<GrantAssignment>()
                           join p in lookup.Set<GrantProposal>() on a.ProposalId equals p.Id
                           where p.RecipientId == actor
                           select a.Id).SingleAsync();
        await AuthorityAsync(f, actor, person, limit: 10m);
        var spec = Spec(f) with { ApprovalLimit = 100m };
        var meta = Meta();
        var proposalId = Guid.Empty;
        var targetId = Guid.Empty;
        if (operation != "propose")
        {
            var proposed = await ProposeAsync(f, spec);
            Assert.Equal(201, proposed.Status);
            proposalId = targetId = proposed.Value!.Id;
            if (operation == "revoke")
            {
                Assert.Equal(200, (await ReviewAsync(f, proposalId)).Status);
                targetId = await AssignmentIdAsync(f, proposalId);
            }
        }
        var session = await f.SessionAsync(actor);
        await using var db = f.CreateContext();
        Task<FinanceResult<GrantView>> Run() => operation switch
        {
            "propose" => Service(f, db, session).ProposeAsync(actor, spec, meta, default),
            "review" => Service(f, db, session).ReviewAsync(actor, targetId, "approve", meta, default),
            _ => Lifecycle(f, db, session).RevokeAsync(actor, targetId, meta, default)
        };
        var success = await Run();
        Assert.Equal(operation == "propose" ? 201 : 200, success.Status);
        if (operation == "propose") proposalId = success.Value!.Id;
        var receipts = await db.Set<FinanceCommandReceipt>().CountAsync();
        var assignments = await db.Set<GrantAssignment>().CountAsync();
        var audits = await db.AuditEvents.CountAsync();
        await using (var change = f.CreateContext())
        {
            var assignment = await change.Set<GrantAssignment>().SingleAsync(x => x.Id == broad);
            if (removal == "revoke")
            {
                assignment.RevokedAtUtc = f.Clock.GetUtcNow(); assignment.RevokedBy = actor;
                assignment.RevocationReason = "ถอนต้นทางกว้างจำลอง"; assignment.Version++;
            }
            else (await change.Set<GrantProposal>().SingleAsync(x => x.Id == assignment.ProposalId)).EndsAtUtc = f.Clock.GetUtcNow();
            await change.SaveChangesAsync();
        }
        var access = FieldAdvanceAccessTests.Access(db, session, f.Clock);
        var reader = new GrantReadService(db, access, Service(f, db, session), new FinancePersonResolver(db, f.Clock),
            new ScopeAccess(db, session, new EffectiveRolePolicy(db), f.Clock), session, f.Clock);
        Assert.Equal(403, (await reader.DetailsAsync(spec.Scope, proposalId, default)).Status);
        var history = await reader.HistoryAsync(spec.Scope, proposalId, 1, 100, default);
        Assert.Equal(403, history.Status); Assert.Null(history.Value);
        // Discovery proves management capability survived; target coverage alone was removed.
        Assert.Contains((await reader.ScopesAsync(1, 100, default)).Value!, x => x.Scope == spec.Scope && x.Capabilities.Contains("grant." + operation));
        var replay = await Run();
        Assert.Equal(403, replay.Status); Assert.Null(replay.Value);
        Assert.Equal(receipts, await db.Set<FinanceCommandReceipt>().CountAsync());
        Assert.Equal(assignments, await db.Set<GrantAssignment>().CountAsync());
        Assert.Equal(audits, await db.AuditEvents.CountAsync());
    }
}
