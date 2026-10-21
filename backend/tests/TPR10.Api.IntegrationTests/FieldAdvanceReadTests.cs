using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Policies;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Sessions;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvancePolicyTests;
using static TPR10.Api.IntegrationTests.FieldAdvancePolicyHttpTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceReadTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("policy.edit.company", false, 200)]
    [InlineData("policy.edit.project", true, 200)]
    [InlineData("policy.review", true, 200)]
    [InlineData("policy.review", false, 200)]
    [InlineData("policy.edit.company", true, 403)]
    [InlineData("policy.edit.project", false, 403)]
    [InlineData("grant.propose", true, 403)]
    [InlineData("grant.review", true, 403)]
    [InlineData("grant.revoke", true, 403)]
    [InlineData("report.read", true, 403)]
    [InlineData("request.approve", true, 403)]
    [InlineData("finance.check", true, 403)]
    [InlineData("payment.record", true, 403)]
    [InlineData("", true, 403)]
    public async Task Reads_use_only_current_exact_scope_policy_editor_or_reviewer(string capability, bool project, int expected)
    {
        await using var f = await Ready(postgres);
        var company = await Review(f, await Submit(f, await Create(f)));
        var row = project ? await Review(f, await Submit(f, await Create(f, project: true, basis: company.Id))) : company;
        await SetRights(f, capability, project ? ProjectId : null);
        var session = await f.SessionAsync(RecipientId);
        await using var db = f.CreateContext();
        var service = Reader(f, db, session);
        Assert.Equal(expected, (await service.ListAsync(RecipientId, row.Scope, 1, 10, default)).Status);
        Assert.Equal(expected, (await service.DetailsAsync(RecipientId, row.Scope, row.Id, default)).Status);
        Assert.Equal(expected, (await service.HistoryAsync(RecipientId, row.Scope, row.Id, 1, 10, default)).Status);
        Assert.Equal(expected, (await service.PreviewAsync(RecipientId, row.Scope, 100m, default)).Status);
        Assert.Equal(capability == "policy.review" ? 200 : 403,
            (await service.ReviewQueueAsync(RecipientId, row.Scope, 1, 10, default)).Status);
    }

    [Fact]
    public async Task Project_authority_reads_effective_company_content_but_not_company_catalog_history_or_other_project()
    {
        await using var f = await Ready(postgres);
        var company = await Review(f, await Submit(f, await Create(f)));
        await SetRights(f, "policy.edit.project", ProjectId);
        Assert.Equal(200, (await FieldAdvanceResolverTests.Resolve(f)).Status);
        var session = await f.SessionAsync(RecipientId);
        await using var db = f.CreateContext();
        var service = Reader(f, db, session);
        foreach (var scope in new[] { new FinanceScope(WorkspaceId, null), new(WorkspaceId, OtherProjectId), new(OtherWorkspaceId, null) })
        {
            Assert.Equal(403, (await service.ListAsync(RecipientId, scope, 1, 10, default)).Status);
            Assert.Equal(403, (await service.HistoryAsync(RecipientId, scope, company.Id, 1, 10, default)).Status);
            Assert.Equal(403, (await service.PreviewAsync(RecipientId, scope, 100m, default)).Status);
        }
        var own = new FinanceScope(WorkspaceId, ProjectId);
        Assert.Equal(403, (await service.DetailsAsync(RecipientId, own, company.Id, default)).Status);
        Assert.Equal(403, (await service.HistoryAsync(RecipientId, own, company.Id, 1, 10, default)).Status);
        Assert.Equal(403, (await service.DetailsAsync(AuthorId, own, company.Id, default)).Status);
    }

    [Fact]
    public async Task List_filters_before_paging_and_returns_actual_pointer_without_replacing_revision_baseline()
    {
        await using var f = await Ready(postgres);
        var company = await Review(f, await Submit(f, await Create(f)));
        var first = await Review(f, await Submit(f, await Create(f, project: true, basis: company.Id)));
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        var next = await Submit(f, await Create(f, project: true, basis: company.Id, source: first, active: first, content: Content(9)));
        var session = await f.SessionAsync(RecipientId);
        await using var db = f.CreateContext();
        var service = Reader(f, db, session);
        var page = await service.ListAsync(RecipientId, first.Scope, 1, 1, default);
        Assert.Equal(200, page.Status);
        Assert.Equal(next.Id, Assert.Single(page.Value!).Id);
        Assert.Equal(first.Id, Assert.Single((await service.ListAsync(RecipientId, first.Scope, 2, 1, default)).Value!).Id);
        Assert.Empty((await service.ListAsync(RecipientId, first.Scope, 3, 1, default)).Value!);
        Assert.Equal(next.Id, Assert.Single((await service.ReviewQueueAsync(RecipientId, first.Scope, 1, 10, default)).Value!).Id);
        var details = (await service.DetailsAsync(RecipientId, first.Scope, first.Id, default)).Value!;
        Assert.Null(details.ExpectedActiveRevisionId);
        Assert.Equal(0, details.ExpectedActiveVersion);
        Assert.Equal(first.Id, details.ActiveRevisionId);
        Assert.Equal(1, details.ActiveVersion);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    [InlineData(int.MaxValue, 100)]
    public async Task Invalid_paging_is_rejected_without_overflow(int page, int size)
    {
        await using var f = await Ready(postgres);
        var draft = await Create(f);
        var session = await f.SessionAsync(RecipientId);
        await using var db = f.CreateContext();
        var service = Reader(f, db, session);
        Assert.Equal(400, (await service.ListAsync(RecipientId, draft.Scope, page, size, default)).Status);
        Assert.Equal(400, (await service.ReviewQueueAsync(RecipientId, draft.Scope, page, size, default)).Status);
        Assert.Equal(400, (await service.HistoryAsync(RecipientId, draft.Scope, draft.Id, page, size, default)).Status);
    }

    [Fact]
    public async Task History_retains_reviewed_content_after_return_edit_and_legacy_is_explicitly_unknown()
    {
        await using var f = await Ready(postgres);
        var returned = await Review(f, await Submit(f, await Create(f)), "return");
        var changed = await Update(f, returned, Content(20));
        var session = await f.SessionAsync(RecipientId);
        await using var db = f.CreateContext();
        var service = Reader(f, db, session);
        var history = await service.HistoryAsync(RecipientId, changed.Scope, changed.Id, 1, 10, default);
        Assert.Equal(200, history.Status);
        var item = Assert.Single(history.Value!);
        Assert.Equal(ReviewerId, item.ReviewerId);
        Assert.Equal("เหตุผลจำลอง", item.Reason);
        Assert.Equal(7, item.Content!.ClearingDays);
        Assert.Equal(2, item.ReviewedVersion);
        Assert.NotEqual(changed.ContentHash, item.ContentHash);
        await using (var change = f.CreateContext())
        {
            var review = await change.Set<PolicyReview>().SingleAsync();
            review.ReviewedVersion = null; review.ReviewedContentHash = null; review.ReviewedContentJson = null;
            await change.SaveChangesAsync();
        }
        var legacy = Assert.Single((await service.HistoryAsync(RecipientId, changed.Scope, changed.Id, 1, 10, default)).Value!);
        Assert.Null(legacy.Content);
        Assert.Null(legacy.ContentHash);
        Assert.Null(legacy.ReviewedVersion);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("mfa")]
    [InlineData("person")]
    [InlineData("grant")]
    public async Task Every_read_rechecks_current_authority_in_a_reused_service(string defect)
    {
        await using var f = await Ready(postgres);
        var row = await Review(f, await Submit(f, await Create(f)));
        var session = await f.SessionAsync(RecipientId);
        await using var db = f.CreateContext();
        var service = Reader(f, db, session);
        Assert.Equal(200, (await service.ListAsync(RecipientId, row.Scope, 1, 10, default)).Status);
        await using (var change = f.CreateContext())
        {
            var stored = await change.Set<IdentitySession>().SingleAsync(x => x.Id == session.Entity!.Id);
            if (defect == "session") stored.RevokedAtUtc = f.Clock.GetUtcNow();
            if (defect == "mfa") stored.MfaVerifiedAtUtc = null;
            if (defect == "person")
            {
                var link = await change.Set<FinanceAccountLink>().SingleAsync(x => x.UserId == RecipientId);
                link.EndedAtUtc = f.Clock.GetUtcNow(); link.EndedBy = AuthorId;
                link.EndReason = "สิ้นสุดการเชื่อมจำลอง"; link.Version++;
            }
            if (defect == "grant") foreach (var p in await change.Set<GrantProposal>().Where(x => x.RecipientId == RecipientId).ToArrayAsync()) p.Capabilities = [];
            await change.SaveChangesAsync();
        }
        Assert.Equal(403, (await service.ListAsync(RecipientId, row.Scope, 1, 10, default)).Status);
        Assert.Equal(403, (await service.DetailsAsync(RecipientId, row.Scope, row.Id, default)).Status);
        Assert.Equal(403, (await service.HistoryAsync(RecipientId, row.Scope, row.Id, 1, 10, default)).Status);
        Assert.Equal(403, (await service.ReviewQueueAsync(RecipientId, row.Scope, 1, 10, default)).Status);
        Assert.Equal(403, (await service.PreviewAsync(RecipientId, row.Scope, 100m, default)).Status);
    }

    [Fact]
    public async Task Production_reads_and_preview_use_server_scope_csrf_no_store_and_no_business_writes()
    {
        await using var f = await Ready(postgres);
        var row = await Review(f, await Submit(f, await Create(f)));
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var client = await Client(factory, f, RecipientId);
        await using var db = f.CreateContext();
        var counts = await Counts(db);
        foreach (var suffix in new[] { "", $"/{row.Id}", $"/{row.Id}/history", "/review-queue", "/effective" })
        {
            using var response = await client.GetAsync($"/api/v1/field-advances/policies{suffix}?workspaceId={WorkspaceId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
        }
        using var preview = await Send(client, HttpMethod.Post, "policies/preview", new { Scope = row.Scope, Total = 100m });
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal(new[] { "supervisor", "project-manager", "director" }, await preview.Content.ReadFromJsonAsync<string[]>());
        using var noCsrf = new HttpRequestMessage(HttpMethod.Post, "/api/v1/field-advances/policies/preview")
        { Content = JsonContent.Create(new { Scope = row.Scope, Total = 100m }) };
        noCsrf.Headers.Add("Origin", "https://localhost:4443");
        using var denied = await client.SendAsync(noCsrf);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains("urn:tpr10:csrf-invalid", await denied.Content.ReadAsStringAsync());
        Assert.Equal(counts, await Counts(db));
    }

    [Fact]
    public async Task Tampered_stored_content_cannot_be_returned_with_an_unrelated_hash()
    {
        await using var f = await Ready(postgres);
        var draft = await Create(f);
        await using (var change = f.CreateContext())
        {
            (await change.Set<PolicyRevision>().SingleAsync()).ClearingDays = 99;
            await change.SaveChangesAsync();
        }
        var session = await f.SessionAsync(RecipientId);
        await using var db = f.CreateContext();
        var service = Reader(f, db, session);
        Assert.Equal(409, (await service.DetailsAsync(RecipientId, draft.Scope, draft.Id, default)).Status);
        Assert.Equal(409, (await service.ListAsync(RecipientId, draft.Scope, 1, 10, default)).Status);
    }

    [Fact]
    public async Task Production_project_editor_cannot_read_company_catalog_history_or_review_queue()
    {
        await using var f = await Ready(postgres);
        var company = await Review(f, await Submit(f, await Create(f)));
        await SetRights(f, "policy.edit.project", ProjectId);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var client = await Client(factory, f, RecipientId);
        var projectQuery = $"workspaceId={WorkspaceId}&projectId={ProjectId}";
        using var effective = await client.GetAsync($"/api/v1/field-advances/policies/effective?{projectQuery}");
        Assert.Equal(HttpStatusCode.OK, effective.StatusCode);
        var snapshot = (await effective.Content.ReadFromJsonAsync<PolicySnapshot>())!;
        Assert.Equal(company.Id, snapshot.CompanyVersionId);
        Assert.Equal(new FinanceScope(WorkspaceId, ProjectId), snapshot.Scope);
        foreach (var path in new[] { $"?workspaceId={WorkspaceId}", $"/{company.Id}?{projectQuery}",
            $"/{company.Id}/history?{projectQuery}", $"/review-queue?{projectQuery}", $"/effective?workspaceId={OtherWorkspaceId}" })
        {
            using var response = await client.GetAsync("/api/v1/field-advances/policies" + path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(company.Id.ToString(), body);
            Assert.DoesNotContain(company.ContentHash, body);
            Assert.DoesNotContain("totalCount", body);
        }
        using var deniedPreview = await Send(client, HttpMethod.Post, "policies/preview", new { Scope = company.Scope, Total = 100m });
        Assert.Equal(HttpStatusCode.Forbidden, deniedPreview.StatusCode);
    }

    [Fact]
    public async Task Existing_cross_company_id_and_missing_id_have_identical_denial_without_content()
    {
        await using var f = await Ready(postgres);
        var foreignId = Guid.NewGuid();
        await using (var seed = f.CreateContext())
        {
            seed.Add(new PolicyRevision
            {
                Id = foreignId,
                WorkspaceId = OtherWorkspaceId,
                Mode = "Company",
                State = "Draft",
                ClearingDays = 7,
                ContentHash = new string('a', 64),
                CreatedBy = AuthorId,
                CreatedByPersonId = Guid.Parse("72000000-0000-0000-0000-000000000041"),
                CreatedAtUtc = f.Clock.GetUtcNow(),
                Reason = "กติกาต่างบริษัทจำลอง"
            });
            await seed.SaveChangesAsync();
        }
        var session = await f.SessionAsync(RecipientId);
        await using var db = f.CreateContext();
        var service = Reader(f, db, session);
        var scope = new FinanceScope(WorkspaceId, null);
        var denied = await service.DetailsAsync(RecipientId, scope, foreignId, default);
        Assert.Equal(403, denied.Status);
        Assert.Null(denied.Value);
        Assert.Equal(denied, await service.DetailsAsync(RecipientId, scope, Guid.NewGuid(), default));
        Assert.Equal(403, (await service.HistoryAsync(RecipientId, scope, foreignId, 1, 10, default)).Status);
        Assert.Empty((await service.ListAsync(RecipientId, scope, 1, 10, default)).Value!);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("details")]
    [InlineData("history")]
    [InlineData("queue")]
    [InlineData("preview")]
    [InlineData("resolve")]
    public async Task Reads_wait_for_identity_lock_and_observe_committed_revocation(string operation)
    {
        await using var f = await Ready(postgres);
        var row = await Review(f, await Submit(f, await Create(f)));
        var session = await f.SessionAsync(RecipientId);
        await using var revoke = f.CreateContext();
        await using var tx = await revoke.Database.BeginTransactionAsync();
        await revoke.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        var grant = await (from a in revoke.Set<GrantAssignment>()
                           join p in revoke.Set<GrantProposal>() on a.ProposalId equals p.Id
                           where p.RecipientId == RecipientId && p.ProjectId == null
                           select a).SingleAsync();
        grant.RevokedAtUtc = f.Clock.GetUtcNow(); grant.RevokedBy = AuthorId;
        grant.RevocationReason = "ถอนจำลองขณะรออ่าน"; grant.Version++;
        await revoke.SaveChangesAsync();
        await using var reading = f.CreateContext();
        var service = Reader(f, reading, session);
        async Task<int> Read() => operation switch
        {
            "list" => (await service.ListAsync(RecipientId, row.Scope, 1, 10, default)).Status,
            "details" => (await service.DetailsAsync(RecipientId, row.Scope, row.Id, default)).Status,
            "history" => (await service.HistoryAsync(RecipientId, row.Scope, row.Id, 1, 10, default)).Status,
            "queue" => (await service.ReviewQueueAsync(RecipientId, row.Scope, 1, 10, default)).Status,
            "preview" => (await service.PreviewAsync(RecipientId, row.Scope, 100m, default)).Status,
            _ => (await new PolicyResolver(reading, FieldAdvanceAccessTests.Access(reading, session, f.Clock), session, f.Clock)
                .ResolveAsync(row.Scope, f.Clock.GetUtcNow(), default)).Status
        };
        var waiting = Read();
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
        Assert.Equal(403, await waiting);
    }

    [Theory]
    [InlineData(0, 400)]
    [InlineData(-1, 400)]
    [InlineData(1000, 200)]
    [InlineData(1001, 400)]
    public async Task Preview_respects_existing_approval_boundaries_and_does_not_create_business_records(int total, int expected)
    {
        await using var f = await Ready(postgres);
        var company = await Review(f, await Submit(f, await Create(f)));
        var session = await f.SessionAsync(RecipientId);
        await using var db = f.CreateContext();
        var before = await Counts(db);
        var service = Reader(f, db, session);
        Assert.Equal(expected, (await service.PreviewAsync(RecipientId, company.Scope, total, default)).Status);
        Assert.Equal(400, (await service.PreviewAsync(RecipientId, company.Scope, 0.001m, default)).Status);
        Assert.Equal(before, await Counts(db));
        Assert.Empty(db.ChangeTracker.Entries());
    }

    internal static PolicyReadService Reader(FieldAdvanceFixture f, Tpr10DbContext db, RequestSession session)
    {
        var access = FieldAdvanceAccessTests.Access(db, session, f.Clock);
        return new(db, access, session, f.Clock, new PolicyResolver(db, access, session, f.Clock));
    }
    internal static async Task SetRights(FieldAdvanceFixture f, string capability, Guid? project)
    {
        await using var db = f.CreateContext();
        foreach (var grant in await db.Set<GrantProposal>().Where(x => x.RecipientId == RecipientId).ToArrayAsync())
            grant.Capabilities = grant.ProjectId == project && capability.Length > 0 ? [capability] : [];
        await db.SaveChangesAsync();
    }
    private static async Task<int[]> Counts(Tpr10DbContext db) =>
    [await db.Set<PolicyRevision>().CountAsync(), await db.Set<PolicyReview>().CountAsync(),
        await db.Set<ActivePolicy>().CountAsync(), await db.Set<FinanceCommandReceipt>().CountAsync(),
        await db.Set<GrantProposal>().CountAsync(), await db.Set<GrantAssignment>().CountAsync()];
}
