using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TPR10.Api.FieldAdvances.Data;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvancePolicyTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvancePolicyMigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Downgrade_cannot_discard_recorded_policy_lifecycle_or_review_evidence()
    {
        await using var f = await Ready(postgres);
        var pending = await Submit(f, await Create(f));
        await Review(f, pending);
        await using var db = f.CreateContext();
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            db.GetService<IMigrator>().MigrateAsync("20261019142031_AddFieldAdvancePolicyFoundation"));
        Assert.Equal("23514", error.SqlState);
        Assert.Equal(pending.ContentHash, (await db.Set<PolicyReview>().SingleAsync()).ReviewedContentHash);
        Assert.Equal("Company", (await db.Set<PolicyRevision>().SingleAsync()).Mode);
    }

    [Fact]
    public async Task Upgrade_preserves_legacy_reviews_without_fabricating_seen_content_evidence()
    {
        await using var database = new IdentityDatabase(postgres.ConnectionString);
        await database.InitializeAsync();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261019142031_AddFieldAdvancePolicyFoundation");
        await SeedDirectoryAsync(db);
        var f = new FieldAdvanceFixture(database);
        await f.SeedPoliciesAsync();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO field_advance_policy_reviews
            (id,workspace_id,policy_revision_id,reviewer_id,reviewer_person_id,outcome,reason,created_at_utc)
            VALUES ('78000000-0000-0000-0000-000000000099','71000000-0000-0000-0000-000000000001',
            '73000000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000032',
            '72000000-0000-0000-0000-000000000032','Returned','หลักฐานเดิมไม่ทราบเนื้อหา','2026-01-02T00:00:00Z');
            """);
        await migrator.MigrateAsync();
        var review = await db.Set<PolicyReview>().SingleAsync();
        Assert.Equal(Guid.Parse("78000000-0000-0000-0000-000000000099"), review.Id);
        Assert.Equal("Returned", review.Outcome);
        Assert.Equal("หลักฐานเดิมไม่ทราบเนื้อหา", review.Reason);
        Assert.Equal(DateTimeOffset.Parse("2026-01-02T00:00:00Z"), review.CreatedAtUtc);
        Assert.Null(review.ReviewedVersion);
        Assert.Null(review.ReviewedContentHash);
        Assert.Null(review.ReviewedContentJson);
        Assert.Equal(2, await db.Set<PolicyRevision>().CountAsync());
        Assert.Single(await db.Set<PolicyContributor>().ToArrayAsync());
        Assert.Empty(await db.Set<ActivePolicy>().ToArrayAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Returned_review_keeps_exact_seen_version_hash_and_content_after_draft_edit()
    {
        await using var f = await Ready(postgres);
        var pending = await Submit(f, await Create(f));
        var returned = await Review(f, pending, "return");
        await Update(f, returned, Content(30));
        await using var db = f.CreateContext();
        var review = await db.Set<PolicyReview>().SingleAsync();
        Assert.Equal(pending.Version, review.ReviewedVersion);
        Assert.Equal(pending.ContentHash, review.ReviewedContentHash);
        using var evidence = System.Text.Json.JsonDocument.Parse(review.ReviewedContentJson!);
        Assert.Equal(7, evidence.RootElement.GetProperty("Content").GetProperty("ClearingDays").GetInt32());
        Assert.Equal(30, (await db.Set<PolicyRevision>().SingleAsync()).ClearingDays);
    }
}
