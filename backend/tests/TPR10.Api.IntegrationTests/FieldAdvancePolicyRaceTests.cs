using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.FieldAdvances.Data;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvancePolicyTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvancePolicyRaceTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Concurrent_same_review_command_has_one_review_receipt_and_activation()
    {
        await using var f = await Ready(postgres);
        var pending = await Submit(f, await Create(f));
        var command = Meta(pending);
        var a = await f.SessionAsync(ReviewerId);
        var b = await f.SessionAsync(ReviewerId);
        await using var db1 = f.CreateContext();
        await using var db2 = f.CreateContext();
        var results = await Task.WhenAll(
            Service(f, db1, a).ReviewAsync(ReviewerId, pending.Id, "approve", command, default),
            Service(f, db2, b).ReviewAsync(ReviewerId, pending.Id, "approve", command, default));
        Assert.All(results, x => Assert.Equal(200, x.Status));
        Assert.Equal(JsonSerializer.Serialize(results[0]), JsonSerializer.Serialize(results[1]));
        await using var check = f.CreateContext();
        Assert.Single(await check.Set<PolicyReview>().ToArrayAsync());
        Assert.Single(await check.Set<FinanceCommandReceipt>().Where(x => x.CommandId == command.CommandId).ToArrayAsync());
        Assert.Single(await check.Set<ActivePolicy>().ToArrayAsync());
    }

    [Fact]
    public async Task Review_waits_for_identity_lock_and_observes_committed_revocation()
    {
        await using var f = await Ready(postgres);
        var pending = await Submit(f, await Create(f));
        var session = await f.SessionAsync(ReviewerId);
        await using var revoke = f.CreateContext();
        await using var tx = await revoke.Database.BeginTransactionAsync();
        await revoke.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        var grant = await (from a in revoke.Set<GrantAssignment>()
                           join p in revoke.Set<GrantProposal>() on a.ProposalId equals p.Id
                           where p.RecipientId == ReviewerId && p.ProjectId == null
                           select a).SingleAsync();
        grant.RevokedAtUtc = f.Clock.GetUtcNow(); grant.RevokedBy = AuthorId; grant.RevocationReason = "ถอนจำลอง"; grant.Version++;
        await revoke.SaveChangesAsync();
        await using var reviewing = f.CreateContext();
        var waiting = Service(f, reviewing, session).ReviewAsync(ReviewerId, pending.Id, "approve", Meta(pending), default);
        await using var probe = f.CreateContext();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (!await probe.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS(SELECT 1 FROM pg_locks WHERE locktype='advisory' AND objid=7241002 AND NOT granted) AS "Value"
                """).SingleAsync(deadline.Token))
            {
                Assert.False(waiting.IsCompleted);
                await Task.Delay(20, deadline.Token);
            }
        }
        finally { await tx.CommitAsync(); }
        Assert.Equal(403, (await waiting).Status);
        Assert.Empty(await probe.Set<ActivePolicy>().ToArrayAsync());
        Assert.Empty(await probe.Set<PolicyReview>().ToArrayAsync());
    }

    [Fact]
    public async Task Competing_drafts_cannot_both_replace_the_same_expected_active_revision()
    {
        await using var f = await Ready(postgres);
        var first = await Submit(f, await Create(f));
        var second = await Submit(f, await Create(f, source: first));
        // Seed sessions before concurrent service calls.
        var session1 = await f.SessionAsync(ReviewerId);
        var session2 = await f.SessionAsync(ReviewerId);
        await using var db1 = f.CreateContext();
        await using var db2 = f.CreateContext();
        var results = await Task.WhenAll(
            Service(f, db1, session1).ReviewAsync(ReviewerId, first.Id, "approve", Meta(first), default),
            Service(f, db2, session2).ReviewAsync(ReviewerId, second.Id, "approve", Meta(second), default));
        Assert.Equal(new[] { 200, 409 }, results.Select(x => x.Status).Order().ToArray());
        await using var check = f.CreateContext();
        Assert.Single(await check.Set<ActivePolicy>().ToArrayAsync());
        Assert.Single(await check.Set<PolicyRevision>().Where(x => x.State == "Active").ToArrayAsync());
        Assert.Single(await check.Set<PolicyReview>().ToArrayAsync());
    }

    [Theory]
    [InlineData("audit")]
    [InlineData("commit")]
    public async Task Failure_during_audit_or_before_commit_rolls_back_activation_retirement_review_and_receipt(string failure)
    {
        await using var f = await Ready(postgres);
        var active = await Review(f, await Submit(f, await Create(f)));
        var pending = await Submit(f, await Create(f, source: active, active: active));
        await using var db = f.CreateContext();
        var before = await Snapshot();
        await db.Database.ExecuteSqlRawAsync(failure == "audit" ? """
            CREATE FUNCTION fail_policy_write() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.event_type LIKE 'field-advance.policy.%' THEN RAISE EXCEPTION 'test-policy-fault'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER fail_policy_write BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION fail_policy_write();
            """ : """
            CREATE FUNCTION fail_policy_write() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'test-policy-fault'; END $$;
            CREATE CONSTRAINT TRIGGER fail_policy_write AFTER INSERT ON field_advance_command_receipts
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fail_policy_write();
            """);
        var session = await f.SessionAsync(ReviewerId);
        await using var mutating = f.CreateContext();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Service(f, mutating, session)
            .ReviewAsync(ReviewerId, pending.Id, "approve", Meta(pending), default));
        while (error.InnerException is not null) error = error.InnerException;
        Assert.Equal("P0001", Assert.IsType<Npgsql.PostgresException>(error).SqlState);
        await mutating.SaveChangesAsync();
        Assert.Equal(before, await Snapshot());
        async Task<string> Snapshot() => JsonSerializer.Serialize(new
        {
            revisions = await db.Set<PolicyRevision>().AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.State, x.Version }).ToArrayAsync(),
            active = await db.Set<ActivePolicy>().AsNoTracking().Select(x => new { x.PolicyRevisionId, x.Version }).ToArrayAsync(),
            reviews = await db.Set<PolicyReview>().CountAsync(),
            receipts = await db.Set<FinanceCommandReceipt>().CountAsync(),
            audit = await db.AuditEvents.CountAsync()
        });
    }

    [Fact]
    public async Task Review_rechecks_authority_after_audit_and_cannot_save_rolled_back_tracking()
    {
        await using var f = await Ready(postgres);
        var pending = await Submit(f, await Create(f));
        await using (var db = f.CreateContext())
        {
            foreach (var grant in await db.Set<GrantProposal>().Where(x => x.RecipientId == ReviewerId).ToArrayAsync())
                grant.EndsAtUtc = f.Clock.GetUtcNow().AddMinutes(1);
            await db.SaveChangesAsync();
        }
        var session = await f.SessionAsync(ReviewerId);
        await using var editing = f.CreateContext();
        var audit = new ExpiringAudit(new AuditEventWriter(editing, new CorrelationContext(), f.Clock), f.Clock);
        var result = await Service(f, editing, session, audit).ReviewAsync(ReviewerId, pending.Id, "approve", Meta(pending), default);
        Assert.Equal(403, result.Status);
        await editing.SaveChangesAsync();
        Assert.Empty(await editing.Set<ActivePolicy>().ToArrayAsync());
        Assert.Empty(await editing.Set<PolicyReview>().ToArrayAsync());
        Assert.Equal("Pending", (await editing.Set<PolicyRevision>().SingleAsync()).State);
    }

    private sealed class ExpiringAudit(IAuditEventWriter inner, ManualTimeProvider clock) : IAuditEventWriter
    {
        public Task WriteAsync(string eventType, Guid targetId, IReadOnlyDictionary<string, string> metadata, CancellationToken ct, Guid? actorId = null) =>
            inner.WriteAsync(eventType, targetId, metadata, ct, actorId);
        public async Task WriteAsync(SecurityAuditRequest request, CancellationToken ct)
        {
            await inner.WriteAsync(request, ct);
            clock.Advance(TimeSpan.FromMinutes(2));
        }
    }
}
