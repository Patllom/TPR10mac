using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Data;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using G = TPR10.Api.IntegrationTests.FieldAdvanceGrantTests;
using P = TPR10.Api.IntegrationTests.FieldAdvancePolicyTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceAtomicityTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Competing_replacements_retire_existing_active_once_and_leave_loser_pending()
    {
        await using var f = await P.Ready(postgres);
        var original = await P.Review(f, await P.Submit(f, await P.Create(f)));
        var first = await P.Submit(f, await P.Create(f, source: original, active: original, content: P.Content(10)));
        var second = await P.Submit(f, await P.Create(f, source: original, active: original, content: P.Content(20)));
        var session1 = await f.SessionAsync(ReviewerId);
        var session2 = await f.SessionAsync(ReviewerId);
        await using var db1 = f.CreateContext();
        await using var db2 = f.CreateContext();
        var command1 = P.Meta(first);
        var command2 = P.Meta(second);
        var results = await Task.WhenAll(
            P.Service(f, db1, session1).ReviewAsync(ReviewerId, first.Id, "approve", command1, default),
            P.Service(f, db2, session2).ReviewAsync(ReviewerId, second.Id, "approve", command2, default));
        Assert.Equal(new[] { 200, 409 }, results.Select(x => x.Status).Order().ToArray());
        var winner = results[0].Status == 200 ? first : second;
        var loser = results[0].Status == 200 ? second : first;
        var loserCommand = results[0].Status == 200 ? command2 : command1;
        await using var check = f.CreateContext();
        var pointer = await check.Set<ActivePolicy>().SingleAsync();
        Assert.Equal(winner.Id, pointer.PolicyRevisionId);
        Assert.Equal(2, pointer.Version);
        Assert.Equal("Retired", (await check.Set<PolicyRevision>().SingleAsync(x => x.Id == original.Id)).State);
        Assert.Equal("Pending", (await check.Set<PolicyRevision>().SingleAsync(x => x.Id == loser.Id)).State);
        Assert.Single(await check.Set<PolicyRevision>().Where(x => x.State == "Active").ToArrayAsync());
        Assert.Equal(2, await check.Set<PolicyReview>().CountAsync());
        Assert.False(await check.Set<FinanceCommandReceipt>().AnyAsync(x => x.CommandId == loserCommand.CommandId));
        Assert.False(await check.Set<PolicyReview>().AnyAsync(x => x.PolicyRevisionId == loser.Id));
    }

    // Existing GrantRaceTests cover audit-insert faults; this injects a deferred COMMIT failure.
    [Theory]
    [InlineData("propose")]
    [InlineData("approve")]
    [InlineData("return")]
    [InlineData("reject")]
    [InlineData("revoke")]
    public async Task Deferred_commit_fault_leaves_no_partial_grant_audit_or_receipt_and_context_cannot_resave(string operation)
    {
        await using var f = await G.ReadyAsync(postgres);
        var id = Guid.Empty;
        if (operation != "propose")
        {
            var proposed = await G.ProposeAsync(f);
            Assert.Equal(201, proposed.Status);
            id = proposed.Value!.Id;
            if (operation == "revoke")
            {
                Assert.Equal(200, (await G.ReviewAsync(f, id)).Status);
                id = await G.AssignmentIdAsync(f, id);
            }
        }
        var actor = operation is "propose" or "revoke" ? AuthorId : ReviewerId;
        var session = await f.SessionAsync(actor);
        await using var check = f.CreateContext();
        var before = await Snapshot();
        await check.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_delivery_commit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'test-only-deferred-fault'; END $$;
            CREATE CONSTRAINT TRIGGER fail_delivery_commit AFTER INSERT ON field_advance_command_receipts
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fail_delivery_commit();
            """);
        await using var mutating = f.CreateContext();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => operation switch
        {
            "propose" => G.Service(f, mutating, session).ProposeAsync(actor, G.Spec(f), G.Meta(), default),
            "revoke" => G.Lifecycle(f, mutating, session).RevokeAsync(actor, id, G.Meta(), default),
            _ => G.Service(f, mutating, session).ReviewAsync(actor, id, operation, G.Meta(), default)
        });
        while (error.InnerException is not null) error = error.InnerException;
        Assert.Equal("P0001", Assert.IsType<Npgsql.PostgresException>(error).SqlState);
        await mutating.SaveChangesAsync();
        Assert.Equal(before, await Snapshot());
        async Task<string> Snapshot() => JsonSerializer.Serialize(new
        {
            proposals = await check.Set<GrantProposal>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            assignments = await check.Set<GrantAssignment>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            reviews = await check.Set<GrantReview>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            receipts = await check.Set<FinanceCommandReceipt>().AsNoTracking().OrderBy(x => x.CommandId).ToArrayAsync(),
            audit = await check.AuditEvents.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync()
        });
    }
}
