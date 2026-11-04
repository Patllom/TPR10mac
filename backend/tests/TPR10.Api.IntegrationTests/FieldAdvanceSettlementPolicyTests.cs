using System.Text.Json;
using System.Security.Cryptography;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using static TPR10.Api.IntegrationTests.FieldAdvancePolicyHttpTests;
using TPR10.Api.FieldAdvances.Settlements;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Policies;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvancePolicyTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceSettlementPolicyTests(PostgresFixture postgres)
{
    // JSON is the existing public input boundary: losing an unknown field must fail at runtime.
    private static PolicyContent Correction(int? days) => JsonSerializer.Deserialize<PolicyContent>(
        JsonSerializer.Serialize(Content()).TrimEnd('}') + ",\"CorrectionDays\":" + (days?.ToString() ?? "null") + "}")!;

    [Theory]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public async Task Approved_correction_days_survive_draft_update_review_and_resolution(int days)
    {
        await using var f = await Ready(postgres);
        var draft = await Create(f);
        var oldHash = draft.ContentHash;
        draft = await Update(f, draft, Correction(days));
        var active = await Review(f, await Submit(f, draft));
        await using var db = f.CreateContext();
        var session = await f.SessionAsync(AuthorId);
        var resolver = new PolicyResolver(db, FieldAdvanceAccessTests.Access(db, session, f.Clock), session, f.Clock);
        var result = await resolver.ResolveAsync(new(WorkspaceId, ProjectId), f.Clock.GetUtcNow(), default);
        Assert.Equal(200, result.Status);
        var json = JsonSerializer.SerializeToElement(result.Value!.Content);
        Assert.True(json.TryGetProperty("CorrectionDays", out var actual), "Approved correction days were lost through the policy lifecycle");
        Assert.Equal(days, actual.GetInt32());
        Assert.NotEqual(oldHash, active.ContentHash);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Nonpositive_correction_days_never_create_a_draft(int days)
    {
        await using var f = await Ready(postgres);
        var result = await Run(f, AuthorId, s => s.CreateDraftAsync(AuthorId, new(WorkspaceId, null), null,
            Correction(days), new(Guid.NewGuid(), 1, "ทดสอบจำนวนวันแก้ไข") { Policy = new("Company") }, default));
        Assert.Equal(400, result.Status);
        await using var db = f.CreateContext();
        Assert.Empty(await db.Set<PolicyRevision>().ToArrayAsync());
    }
    [Fact]
    public async Task Legacy_snapshot_is_readable_but_missing_correction_window_is_not_invented()
    {
        await using var f = await Ready(postgres);
        var company = await Review(f, await Submit(f, await Create(f)));
        // Freeze the deployed serializer shape independently of PolicyContent, which is evolving.
        const string oldContent = """{"ClearingDays":7,"Bands":[{"Order":1,"LevelCode":"director","UpperInclusive":1000}],"Categories":[{"Id":"78000000-0000-0000-0000-000000000001","Name":"\u0E2B\u0E21\u0E27\u0E14\u0E08\u0E33\u0E25\u0E2D\u0E07","Active":true,"Unit":"\u0E27\u0E31\u0E19","StandardRate":100,"EvidenceTypes":["receipt"]}]}""";
        var oldHashInput = "{\"scope\":{\"WorkspaceId\":\"71000000-0000-0000-0000-000000000001\",\"ProjectId\":null},\"mode\":\"Company\",\"basis\":null,\"content\":" + oldContent + "}";
        var oldHash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(oldHashInput)));
        Assert.Equal(oldHash, company.ContentHash);
        Assert.Equal(oldContent, JsonSerializer.Serialize(PolicyService.Copy(company.Content)));
        var oldSnapshot = "{\"CompanyVersionId\":\"" + company.Id + "\",\"ProjectVersionId\":null,\"Content\":" + oldContent + ",\"ContentHash\":\"" + oldHash + "\",\"Scope\":null,\"RequiresReview\":false}";
        var requestId = await SeedRequest(f, oldSnapshot, company.Id, oldHash);
        await using var db = f.CreateContext();
        var stored = await db.Set<AdvanceRequestRevision>().AsNoTracking().SingleAsync(x => x.RequestId == requestId);
        var legacy = JsonSerializer.Deserialize<PolicySnapshot>(stored.PolicySnapshotJson!)!;
        Assert.Equal(oldHash, legacy.ContentHash);
        Assert.Null(legacy.Content.CorrectionDays);
        Assert.Equal(oldSnapshot, JsonSerializer.Serialize(legacy));
        var window = await Resolve(f, requestId);
        Assert.Equal("correction-policy-not-ready", window.Code);
        Assert.Equal(409, window.Status);
        Assert.Null(window.Value);
        Assert.Equal(stored.PolicySnapshotJson, (await db.Set<AdvanceRequestRevision>().AsNoTracking().SingleAsync()).PolicySnapshotJson);
    }

    [Fact]
    public async Task Current_approved_company_policy_replaces_request_snapshot_but_not_a_previous_window()
    {
        await using var f = await Ready(postgres);
        var old = await Review(f, await Submit(f, await Create(f, content: Correction(2))));
        var snapshot = JsonSerializer.Serialize(new PolicySnapshot(old.Id, null, old.Content, old.ContentHash));
        var request = await SeedRequest(f, snapshot, old.Id, old.ContentHash);
        var initial = await Resolve(f, request);
        Assert.Equal(new CorrectionWindow(2, old.Id, old.ContentHash), initial.Value);
        var pending = await Submit(f, await Create(f, source: old, active: old, content: Correction(9)));
        Assert.Equal(initial.Value, (await Resolve(f, request)).Value);
        var current = await Review(f, pending);
        Assert.Equal(new CorrectionWindow(9, current.Id, current.ContentHash), (await Resolve(f, request)).Value);
        Assert.Equal(2, initial.Value!.Days);
        await using var db = f.CreateContext();
        Assert.Equal(old.ContentHash, (await db.Set<AdvanceRequestRevision>().SingleAsync()).PolicyHash);
    }

    [Theory]
    [InlineData("Override", 3)]
    [InlineData("Inherit", 9)]
    public async Task Project_resolution_preserves_7A_rebase_and_records_the_content_source(string mode, int expectedDays)
    {
        await using var f = await Ready(postgres);
        var company = await Review(f, await Submit(f, await Create(f, content: Correction(2))));
        var project = await Review(f, await Submit(f, await Create(f, project: true, basis: company.Id,
            mode: mode, content: mode == "Inherit" ? company.Content : Correction(3))));
        var request = await SeedRequest(f);
        var newer = await Review(f, await Submit(f, await Create(f, source: company, active: company, content: Correction(9))));
        var result = await Resolve(f, request);
        var source = mode == "Override" ? project : newer;
        Assert.Equal(200, result.Status);
        Assert.Equal(new CorrectionWindow(expectedDays, source.Id, source.ContentHash), result.Value);
        await using var db = f.CreateContext();
        var session = await f.SessionAsync(AuthorId);
        var policies = new PolicyResolver(db, FieldAdvanceAccessTests.Access(db, session, f.Clock), session, f.Clock);
        var effective = await policies.ResolveAsync(new(WorkspaceId, ProjectId), f.Clock.GetUtcNow(), default);
        Assert.Equal(mode == "Override", effective.Value!.RequiresReview);
    }


    [Fact]
    public async Task Migration_preserves_existing_rows_without_defaults_and_enforces_positive_storage()
    {
        await using var database = new IdentityDatabase(postgres.ConnectionString);
        await database.InitializeAsync();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261031084927_AddFieldAdvanceSettlements");
        await SeedDirectoryAsync(db);
        var fixture = new FieldAdvanceFixture(database);
        await fixture.SeedPoliciesAsync();
        var before = await db.Database.SqlQueryRaw<string>("""SELECT (to_jsonb(p))::text AS "Value" FROM field_advance_policy_revisions p ORDER BY id""").ToArrayAsync();
        await migrator.MigrateAsync();
        var after = await db.Database.SqlQueryRaw<string>("""SELECT (to_jsonb(p)-'correction_days')::text AS "Value" FROM field_advance_policy_revisions p ORDER BY id""").ToArrayAsync();
        Assert.Equal(before, after);
        Assert.All(await db.Set<PolicyRevision>().AsNoTracking().ToArrayAsync(), row => Assert.Null(row.CorrectionDays));
        Assert.False(db.Database.HasPendingModelChanges());
        foreach (var invalid in new[] { 0, -1 })
        {
            var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE field_advance_policy_revisions SET correction_days={invalid} WHERE id={CompanyRevisionId}"));
            Assert.Equal("23514", error.SqlState);
            Assert.Equal("ck_field_advance_policy_correction_days", error.ConstraintName);
        }
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE field_advance_policy_revisions SET correction_days={int.MaxValue} WHERE id={CompanyRevisionId}");
        Assert.Equal(int.MaxValue, (await db.Set<PolicyRevision>().AsNoTracking().SingleAsync(x => x.Id == CompanyRevisionId)).CorrectionDays);
    }

    [Fact]
    public async Task Correction_field_uses_existing_HTTP_review_and_history_lifecycle()
    {
        await using var f = await Ready(postgres);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var editor = await Client(factory, f, AuthorId);
        using var reviewer = await Client(factory, f, ReviewerId);
        using var created = await Send(editor, HttpMethod.Post, "policies", new CreatePolicyRequest(new(WorkspaceId, null), null,
            Content(), new(Guid.NewGuid(), 1, "ร่างเดิม") { Policy = new("Company") }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var raw = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
        Assert.False(raw.GetProperty("content").TryGetProperty("correctionDays", out _));
        var draft = (await created.Content.ReadFromJsonAsync<PolicyView>())!;
        using var updated = await Send(editor, HttpMethod.Put, $"policies/{draft.Id}", new UpdatePolicyRequest(Correction(4), Meta(draft)));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        draft = (await updated.Content.ReadFromJsonAsync<PolicyView>())!;
        Assert.Equal(4, draft.Content.CorrectionDays);
        using var submitted = await Send(editor, HttpMethod.Post, $"policies/{draft.Id}/submit", Meta(draft));
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        var pending = (await submitted.Content.ReadFromJsonAsync<PolicyView>())!;
        using var self = await Send(editor, HttpMethod.Post, $"policies/{draft.Id}/review", new ReviewPolicyRequest("approve", Meta(pending)));
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        using var approved = await Send(reviewer, HttpMethod.Post, $"policies/{draft.Id}/review", new ReviewPolicyRequest("approve", Meta(pending)));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var active = (await approved.Content.ReadFromJsonAsync<PolicyView>())!;
        Assert.Equal(4, active.Content.CorrectionDays);
        using var history = await editor.GetAsync($"/api/v1/field-advances/policies/{draft.Id}/history?workspaceId={WorkspaceId}");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var events = (await history.Content.ReadFromJsonAsync<PolicyHistoryView[]>())!;
        Assert.Equal(4, Assert.Single(events).Content!.CorrectionDays);
        using var scope = factory.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<SettlementPolicyResolver>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.5)]
    [InlineData(2147483648d)]
    public async Task Invalid_HTTP_days_are_rejected_without_creating_policy(double days)
    {
        await using var f = await Ready(postgres);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var editor = await Client(factory, f, AuthorId);
        var c = Content();
        using var response = await Send(editor, HttpMethod.Post, "policies", new
        {
            Scope = new FinanceScope(WorkspaceId, null),
            BaseCompanyVersionId = (Guid?)null,
            Content = new { c.ClearingDays, c.Bands, c.Categories, CorrectionDays = days },
            Meta = new CommandMeta(Guid.NewGuid(), 1, "ทดสอบ") { Policy = new("Company") }
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = f.CreateContext();
        Assert.Empty(await db.Set<PolicyRevision>().ToArrayAsync());
    }

    [Fact]
    public async Task Removing_correction_setting_preserves_old_review_evidence_and_old_command_replays()
    {
        await using var f = await Ready(postgres);
        var c = Content();
        var meta = new CommandMeta(Guid.NewGuid(), 1, "ร่างเดิม") { Policy = new("Company") };
        var scope = new FinanceScope(WorkspaceId, null);
        var created = await Run(f, AuthorId, service => service.CreateDraftAsync(AuthorId, scope, null, c, meta, default));
        Assert.Equal(201, created.Status);
        // Three-field deployed content, independent of the new nullable member.
        var oldContent = new { c.ClearingDays, c.Bands, c.Categories };
        var oldPayload = JsonSerializer.SerializeToUtf8Bytes(new { scope, baseCompanyVersionId = (Guid?)null, content = oldContent, meta });
        await using (var db = f.CreateContext())
        {
            var receipt = await db.Set<FinanceCommandReceipt>().SingleAsync(x => x.CommandId == meta.CommandId);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(oldPayload)), receipt.PayloadSha256);
        }
        var replay = await Run(f, AuthorId, service => service.CreateDraftAsync(AuthorId, scope, null, c with { CorrectionDays = null }, meta, default));
        Assert.Equal(201, replay.Status);
        Assert.Equal(created.Value!.Id, replay.Value!.Id);
        Assert.Equal(409, (await Run(f, AuthorId, service => service.CreateDraftAsync(AuthorId, scope, null, Correction(3), meta, default))).Status);
        var draft = await Update(f, created.Value, Correction(3));
        var returned = await Review(f, await Submit(f, draft), "return");
        var cleared = await Update(f, returned, Correction(null));
        Assert.Null(cleared.Content.CorrectionDays);
        await using var check = f.CreateContext();
        var review = await check.Set<PolicyReview>().SingleAsync();
        Assert.Equal(3, JsonSerializer.Deserialize<PolicyView>(review.ReviewedContentJson!)!.Content.CorrectionDays);
        Assert.Equal(draft.ContentHash, review.ReviewedContentHash);
    }

    [Fact]
    public async Task Resolver_requires_callers_transaction_and_does_not_resolve_unknown_requests()
    {
        await using var f = await Ready(postgres);
        await using var db = f.CreateContext();
        var session = await f.SessionAsync(AuthorId);
        var policies = new PolicyResolver(db, FieldAdvanceAccessTests.Access(db, session, f.Clock), session, f.Clock);
        var resolver = new SettlementPolicyResolver(db, policies, f.Clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveCorrectionAsync(Guid.NewGuid(), default));
        var unknown = await Resolve(f, Guid.NewGuid());
        Assert.Equal(403, unknown.Status);
        Assert.Null(unknown.Value);
    }

    [Fact]
    public async Task Project_without_correction_days_does_not_fall_back_to_company_days()
    {
        await using var f = await Ready(postgres);
        var company = await Review(f, await Submit(f, await Create(f, content: Correction(8))));
        await Review(f, await Submit(f, await Create(f, project: true, basis: company.Id)));
        var result = await Resolve(f, await SeedRequest(f));
        Assert.Equal("correction-policy-not-ready", result.Code);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Unapproved_or_inconsistent_policy_is_not_a_correction_window()
    {
        await using var f = await Ready(postgres);
        var pending = await Submit(f, await Create(f, content: Correction(8)));
        var request = await SeedRequest(f);
        Assert.Equal("policy-conflict", (await Resolve(f, request)).Code);
        var active = await Review(f, pending);
        await using (var db = f.CreateContext())
        {
            // Simulate evidence/row divergence: the resolver must reuse 7A integrity checks.
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE field_advance_policy_revisions SET correction_days=9 WHERE id={active.Id}");
        }
        var invalid = await Resolve(f, request);
        Assert.Equal("policy-conflict", invalid.Code);
        Assert.Null(invalid.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Downgrade_cannot_discard_recorded_correction_days(bool clearedAfterReview)
    {
        await using var f = await Ready(postgres);
        var draft = await Create(f, content: Correction(3));
        if (clearedAfterReview)
        {
            var returned = await Review(f, await Submit(f, draft), "return");
            await Update(f, returned, Correction(null));
        }
        await using var db = f.CreateContext();
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            db.GetService<IMigrator>().MigrateAsync("20261031084927_AddFieldAdvanceSettlements"));
        Assert.Equal("23514", error.SqlState);
        Assert.Equal(clearedAfterReview ? null : (int?)3, (await db.Set<PolicyRevision>().SingleAsync()).CorrectionDays);
        if (clearedAfterReview)
            Assert.Equal(3, JsonSerializer.Deserialize<PolicyView>((await db.Set<PolicyReview>().SingleAsync()).ReviewedContentJson!)!.Content.CorrectionDays);
    }

    private static async Task<Guid> SeedRequest(FieldAdvanceFixture f, string? snapshot = null, Guid? companyId = null, string? hash = null)
    {
        await using var db = f.CreateContext();
        var request = new AdvanceRequest
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            OwnerPersonId = AuthorPersonId,
            CreatedBy = AuthorId,
            CreatedAtUtc = f.Clock.GetUtcNow()
        };
        db.Add(request);
        db.Add(new AdvanceRequestRevision
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            RequestId = request.Id,
            RevisionNumber = 1,
            IsCurrent = true,
            ContentJson = "{}",
            ContentHash = new('a', 64),
            CreatedBy = AuthorId,
            CreatedByPersonId = AuthorPersonId,
            CreatedAtUtc = f.Clock.GetUtcNow(),
            CompanyPolicyRevisionId = companyId,
            PolicySnapshotJson = snapshot,
            PolicyHash = hash
        });
        await db.SaveChangesAsync();
        return request.Id;
    }

    private static async Task<FinanceResult<CorrectionWindow>> Resolve(FieldAdvanceFixture f, Guid request)
    {
        await using var db = f.CreateContext();
        var session = await f.SessionAsync(AuthorId);
        var policies = new PolicyResolver(db, FieldAdvanceAccessTests.Access(db, session, f.Clock), session, f.Clock);
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        return await new SettlementPolicyResolver(db, policies, f.Clock).ResolveCorrectionAsync(request, default);
    }

}
