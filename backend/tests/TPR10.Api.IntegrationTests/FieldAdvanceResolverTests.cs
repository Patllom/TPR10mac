using Microsoft.EntityFrameworkCore;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Policies;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvancePolicyTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceResolverTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Inherit_selects_company_at_asOf_and_saved_snapshot_survives_new_approval()
    {
        await using var f = await Ready(postgres);
        var first = await Review(f, await Submit(f, await Create(f)));
        await using var evidence = f.CreateContext();
        var before = (await evidence.Set<PolicyReview>().SingleAsync()).CreatedAtUtc;
        var saved = (await Resolve(f, before)).Value!;
        Assert.NotNull(saved);
        var oldHash = saved.ContentHash;
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        var second = await Review(f, await Submit(f, await Create(f, source: first, active: first, content: Content(20))));
        var current = (await Resolve(f)).Value!;
        Assert.Equal(second.Id, current.CompanyVersionId);
        Assert.Null(current.ProjectVersionId);
        Assert.Equal(new FinanceScope(WorkspaceId, ProjectId), current.Scope);
        Assert.Equal(20, current.Content.ClearingDays);
        Assert.Equal(oldHash, saved.ContentHash);
        Assert.NotEqual(oldHash, current.ContentHash);
        Assert.Equal(first.Id, (await Resolve(f, before)).Value!.CompanyVersionId);
        Assert.Equal(409, (await Resolve(f, before.AddTicks(-1))).Status);
    }

    [Fact]
    public async Task Override_pins_base_until_reviewed_rebase_and_return_to_inherit()
    {
        await using var f = await Ready(postgres);
        var company = await Review(f, await Submit(f, await Create(f)));
        var project = await Review(f, await Submit(f, await Create(f, project: true, basis: company.Id, content: Content(3))));
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        var next = await Review(f, await Submit(f, await Create(f, source: company, active: company, content: Content(20))));
        var pinned = (await Resolve(f)).Value!;
        Assert.NotNull(pinned);
        Assert.Equal(company.Id, pinned.CompanyVersionId);
        Assert.Equal(project.Id, pinned.ProjectVersionId);
        Assert.Equal(3, pinned.Content.ClearingDays);
        Assert.True(pinned.RequiresReview);
        var rebase = await Submit(f, await Create(f, project: true, basis: next.Id, source: project, active: project, content: Content(4)));
        Assert.Equal(project.Id, (await Resolve(f)).Value!.ProjectVersionId);
        rebase = await Review(f, rebase);
        Assert.Equal(next.Id, (await Resolve(f)).Value!.CompanyVersionId);
        Assert.False((await Resolve(f)).Value!.RequiresReview);
        var inherit = await Submit(f, await Create(f, project: true, basis: next.Id, source: rebase, active: rebase, mode: "Inherit", content: next.Content));
        Assert.Equal(4, (await Resolve(f)).Value!.Content.ClearingDays);
        inherit = await Review(f, inherit);
        Assert.Equal(20, (await Resolve(f)).Value!.Content.ClearingDays);
        Assert.Equal(inherit.Id, (await Resolve(f)).Value!.ProjectVersionId);
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        var latest = await Review(f, await Submit(f, await Create(f, source: next, active: next, content: Content(30))));
        Assert.Equal(latest.Id, (await Resolve(f)).Value!.CompanyVersionId);
        Assert.Equal(30, (await Resolve(f)).Value!.Content.ClearingDays);
    }

    [Fact]
    public async Task Returned_arrays_cannot_change_storage_history_or_later_results()
    {
        await using var f = await Ready(postgres);
        var company = await Review(f, await Submit(f, await Create(f)));
        var first = (await Resolve(f)).Value!;
        Assert.NotNull(first);
        first.Content.Bands[0] = new(1, "tampered", 1m);
        first.Content.Categories[0].EvidenceTypes[0] = "tampered";
        first.Content.Categories[0] = first.Content.Categories[0] with { StandardRate = 1m };
        var second = (await Resolve(f)).Value!;
        Assert.Equal("director", second.Content.Bands[0].LevelCode);
        Assert.Equal(100m, second.Content.Categories[0].StandardRate);
        Assert.Equal("receipt", second.Content.Categories[0].EvidenceTypes[0]);
        Assert.Equal(company.ContentHash, second.ContentHash);
        await using var db = f.CreateContext();
        Assert.DoesNotContain("tampered", (await db.Set<PolicyReview>().SingleAsync()).ReviewedContentJson!);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("legacy")]
    [InlineData("hash")]
    [InlineData("json")]
    [InlineData("pointer")]
    [InlineData("timestamp")]
    [InlineData("mode")]
    public async Task Missing_or_untrustworthy_activation_evidence_is_conflict(string defect)
    {
        await using var f = await Ready(postgres);
        if (defect != "missing")
        {
            await Review(f, await Submit(f, await Create(f)));
            await using var db = f.CreateContext();
            var review = await db.Set<PolicyReview>().SingleAsync();
            if (defect == "legacy") { review.ReviewedVersion = null; review.ReviewedContentHash = null; review.ReviewedContentJson = null; }
            if (defect == "hash") review.ReviewedContentHash = new string('a', 64);
            if (defect == "json") review.ReviewedContentJson = "{}";
            if (defect == "pointer") (await db.Set<ActivePolicy>().SingleAsync()).Version++;
            if (defect == "timestamp") review.CreatedAtUtc = f.Clock.GetUtcNow().AddMinutes(1);
            if (defect == "mode") (await db.Set<PolicyRevision>().SingleAsync()).Mode = null;
            await db.SaveChangesAsync();
        }
        var result = await Resolve(f);
        Assert.Equal(409, result.Status);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Equal_activation_timestamps_use_proven_pointer_order_but_inconsistent_history_is_conflict()
    {
        await using var f = await Ready(postgres);
        var first = await Review(f, await Submit(f, await Create(f)));
        var second = await Review(f, await Submit(f, await Create(f, source: first, active: first, content: Content(10))));
        Assert.Equal(second.Id, (await Resolve(f)).Value!.CompanyVersionId);
        await using (var change = f.CreateContext())
        {
            var review = await change.Set<PolicyReview>().SingleAsync(x => x.PolicyRevisionId == first.Id);
            review.CreatedAtUtc = review.CreatedAtUtc.AddTicks(10);
            await change.SaveChangesAsync();
        }
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(409, (await Resolve(f)).Status);
    }

    [Fact]
    public async Task Future_asOf_is_invalid_and_revocation_denies_even_a_historical_snapshot()
    {
        await using var f = await Ready(postgres);
        await Review(f, await Submit(f, await Create(f)));
        var asOf = f.Clock.GetUtcNow();
        Assert.Equal(400, (await Resolve(f, asOf.AddSeconds(1))).Status);
        var session = await f.SessionAsync(RecipientId);
        await using var db = f.CreateContext();
        var resolver = new PolicyResolver(db, FieldAdvanceAccessTests.Access(db, session, f.Clock), session, f.Clock);
        Assert.Equal(200, (await resolver.ResolveAsync(new(WorkspaceId, ProjectId), asOf, default)).Status);
        await using (var change = f.CreateContext())
        {
            foreach (var grant in await change.Set<GrantAssignment>().Where(x => x.ProjectId == ProjectId).ToArrayAsync())
            {
                grant.RevokedAtUtc = f.Clock.GetUtcNow(); grant.RevokedBy = AuthorId;
                grant.RevocationReason = "ถอนสิทธิ์จำลอง"; grant.Version++;
            }
            await change.SaveChangesAsync();
        }
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(403, (await resolver.ResolveAsync(new(WorkspaceId, ProjectId), asOf, default)).Status);
    }

    internal static async Task<FinanceResult<PolicySnapshot>> Resolve(FieldAdvanceFixture f, DateTimeOffset? asOf = null, FinanceScope? scope = null)
    {
        var session = await f.SessionAsync(RecipientId);
        await using var db = f.CreateContext();
        return await new PolicyResolver(db, FieldAdvanceAccessTests.Access(db, session, f.Clock), session, f.Clock)
            .ResolveAsync(scope ?? new(WorkspaceId, ProjectId), asOf ?? f.Clock.GetUtcNow(), default);
    }
}
