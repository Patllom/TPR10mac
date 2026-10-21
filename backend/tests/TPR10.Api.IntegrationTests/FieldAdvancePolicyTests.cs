using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Policies;
using TPR10.Api.Identity.Sessions;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvancePolicyTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Submitting_unchanged_content_does_not_invent_edit_contribution()
    {
        await using var f = await Ready(postgres);
        var draft = await Create(f);
        var pending = await Submit(f, draft, RecipientId);
        await using var db = f.CreateContext();
        Assert.False(await db.Set<PolicyContributor>().AnyAsync(x => x.PolicyRevisionId == draft.Id && x.PersonId == RecipientPersonId));
        // Current exact-scope authority and actual edit history remain the review predicates.
        Assert.Equal("Active", (await Review(f, pending, actor: RecipientId)).State);
    }

    [Theory]
    [InlineData("create", "session")]
    [InlineData("update", "session")]
    [InlineData("submit", "session")]
    [InlineData("review", "session")]
    [InlineData("create", "mfa")]
    [InlineData("update", "mfa")]
    [InlineData("submit", "mfa")]
    [InlineData("review", "mfa")]
    [InlineData("create", "grant")]
    [InlineData("update", "grant")]
    [InlineData("submit", "grant")]
    [InlineData("review", "grant")]
    public async Task Every_operation_reloads_current_session_mfa_and_finance_grants(string operation, string defect)
    {
        await using var f = await Ready(postgres);
        PolicyView? row = operation == "create" ? null : await Create(f);
        if (operation == "review") row = await Submit(f, row!);
        var actor = operation == "review" ? ReviewerId : AuthorId;
        var session = await f.SessionAsync(actor);
        await using (var change = f.CreateContext())
        {
            var entity = await change.Set<TPR10.Api.Identity.Data.IdentitySession>().SingleAsync(x => x.Id == session.Entity!.Id);
            if (defect == "session") entity.RevokedAtUtc = f.Clock.GetUtcNow();
            if (defect == "mfa") entity.MfaVerifiedAtUtc = null;
            if (defect == "grant")
                foreach (var grant in await change.Set<GrantProposal>().Where(x => x.RecipientId == actor).ToArrayAsync()) grant.Capabilities = [];
            await change.SaveChangesAsync();
        }
        await using var db = f.CreateContext();
        var service = Service(f, db, session);
        var result = operation switch
        {
            "create" => await service.CreateDraftAsync(actor, new(WorkspaceId, null), null, Content(),
                new(Guid.NewGuid(), 1, "ทดสอบ") { Policy = new("Company") }, default),
            "update" => await service.UpdateDraftAsync(actor, row!.Id, Content(10), Meta(row), default),
            "submit" => await service.SubmitAsync(actor, row!.Id, Meta(row), default),
            _ => await service.ReviewAsync(actor, row!.Id, "approve", Meta(row), default)
        };
        Assert.Equal(403, result.Status);
        if (row is not null) Assert.Equal(row.Version, (await db.Set<PolicyRevision>().SingleAsync()).Version);
        else Assert.Empty(await db.Set<PolicyRevision>().ToArrayAsync());
    }

    [Fact]
    public async Task Contributor_alias_account_cannot_review_the_same_persons_policy()
    {
        await using var f = await Ready(postgres);
        var pending = await Submit(f, await Create(f));
        await using (var db = f.CreateContext())
        {
            db.Add(new FinanceAccountLink
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                UserId = AliasAccountId,
                PersonId = AuthorPersonId,
                CreatedBy = AuthorId,
                CreatedAtUtc = f.Clock.GetUtcNow(),
                Reason = "บัญชีทดสอบบุคคลเดียวกัน"
            });
            await db.SaveChangesAsync();
        }
        await FieldAdvanceGrantTests.AuthorityAsync(f, AliasAccountId, AuthorPersonId, company: true);
        await using (var db = f.CreateContext())
        {
            var grant = await db.Set<GrantProposal>().SingleAsync(x => x.RecipientId == AliasAccountId);
            grant.Capabilities = ["policy.review"];
            await db.SaveChangesAsync();
        }
        Assert.Equal(403, (await Run(f, AliasAccountId, s => s.ReviewAsync(AliasAccountId, pending.Id, "approve", Meta(pending), default))).Status);
    }

    [Fact]
    public async Task Override_keeps_approved_company_base_while_company_changes()
    {
        await using var f = await Ready(postgres);
        var company = await Review(f, await Submit(f, await Create(f)));
        var project = await Submit(f, await Create(f, project: true, basis: company.Id, content: Content(3)));
        await Review(f, await Submit(f, await Create(f, source: company, active: company, content: Content(20))));
        var approved = await Review(f, project);
        Assert.Equal(company.Id, approved.BaseCompanyVersionId);
        Assert.Equal(3, approved.Content.ClearingDays);
        Assert.Equal("Override", approved.Mode);
    }

    [Fact]
    public async Task Existing_grant_receipt_remains_replayable_when_policy_metadata_is_absent()
    {
        await using var f = await FieldAdvanceGrantTests.ReadyAsync(postgres);
        var spec = FieldAdvanceGrantTests.Spec(f);
        var meta = FieldAdvanceGrantTests.Meta();
        var result = await FieldAdvanceGrantTests.ProposeAsync(f, spec, meta);
        Assert.Equal(201, result.Status);
        await using var db = f.CreateContext();
        var receipt = await db.Set<FinanceCommandReceipt>().SingleAsync();
        // Task4 serialized exactly these three command fields, before Task6 added policy options.
        receipt.PayloadSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(new { spec, meta = new { meta.CommandId, meta.ExpectedVersion, meta.Reason } })));
        await db.SaveChangesAsync();
        Assert.Equal(201, (await FieldAdvanceGrantTests.ProposeAsync(f, spec, meta)).Status);
    }

    [Fact]
    public async Task Branching_from_an_older_revision_does_not_erase_other_contributors_in_the_same_lineage()
    {
        await using var f = await Ready(postgres);
        var root = await Create(f);
        var branch = await Create(f, source: root);
        branch = await Update(f, branch, Content(9), RecipientId);
        await Review(f, await Submit(f, branch), "reject");
        var sibling = await Submit(f, await Create(f, source: root));
        Assert.Equal(403, (await Run(f, RecipientId, s => s.ReviewAsync(RecipientId, sibling.Id, "approve", Meta(sibling), default))).Status);
    }

    [Fact]
    public async Task Draft_update_submit_approve_retires_old_revision_and_keeps_active_content_immutable()
    {
        await using var f = await Ready(postgres);
        var draft = await Create(f);
        var updated = await Update(f, draft, Content(12));
        Assert.Equal(2, updated.Version);
        Assert.NotEqual(draft.ContentHash, updated.ContentHash);
        var pending = await Submit(f, updated);
        Assert.Equal(409, (await Run(f, AuthorId, s => s.UpdateDraftAsync(AuthorId, pending.Id, Content(), Meta(pending), default))).Status);
        var active = await Review(f, pending);
        Assert.Equal("Active", active.State);
        Assert.Equal(409, (await Run(f, AuthorId, s => s.UpdateDraftAsync(AuthorId, active.Id, Content(), Meta(active), default))).Status);
        var next = await Create(f, source: active, active: active);
        var second = await Review(f, await Submit(f, next));
        await using var db = f.CreateContext();
        Assert.Equal(second.Id, (await db.Set<ActivePolicy>().SingleAsync()).PolicyRevisionId);
        Assert.Equal(2, (await db.Set<ActivePolicy>().SingleAsync()).Version);
        Assert.Equal("Retired", (await db.Set<PolicyRevision>().SingleAsync(x => x.Id == active.Id)).State);
        Assert.Equal(12, (await db.Set<PolicyRevision>().SingleAsync(x => x.Id == active.Id)).ClearingDays);
    }

    [Theory]
    [InlineData("return")]
    [InlineData("reject")]
    public async Task Contributors_survive_return_reject_and_new_revisions(string decision)
    {
        await using var f = await Ready(postgres);
        var draft = await Create(f);
        draft = await Update(f, draft, Content(9), RecipientId);
        var pending = await Submit(f, draft);
        foreach (var actor in new[] { AuthorId, RecipientId })
            Assert.Equal(403, (await Run(f, actor, s => s.ReviewAsync(actor, pending.Id, "approve", Meta(pending), default))).Status);
        var reviewed = await Review(f, pending, decision);
        Assert.Equal(decision == "return" ? "Returned" : "Rejected", reviewed.State);
        Assert.Equal(409, (await Run(f, ReviewerId, s => s.ReviewAsync(ReviewerId, reviewed.Id, "approve", Meta(reviewed), default))).Status);
        if (decision == "return")
        {
            var edited = await Update(f, reviewed, Content(10));
            Assert.Equal("Draft", edited.State);
            Assert.Equal(409, (await Run(f, ReviewerId, s => s.ReviewAsync(ReviewerId, edited.Id, "approve", Meta(edited), default))).Status);
            reviewed = edited;
        }
        else
            Assert.Equal(409, (await Run(f, AuthorId, s => s.UpdateDraftAsync(AuthorId, reviewed.Id, Content(), Meta(reviewed), default))).Status);
        var child = await Create(f, source: reviewed);
        child = await Submit(f, child);
        Assert.Equal(403, (await Run(f, RecipientId, s => s.ReviewAsync(RecipientId, child.Id, "approve", Meta(child), default))).Status);
        Assert.Equal("Active", (await Review(f, child)).State);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    public async Task Project_can_be_weaker_or_stronger_and_company_author_can_review_with_exact_project_grant(int days)
    {
        await using var f = await Ready(postgres);
        var company = await Review(f, await Submit(f, await Create(f)));
        var project = await Create(f, project: true, basis: company.Id, actor: RecipientId, content: Content(days));
        // Author contributed to the company base only; null-scope policy.review is removed.
        await using (var db = f.CreateContext())
        {
            var grants = await db.Set<GrantProposal>().Where(x => x.RecipientId == AuthorId && x.ProjectId == null).ToArrayAsync();
            foreach (var grant in grants) grant.Capabilities = ["policy.edit.company"];
            await db.SaveChangesAsync();
        }
        var pending = await Submit(f, project, RecipientId);
        var approved = await Review(f, pending, actor: AuthorId);
        Assert.Equal(company.Id, approved.BaseCompanyVersionId);
        Assert.Equal(days, approved.Content.ClearingDays);
    }

    [Fact]
    public async Task Company_only_reviewer_cannot_review_project_and_unknown_scope_does_not_disclose()
    {
        await using var f = await Ready(postgres);
        var company = await Review(f, await Submit(f, await Create(f)));
        var project = await Submit(f, await Create(f, project: true, basis: company.Id));
        await using (var db = f.CreateContext())
        {
            var grants = await db.Set<GrantProposal>().Where(x => x.RecipientId == ReviewerId && x.ProjectId != null).ToArrayAsync();
            foreach (var grant in grants) grant.Capabilities = [];
            await db.SaveChangesAsync();
        }
        var denied = await Run(f, ReviewerId, s => s.ReviewAsync(ReviewerId, project.Id, "approve", Meta(project), default));
        Assert.Equal(403, denied.Status);
        Assert.Equal(denied.Code, (await Run(f, ReviewerId, s => s.ReviewAsync(ReviewerId, Guid.NewGuid(), "approve", Meta(project), default))).Code);
    }

    [Fact]
    public async Task Inherit_requires_review_and_changed_company_base_requires_reload_and_resubmit()
    {
        await using var f = await Ready(postgres);
        var company = await Review(f, await Submit(f, await Create(f)));
        var project = await Review(f, await Submit(f, await Create(f, project: true, basis: company.Id)));
        var inherit = await Create(f, project: true, basis: company.Id, source: project, active: project, mode: "Inherit", content: company.Content);
        var pending = await Submit(f, inherit);
        var newer = await Review(f, await Submit(f, await Create(f, source: company, active: company, content: Content(15))));
        Assert.Equal(409, (await Run(f, ReviewerId, s => s.ReviewAsync(ReviewerId, pending.Id, "approve", Meta(pending), default))).Status);
        await using (var db = f.CreateContext())
            Assert.Equal(project.Id, (await db.Set<ActivePolicy>().SingleAsync(x => x.ProjectId == ProjectId)).PolicyRevisionId);
        var returned = await Review(f, pending, "return");
        var refreshed = await Update(f, returned, newer.Content, meta: Meta(returned) with
        { Policy = Meta(returned).Policy! with { BaseCompanyVersionId = newer.Id } });
        var approved = await Review(f, await Submit(f, refreshed));
        Assert.Equal("Inherit", approved.Mode);
        Assert.Equal(newer.Id, approved.BaseCompanyVersionId);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("version")]
    [InlineData("active")]
    public async Task Review_rejects_stale_content_version_and_active_pointer(string defect)
    {
        await using var f = await Ready(postgres);
        var pending = await Submit(f, await Create(f));
        var meta = Meta(pending);
        meta = defect switch
        {
            "hash" => meta with { Policy = meta.Policy! with { ExpectedContentHash = new string('0', 64) } },
            "version" => meta with { ExpectedVersion = 99 },
            _ => meta with { Policy = meta.Policy! with { ExpectedActiveRevisionId = Guid.NewGuid(), ExpectedActiveVersion = 1 } }
        };
        Assert.Equal(409, (await Run(f, ReviewerId, s => s.ReviewAsync(ReviewerId, pending.Id, "approve", meta, default))).Status);
        await using var db = f.CreateContext();
        Assert.Empty(await db.Set<ActivePolicy>().ToArrayAsync());
    }

    [Fact]
    public async Task Exact_replay_is_idempotent_but_changed_payload_and_revoked_authority_are_rejected()
    {
        await using var f = await Ready(postgres);
        var pending = await Submit(f, await Create(f));
        var meta = Meta(pending);
        var first = await Run(f, ReviewerId, s => s.ReviewAsync(ReviewerId, pending.Id, "approve", meta, default));
        Assert.Equal(200, first.Status);
        var replay = await Run(f, ReviewerId, s => s.ReviewAsync(ReviewerId, pending.Id, "approve", meta, default));
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(replay));
        Assert.Equal(409, (await Run(f, ReviewerId, s => s.ReviewAsync(ReviewerId, pending.Id, "reject", meta, default))).Status);
        await using var db = f.CreateContext();
        foreach (var grant in await db.Set<GrantProposal>().Where(x => x.RecipientId == ReviewerId).ToArrayAsync()) grant.Capabilities = [];
        await db.SaveChangesAsync();
        Assert.Equal(403, (await Run(f, ReviewerId, s => s.ReviewAsync(ReviewerId, pending.Id, "approve", meta, default))).Status);
        Assert.Single(await db.Set<PolicyReview>().Where(x => x.PolicyRevisionId == pending.Id).ToArrayAsync());
    }

    [Theory]
    [InlineData("days")]
    [InlineData("money")]
    [InlineData("reason")]
    [InlineData("base")]
    public async Task Invalid_input_never_creates_a_draft(string defect)
    {
        await using var f = await Ready(postgres);
        var content = defect == "days" ? Content(0) : Content();
        if (defect == "money") content = content with { Bands = [new(1, "director", 1.001m)] };
        var meta = new CommandMeta(Guid.NewGuid(), 1, defect == "reason" ? "" : "ทดสอบ")
        { Policy = new("Company") };
        Assert.Equal(400, (await Run(f, AuthorId, s => s.CreateDraftAsync(AuthorId, new(WorkspaceId, null),
            defect == "base" ? Guid.NewGuid() : null, content, meta, default))).Status);
        await using var db = f.CreateContext();
        Assert.Empty(await db.Set<PolicyRevision>().ToArrayAsync());
    }

    internal static PolicyContent Content(int days = 7) => new(days, [new(1, "director", 1000m)],
        [new(Guid.Parse("78000000-0000-0000-0000-000000000001"), "หมวดจำลอง", true, "วัน", 100m, ["receipt"])]);
    internal static async Task<FieldAdvanceFixture> Ready(PostgresFixture postgres)
    {
        var f = await FieldAdvanceFixture.CreateAsync(postgres);
        await f.SeedPeopleAsync();
        foreach (var (actor, person) in new[] { (AuthorId, AuthorPersonId), (ReviewerId, ReviewerPersonId), (RecipientId, RecipientPersonId) })
            foreach (var company in new[] { false, true })
            {
                await FieldAdvanceGrantTests.AuthorityAsync(f, actor, person, company: company);
                await using var db = f.CreateContext();
                var row = await db.Set<GrantProposal>().SingleAsync(x => x.RecipientId == actor && x.ProjectId == (company ? (Guid?)null : ProjectId));
                row.Capabilities = [company ? "policy.edit.company" : "policy.edit.project", "policy.review"];
                await db.SaveChangesAsync();
            }
        return f;
    }
    internal static PolicyService Service(FieldAdvanceFixture f, Tpr10DbContext db, RequestSession session, IAuditEventWriter? audit = null) =>
        new(db, FieldAdvanceAccessTests.Access(db, session, f.Clock), new FinancePersonResolver(db, f.Clock),
            audit ?? new AuditEventWriter(db, new CorrelationContext(), f.Clock), f.Clock);
    internal static async Task<FinanceResult<PolicyView>> Run(FieldAdvanceFixture f, Guid actor, Func<PolicyService, Task<FinanceResult<PolicyView>>> action)
    {
        var session = await f.SessionAsync(actor);
        await using var db = f.CreateContext();
        return await action(Service(f, db, session));
    }
    internal static CommandMeta Meta(PolicyView v) => new(Guid.NewGuid(), v.Version, "เหตุผลจำลอง")
    { Policy = new(v.Mode, v.SourcePolicyRevisionId, v.BaseCompanyVersionId, v.ExpectedActiveRevisionId, v.ExpectedActiveVersion, v.ContentHash) };
    internal static async Task<PolicyView> Create(FieldAdvanceFixture f, bool project = false, Guid? basis = null,
        PolicyView? source = null, PolicyView? active = null, Guid? actor = null, PolicyContent? content = null, string? mode = null)
    {
        var who = actor ?? AuthorId;
        var meta = new CommandMeta(Guid.NewGuid(), 1, "สร้างฉบับจำลอง")
        { Policy = new(mode ?? (project ? "Override" : "Company"), source?.Id, basis, active?.Id, active is null ? 0 : active.ExpectedActiveVersion + 1) };
        var result = await Run(f, who, s => s.CreateDraftAsync(who, new(WorkspaceId, project ? ProjectId : null), basis, content ?? Content(), meta, default));
        Assert.Equal(201, result.Status);
        return result.Value!;
    }
    internal static async Task<PolicyView> Update(FieldAdvanceFixture f, PolicyView v, PolicyContent content, Guid? actor = null, CommandMeta? meta = null)
    {
        var who = actor ?? AuthorId;
        var result = await Run(f, who, s => s.UpdateDraftAsync(who, v.Id, content, meta ?? Meta(v), default));
        Assert.Equal(200, result.Status);
        return result.Value!;
    }
    internal static async Task<PolicyView> Submit(FieldAdvanceFixture f, PolicyView v, Guid? actor = null)
    {
        var who = actor ?? AuthorId;
        var result = await Run(f, who, s => s.SubmitAsync(who, v.Id, Meta(v), default));
        Assert.Equal(200, result.Status);
        Assert.Equal("Pending", result.Value!.State);
        return result.Value;
    }
    internal static async Task<PolicyView> Review(FieldAdvanceFixture f, PolicyView v, string decision = "approve", Guid? actor = null)
    {
        var who = actor ?? ReviewerId;
        var result = await Run(f, who, s => s.ReviewAsync(who, v.Id, decision, Meta(v), default));
        Assert.Equal(200, result.Status);
        return result.Value!;
    }
}
