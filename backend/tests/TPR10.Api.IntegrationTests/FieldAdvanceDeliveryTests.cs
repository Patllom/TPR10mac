using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Policies;
using TPR10.Api.Identity.Data;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvanceGrantTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceDeliveryTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Restored_lifecycle_retains_historical_snapshot_without_restoring_revoked_authority_and_refuses_downgrade()
    {
        await using var f = await FieldAdvancePolicyTests.Ready(postgres);
        var first = await FieldAdvancePolicyTests.Review(f, await FieldAdvancePolicyTests.Submit(f, await FieldAdvancePolicyTests.Create(f)));
        var asOf = f.Clock.GetUtcNow();
        var saved = await FieldAdvanceResolverTests.Resolve(f, asOf);
        Assert.Equal(200, saved.Status);
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        var next = await FieldAdvancePolicyTests.Review(f, await FieldAdvancePolicyTests.Submit(f,
            await FieldAdvancePolicyTests.Create(f, source: first, active: first, content: FieldAdvancePolicyTests.Content(20))));
        await using (var change = f.CreateContext())
        {
            var grant = await (from a in change.Set<GrantAssignment>()
                               join p in change.Set<GrantProposal>() on a.ProposalId equals p.Id
                               where p.RecipientId == RecipientId && p.ProjectId == ProjectId
                               select a).SingleAsync();
            grant.RevokedAtUtc = f.Clock.GetUtcNow(); grant.RevokedBy = AuthorId;
            grant.RevocationReason = "ถอนในฐานจำลองก่อนสำรอง"; grant.Version++;
            await change.SaveChangesAsync();
        }
        await using var restored = new IdentityDatabase(postgres.ConnectionString);
        await restored.InitializeAsync();
        await postgres.BackupRestoreAsync(f.Database.ConnectionString, restored.ConnectionString);
        var copy = new FieldAdvanceFixture(restored) { Clock = f.Clock };
        Assert.Equal(403, (await FieldAdvanceResolverTests.Resolve(copy, asOf)).Status);
        var reader = await copy.SessionAsync(AuthorId);
        await using var db = copy.CreateContext();
        var resolver = new PolicyResolver(db, FieldAdvanceAccessTests.Access(db, reader, copy.Clock), reader, copy.Clock);
        var historical = await resolver.ResolveAsync(new(WorkspaceId, ProjectId), asOf, default);
        Assert.Equal(200, historical.Status);
        Assert.Equal(first.Id, historical.Value!.CompanyVersionId);
        Assert.Equal(saved.Value!.ContentHash, historical.Value.ContentHash);
        Assert.Equal(7, historical.Value.Content.ClearingDays);
        Assert.Equal(100m, historical.Value.Content.Categories.Single().StandardRate);
        Assert.Equal(next.Id, (await resolver.ResolveAsync(new(WorkspaceId, ProjectId), copy.Clock.GetUtcNow(), default)).Value!.CompanyVersionId);
        var receipts = await db.Set<FinanceCommandReceipt>().CountAsync();
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(PreviousMigration));
        Assert.Equal("23514", error.SqlState);
        Assert.Equal(receipts, await db.Set<FinanceCommandReceipt>().CountAsync());
        Assert.Equal(2, await db.Set<PolicyReview>().CountAsync());
        Assert.Equal(2, await db.Set<PolicyRevision>().CountAsync());
        Assert.Single(await db.Set<GrantAssignment>().Where(x => x.RevokedAtUtc != null).ToArrayAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    // Moving receipt replay before current authorization must fail all six cases.
    [Theory]
    [InlineData("propose", "session")]
    [InlineData("review", "session")]
    [InlineData("revoke", "session")]
    [InlineData("propose", "capability")]
    [InlineData("review", "capability")]
    [InlineData("revoke", "capability")]
    public async Task Successful_receipt_cannot_be_replayed_after_current_authority_is_removed(string operation, string removed)
    {
        await using var f = await ReadyAsync(postgres);
        var actor = operation == "review" ? ReviewerId : AuthorId;
        var session = await f.SessionAsync(actor);
        var spec = Spec(f);
        var command = Meta();
        var id = Guid.Empty;
        if (operation != "propose")
        {
            var proposed = await ProposeAsync(f);
            Assert.Equal(201, proposed.Status);
            id = proposed.Value!.Id;
            if (operation == "revoke")
            {
                Assert.Equal(200, (await ReviewAsync(f, id)).Status);
                id = await AssignmentIdAsync(f, id);
            }
        }
        await using var db = f.CreateContext();
        Task<FinanceResult<GrantView>> Run() => operation switch
        {
            "propose" => Service(f, db, session).ProposeAsync(actor, spec, command, default),
            "review" => Service(f, db, session).ReviewAsync(actor, id, "approve", command, default),
            _ => Lifecycle(f, db, session).RevokeAsync(actor, id, command, default)
        };
        Assert.Equal(operation == "propose" ? 201 : 200, (await Run()).Status);
        var before = await db.Set<FinanceCommandReceipt>().AsNoTracking().SingleAsync(x => x.CommandId == command.CommandId);
        var auditCount = await db.AuditEvents.CountAsync();
        await using (var change = f.CreateContext())
        {
            if (removed == "session")
                (await change.Set<IdentitySession>().SingleAsync(x => x.Id == session.Entity!.Id)).RevokedAtUtc = f.Clock.GetUtcNow();
            else
                foreach (var source in await change.Set<GrantProposal>().Where(x => x.RecipientId == actor).ToArrayAsync()) source.Capabilities = [];
            await change.SaveChangesAsync();
        }
        var denied = await Run();
        Assert.Equal(403, denied.Status);
        Assert.Null(denied.Value);
        await db.SaveChangesAsync();
        var receipt = Assert.Single(await db.Set<FinanceCommandReceipt>().AsNoTracking().Where(x => x.CommandId == command.CommandId).ToArrayAsync());
        Assert.Equal(before.ResultJson, receipt.ResultJson);
        Assert.Equal(before.PayloadSha256, receipt.PayloadSha256);
        Assert.Equal(auditCount, await db.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task Company_scope_grant_succeeds_without_becoming_project_authority_or_cross_scope_replay()
    {
        await using var f = await ReadyAsync(postgres);
        await AuthorityAsync(f, AuthorId, AuthorPersonId, company: true);
        await AuthorityAsync(f, ReviewerId, ReviewerPersonId, company: true);
        var command = Meta();
        var spec = Spec(f) with { Scope = new(WorkspaceId, null) };
        var proposed = await ProposeAsync(f, spec, command);
        Assert.Equal(201, proposed.Status);
        Assert.Equal(200, (await ReviewAsync(f, proposed.Value!.Id)).Status);
        // Actor has authority in both scopes: a reused command must conflict, not mint a second proposal.
        Assert.Equal(409, (await ProposeAsync(f, spec with { Scope = new(WorkspaceId, ProjectId) }, command)).Status);
        var session = await f.SessionAsync(RecipientId);
        await using var db = f.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        var access = FieldAdvanceAccessTests.Access(db, session, f.Clock);
        Assert.True((await access.CheckAsync(RecipientId, new(WorkspaceId, null), "report.read", default)).Allowed);
        Assert.False((await access.CheckAsync(RecipientId, new(WorkspaceId, ProjectId), "report.read", default)).Allowed);
        var assignment = await db.Set<GrantAssignment>().SingleAsync(x => x.ProposalId == proposed.Value.Id);
        Assert.Null(assignment.ProjectId);
        Assert.Single(await db.Set<GrantProposal>().Where(x => x.RecipientId == RecipientId).ToArrayAsync());
        Assert.Single(await db.Set<FinanceCommandReceipt>().Where(x => x.CommandId == command.CommandId).ToArrayAsync());
    }
}
