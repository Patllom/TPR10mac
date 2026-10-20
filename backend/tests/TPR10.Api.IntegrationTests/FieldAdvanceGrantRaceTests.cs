using Microsoft.EntityFrameworkCore;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Data;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvanceGrantTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceGrantRaceTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("proposer")]
    [InlineData("reviewer")]
    public async Task Real_revoke_serializes_before_review_and_prevents_invalid_approval(string sourceActor)
    {
        await using var f = await ReadyAsync(postgres);
        var proposal = (await ProposeAsync(f)).Value!;
        var revokedActor = sourceActor == "proposer" ? AuthorId : ReviewerId;
        var revoker = sourceActor == "proposer" ? ReviewerId : AuthorId;
        Guid sourceId;
        await using (var seed = f.CreateContext())
        {
            var revokerSource = await seed.Set<GrantProposal>().SingleAsync(x => x.RecipientId == revoker);
            revokerSource.DelegableCapabilities = [.. revokerSource.DelegableCapabilities, "grant.propose", "grant.review", "grant.revoke"];
            sourceId = await (from a in seed.Set<GrantAssignment>()
                              join p in seed.Set<GrantProposal>() on a.ProposalId equals p.Id
                              where p.RecipientId == revokedActor
                              select a.Id).SingleAsync();
            await seed.SaveChangesAsync();
        }
        var revokerSession = await f.SessionAsync(revoker);
        var reviewerSession = await f.SessionAsync(ReviewerId);
        await using var revoking = f.CreateContext();
        await using var reviewing = f.CreateContext();
        var gate = new GatedAudit(new AuditEventWriter(revoking, new CorrelationContext(), f.Clock));
        var revoke = Lifecycle(f, revoking, revokerSession, gate).RevokeAsync(revoker, sourceId, Meta(), default);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var review = Service(f, reviewing, reviewerSession).ReviewAsync(ReviewerId, proposal.Id, "approve", Meta(), default);
        await using var probe = f.CreateContext();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (!await probe.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS(SELECT 1 FROM pg_locks WHERE locktype='advisory'
                  AND objid=7241002 AND NOT granted) AS "Value"
                """).SingleAsync(deadline.Token))
            {
                Assert.False(review.IsCompleted);
                await Task.Delay(20, deadline.Token);
            }
        }
        finally { gate.Release.TrySetResult(); }
        Assert.Equal(200, (await revoke).Status);
        Assert.Equal(403, (await review).Status);
        Assert.False(await probe.Set<GrantAssignment>().AnyAsync(x => x.ProposalId == proposal.Id));
        Assert.Equal("Pending", (await probe.Set<GrantProposal>().SingleAsync(x => x.Id == proposal.Id)).State);
    }

    [Fact]
    public async Task Expiry_during_audit_rolls_back_pending_approval_and_context_cannot_resave_it()
    {
        await using var f = await ReadyAsync(postgres);
        var proposal = (await ProposeAsync(f, Spec(f) with { EndsAt = f.Clock.GetUtcNow().AddMinutes(1) })).Value!;
        await using (var change = f.CreateContext())
        {
            var source = await change.Set<GrantProposal>().SingleAsync(x => x.RecipientId == ReviewerId);
            source.EndsAtUtc = f.Clock.GetUtcNow().AddMinutes(1);
            await change.SaveChangesAsync();
        }
        var current = await f.SessionAsync(ReviewerId);
        await using var db = f.CreateContext();
        var audit = new AdvancingAudit(new AuditEventWriter(db, new CorrelationContext(), f.Clock), f.Clock);
        Assert.Equal(403, (await Service(f, db, current, audit).ReviewAsync(ReviewerId, proposal.Id, "approve", Meta(), default)).Status);
        await db.SaveChangesAsync();
        Assert.Equal("Pending", (await db.Set<GrantProposal>().SingleAsync(x => x.Id == proposal.Id)).State);
        Assert.False(await db.Set<GrantAssignment>().AnyAsync(x => x.ProposalId == proposal.Id));
        Assert.False(await db.Set<GrantReview>().AnyAsync(x => x.ProposalId == proposal.Id));
        Assert.False(await db.AuditEvents.AnyAsync(x => x.EventType == "field-advance.grant.approved"));
        Assert.Equal(1, await db.Set<FinanceCommandReceipt>().CountAsync());
    }

    private sealed class GatedAudit(IAuditEventWriter inner) : IAuditEventWriter
    {
        public Task WriteAsync(string eventType, Guid targetId, IReadOnlyDictionary<string, string> metadata, CancellationToken ct, Guid? actorId = null) =>
            inner.WriteAsync(eventType, targetId, metadata, ct, actorId);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task WriteAsync(SecurityAuditRequest request, CancellationToken ct)
        {
            await inner.WriteAsync(request, ct);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        }
    }

    private sealed class AdvancingAudit(IAuditEventWriter inner, ManualTimeProvider clock) : IAuditEventWriter
    {
        public Task WriteAsync(string eventType, Guid targetId, IReadOnlyDictionary<string, string> metadata, CancellationToken ct, Guid? actorId = null) =>
            inner.WriteAsync(eventType, targetId, metadata, ct, actorId);
        public async Task WriteAsync(SecurityAuditRequest request, CancellationToken ct)
        {
            await inner.WriteAsync(request, ct);
            clock.Advance(TimeSpan.FromMinutes(2));
        }
    }

    // Removing the transaction, or committing state before audit, must break these assertions.
    [Theory]
    [InlineData("propose")]
    [InlineData("approve")]
    [InlineData("return")]
    [InlineData("reject")]
    [InlineData("revoke")]
    public async Task Database_audit_failure_rolls_back_state_receipt_and_review(string operation)
    {
        await using var f = await ReadyAsync(postgres);
        var id = Guid.Empty;
        if (operation != "propose")
        {
            var proposal = await ProposeAsync(f);
            id = proposal.Value!.Id;
            if (operation == "revoke")
            {
                Assert.Equal(200, (await ReviewAsync(f, id)).Status);
                id = await AssignmentIdAsync(f, id);
            }
        }
        await using var db = f.CreateContext();
        var before = await SnapshotAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_grant_audit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.event_type LIKE 'field-advance.grant.%' THEN
                RAISE EXCEPTION 'test-only-audit-failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER fail_grant_audit BEFORE INSERT ON audit_events
            FOR EACH ROW EXECUTE FUNCTION fail_grant_audit();
            """);
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => operation switch
        {
            "propose" => ProposeAsync(f),
            "revoke" => RevokeAsync(f, id),
            _ => ReviewAsync(f, id, operation)
        });
        Assert.Equal("P0001", Assert.IsType<Npgsql.PostgresException>(failure.InnerException).SqlState);
        Assert.Equal(before, await SnapshotAsync());

        async Task<string> SnapshotAsync()
        {
            var proposals = await db.Set<GrantProposal>().AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.State, x.Version }).ToArrayAsync();
            var assignments = await db.Set<GrantAssignment>().AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.Version, x.RevokedAtUtc }).ToArrayAsync();
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                proposals,
                assignments,
                reviews = await db.Set<GrantReview>().CountAsync(),
                receipts = await db.Set<FinanceCommandReceipt>().CountAsync(),
                audit = await db.AuditEvents.CountAsync()
            });
        }
    }

    [Fact]
    public async Task Concurrent_same_command_approvals_return_one_receipt_and_one_grant()
    {
        await using var f = await ReadyAsync(postgres);
        var proposal = await ProposeAsync(f);
        var meta = Meta();
        // MFA setup is fixture preparation, not part of the concurrent workflow.
        await f.SessionAsync(ReviewerId);
        var results = await Task.WhenAll(ReviewAsync(f, proposal.Value!.Id, meta: meta), ReviewAsync(f, proposal.Value.Id, meta: meta));
        Assert.All(results, x => Assert.Equal(200, x.Status));
        Assert.Equal(results[0].Value!.Id, results[1].Value!.Id);
        await using var db = f.CreateContext();
        Assert.Single(await db.Set<GrantAssignment>().Where(x => x.ProposalId == proposal.Value.Id).ToArrayAsync());
        Assert.Single(await db.Set<GrantReview>().Where(x => x.ProposalId == proposal.Value.Id).ToArrayAsync());
        Assert.Single(await db.Set<FinanceCommandReceipt>().Where(x => x.CommandId == meta.CommandId).ToArrayAsync());
        Assert.Equal(1, await db.AuditEvents.CountAsync(x => x.EventType == "field-advance.grant.approved"));
    }

    [Fact]
    public async Task Revocation_holding_identity_lock_prevents_waiting_review_from_using_stale_authority()
    {
        await using var f = await ReadyAsync(postgres);
        var proposal = await ProposeAsync(f);
        await using var revoking = f.CreateContext();
        await using var tx = await revoking.Database.BeginTransactionAsync();
        await revoking.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        var source = await (from assignment in revoking.Set<GrantAssignment>()
                            join p in revoking.Set<GrantProposal>() on assignment.ProposalId equals p.Id
                            where p.RecipientId == AuthorId
                            select assignment).SingleAsync();
        source.RevokedAtUtc = f.Clock.GetUtcNow(); source.RevokedBy = ReviewerId; source.RevocationReason = "ถอนขณะตรวจจำลอง"; source.Version++;
        await revoking.SaveChangesAsync();
        var waiting = ReviewAsync(f, proposal.Value!.Id);
        await using var probe = f.CreateContext();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            while (!await probe.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS(SELECT 1 FROM pg_locks WHERE locktype='advisory'
                  AND objid=7241002 AND NOT granted) AS "Value"
                """).SingleAsync(deadline.Token))
            {
                Assert.False(waiting.IsCompleted, "Review must wait for the identity transaction.");
                await Task.Delay(20, deadline.Token);
            }
        }
        finally { await tx.CommitAsync(); }
        Assert.Equal(403, (await waiting).Status);
        Assert.False(await AllowedAsync(f));
        Assert.False(await probe.Set<GrantAssignment>().AnyAsync(x => x.ProposalId == proposal.Value.Id));
        Assert.Equal("Pending", (await probe.Set<GrantProposal>().SingleAsync(x => x.Id == proposal.Value.Id)).State);
    }
}
