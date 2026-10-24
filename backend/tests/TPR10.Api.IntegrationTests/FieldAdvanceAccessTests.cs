using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.Identity;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Organization;
using TPR10.Api.Organization.Data;
using TPR10.Api.Scopes;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceAccessTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Resolver_uses_explicit_person_and_shared_account_identity()
    {
        await using var f = await CreateAsync(postgres);
        await f.SeedPeopleAsync();
        await using var db = f.CreateContext();
        db.Add(new FinanceAccountLink
        {
            Id = Guid.NewGuid(),
            UserId = AliasAccountId,
            PersonId = RecipientPersonId,
            WorkspaceId = WorkspaceId,
            CreatedBy = AuthorId,
            CreatedAtUtc = f.Clock.GetUtcNow(),
            Reason = "บัญชีจำลองคนเดียวกัน"
        });
        await db.SaveChangesAsync();
        var resolver = new FinancePersonResolver(db, f.Clock);
        Assert.Equal(RecipientPersonId, await resolver.ResolveAsync(RecipientId, WorkspaceId, default));
        Assert.Equal(RecipientPersonId, await resolver.ResolveAsync(AliasAccountId, WorkspaceId, default));
        Assert.Null(await resolver.ResolveAsync(RecipientId, OtherWorkspaceId, default));
    }

    [Theory]
    [InlineData("missing-link")]
    [InlineData("ended-link")]
    [InlineData("future-link")]
    [InlineData("ambiguous-link")]
    [InlineData("inactive-account")]
    [InlineData("inactive-workspace")]
    [InlineData("inactive-department")]
    [InlineData("ended-membership")]
    [InlineData("future-membership")]
    public async Task Resolver_fails_closed_without_unique_current_identity_and_membership(string state)
    {
        await using var f = await CreateAsync(postgres);
        await f.SeedPeopleAsync();
        await using var db = f.CreateContext();
        var link = await db.Set<FinanceAccountLink>().SingleAsync(x => x.UserId == RecipientId);
        var member = await db.Set<EmployeeMembership>().SingleAsync(x => x.UserId == RecipientId);
        var now = f.Clock.GetUtcNow();
        var userId = RecipientId;
        switch (state)
        {
            case "missing-link": userId = AliasAccountId; break;
            case "ended-link": link.EndedAtUtc = now; link.EndedBy = AuthorId; link.EndReason = "สิ้นสุดจำลอง"; link.Version++; break;
            case "future-link":
                userId = AliasAccountId;
                db.Add(new FinanceAccountLink
                {
                    Id = Guid.NewGuid(),
                    UserId = AliasAccountId,
                    PersonId = RecipientPersonId,
                    WorkspaceId = WorkspaceId,
                    CreatedBy = AuthorId,
                    CreatedAtUtc = now.AddMinutes(1),
                    Reason = "mapping ในอนาคตจำลอง"
                });
                break;
            case "ambiguous-link":
                // Corrupt only this disposable database to exercise defense beyond its unique index.
                await db.Database.ExecuteSqlRawAsync("""
                    DROP INDEX "IX_finance_account_links_workspace_id_user_id";
                    ALTER TABLE finance_account_links DISABLE TRIGGER finance_account_link_history;
                    """);
                db.Add(new FinanceAccountLink
                {
                    Id = Guid.NewGuid(),
                    UserId = RecipientId,
                    PersonId = AuthorPersonId,
                    WorkspaceId = WorkspaceId,
                    CreatedBy = AuthorId,
                    CreatedAtUtc = now,
                    Reason = "mapping กำกวมจำลอง"
                });
                break;
            case "inactive-account": (await db.Set<IdentityUser>().SingleAsync(x => x.Id == RecipientId)).IsActive = false; break;
            case "inactive-workspace": (await db.Set<Workspace>().SingleAsync(x => x.Id == WorkspaceId)).IsActive = false; break;
            case "inactive-department": (await db.Set<Department>().SingleAsync()).IsActive = false; break;
            case "ended-membership": member.ValidToUtc = now; member.EndedBy = AuthorId; break;
            case "future-membership": member.ValidFromUtc = now.AddMinutes(1); break;
        }
        await db.SaveChangesAsync();
        Assert.Null(await new FinancePersonResolver(db, f.Clock).ResolveAsync(userId, WorkspaceId, default));
        await f.ApprovedGrantAsync("report.read", ProjectId);
        if (userId != RecipientId)
            await db.Set<GrantProposal>().ExecuteUpdateAsync(x => x.SetProperty(p => p.RecipientId, userId));
        var current = await f.SessionAsync(userId);
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        await AssertBatchEquivalent(Access(db, current, f.Clock), userId, "report.read");
    }

    [Theory]
    [InlineData("policy.edit.company")]
    [InlineData("policy.edit.project")]
    [InlineData("policy.review")]
    [InlineData("grant.propose")]
    [InlineData("grant.review")]
    [InlineData("grant.revoke")]
    [InlineData("request.approve")]
    [InlineData("finance.check")]
    [InlineData("payment.record")]
    [InlineData("report.read")]
    public async Task Approved_capability_is_exact_to_account_person_and_scope(string capability)
    {
        await using var f = await CreateAsync(postgres);
        await f.SeedPeopleAsync();
        await f.ApprovedGrantAsync(capability, ProjectId);
        var current = await f.SessionAsync(RecipientId);
        await using var db = f.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        var access = Access(db, current, f.Clock);
        Assert.True((await access.CheckAsync(RecipientId, new(WorkspaceId, ProjectId), capability, default)).Allowed);
        await AssertBatchEquivalent(access, RecipientId, capability);
        Assert.False((await access.CheckAsync(RecipientId, new(WorkspaceId, Guid.Parse("71000000-0000-0000-0000-000000000012")), capability, default)).Allowed);
        Assert.False((await access.CheckAsync(RecipientId, new(OtherWorkspaceId, OtherProjectId), capability, default)).Allowed);
        Assert.False((await access.CheckAsync(RecipientId, new(WorkspaceId, null), capability, default)).Allowed);
        Assert.False((await access.CheckAsync(AuthorId, new(WorkspaceId, ProjectId), capability, default)).Allowed);
        Assert.False((await access.CheckAsync(RecipientId, new(WorkspaceId, ProjectId), "unknown", default)).Allowed);
    }

    [Theory]
    [InlineData("missing-session")]
    [InlineData("revoked-session")]
    [InlineData("expired-session")]
    [InlineData("idle-session")]
    [InlineData("security-version")]
    [InlineData("inactive-account")]
    [InlineData("restricted-stage")]
    [InlineData("password-change")]
    [InlineData("missing-mfa")]
    [InlineData("stale-mfa")]
    [InlineData("future-mfa")]
    [InlineData("revoked-factor")]
    public async Task Approved_grant_never_bypasses_current_session_or_mfa(string state)
    {
        await using var f = await CreateAsync(postgres);
        await f.SeedPeopleAsync();
        await f.ApprovedGrantAsync("payment.record", ProjectId);
        var current = await f.SessionAsync(RecipientId);
        await using var db = f.CreateContext();
        var session = await db.Set<IdentitySession>().SingleAsync();
        var now = f.Clock.GetUtcNow();
        switch (state)
        {
            case "missing-session": current.Entity = null; break;
            case "revoked-session": session.RevokedAtUtc = now; break;
            case "expired-session": session.CreatedAtUtc = now.AddHours(-1); session.ExpiresAtUtc = now; break;
            case "idle-session": session.LastSeenAtUtc = now.AddMinutes(-30); break;
            case "security-version": (await db.Set<IdentityUser>().SingleAsync(x => x.Id == RecipientId)).SecurityVersion++; break;
            case "inactive-account": (await db.Set<IdentityUser>().SingleAsync(x => x.Id == RecipientId)).IsActive = false; break;
            case "restricted-stage": session.Stage = SessionStage.MfaEnrollmentRequired; break;
            case "password-change": db.Add(new LocalCredential { UserId = RecipientId, PasswordHash = "test-only", MustChangePassword = true }); break;
            case "missing-mfa": session.MfaVerifiedAtUtc = null; break;
            case "stale-mfa": session.MfaVerifiedAtUtc = now.AddMinutes(-15); break;
            case "future-mfa": session.MfaVerifiedAtUtc = now.AddSeconds(1); break;
            case "revoked-factor": (await db.Set<MfaFactor>().SingleAsync()).RevokedAtUtc = now; break;
        }
        await db.SaveChangesAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        Assert.False((await Access(db, current, f.Clock).CheckAsync(RecipientId, new(WorkspaceId, ProjectId), "payment.record", default)).Allowed);
        await AssertBatchEquivalent(Access(db, current, f.Clock), RecipientId, "payment.record");
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("pending")]
    [InlineData("rejected-review")]
    [InlineData("future")]
    [InlineData("expired")]
    [InlineData("person-mismatch")]
    [InlineData("proposer-recipient")]
    [InlineData("reviewer-recipient")]
    [InlineData("reviewer-proposer")]
    [InlineData("project-inactive")]
    [InlineData("delegable-only")]
    [InlineData("future-assignment")]
    [InlineData("future-review")]
    [InlineData("future-proposal")]
    public async Task Retained_or_invalid_grant_cannot_authorize(string state)
    {
        await using var f = await CreateAsync(postgres);
        await f.SeedPeopleAsync();
        await f.ApprovedGrantAsync("payment.record", ProjectId);
        var current = await f.SessionAsync(RecipientId);
        await using var db = f.CreateContext();
        var grant = await db.Set<TPR10.Api.FieldAdvances.Data.GrantAssignment>().SingleAsync();
        var proposal = await db.Set<GrantProposal>().SingleAsync();
        var review = await db.Set<GrantReview>().SingleAsync();
        var now = f.Clock.GetUtcNow();
        switch (state)
        {
            case "revoked": grant.RevokedAtUtc = now; grant.RevokedBy = AuthorId; grant.RevocationReason = "ถอนจำลอง"; break;
            case "pending": proposal.State = "Pending"; break;
            case "rejected-review": review.Outcome = "Rejected"; break;
            case "future": proposal.StartsAtUtc = now.AddSeconds(1); break;
            case "expired": proposal.EndsAtUtc = now; break;
            case "person-mismatch": proposal.RecipientPersonId = AuthorPersonId; break;
            case "proposer-recipient": proposal.ProposedByPersonId = RecipientPersonId; break;
            case "reviewer-recipient": review.ReviewerPersonId = RecipientPersonId; break;
            case "reviewer-proposer": review.ReviewerPersonId = AuthorPersonId; break;
            case "project-inactive": (await db.Set<Project>().SingleAsync(x => x.Id == ProjectId)).IsActive = false; break;
            case "delegable-only": proposal.Capabilities = ["grant.propose"]; proposal.DelegableCapabilities = ["payment.record"]; break;
            case "future-assignment": grant.CreatedAtUtc = now.AddSeconds(1); break;
            case "future-review": review.CreatedAtUtc = now.AddSeconds(1); break;
            case "future-proposal": proposal.CreatedAtUtc = now.AddSeconds(1); break;
        }
        await db.SaveChangesAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        Assert.False((await Access(db, current, f.Clock).CheckAsync(RecipientId, new(WorkspaceId, ProjectId), "payment.record", default)).Allowed);
        await AssertBatchEquivalent(Access(db, current, f.Clock), RecipientId, "payment.record");
    }

    [Fact]
    public async Task Authorization_requires_caller_transaction()
    {
        await using var f = await CreateAsync(postgres);
        await using var db = f.CreateContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Access(db, new(), f.Clock)
            .CheckAsync(RecipientId, new(WorkspaceId, ProjectId), "report.read", default));
    }

    [Theory]
    [InlineData("policy.edit.company")]
    [InlineData("policy.edit.project")]
    [InlineData("policy.review")]
    [InlineData("grant.propose")]
    [InlineData("grant.review")]
    [InlineData("grant.revoke")]
    [InlineData("request.approve")]
    [InlineData("finance.check")]
    [InlineData("payment.record")]
    [InlineData("report.read")]
    public async Task Company_scope_is_never_a_project_wildcard_or_another_capability(string capability)
    {
        await using var f = await CreateAsync(postgres);
        await f.SeedPeopleAsync();
        await f.ApprovedGrantAsync(capability, null);
        var current = await f.SessionAsync(RecipientId);
        await using var db = f.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        var access = Access(db, current, f.Clock);
        Assert.True((await access.CheckAsync(RecipientId, new(WorkspaceId, null), capability, default)).Allowed);
        await AssertBatchEquivalent(access, RecipientId, capability);
        Assert.False((await access.CheckAsync(RecipientId, new(WorkspaceId, ProjectId), capability, default)).Allowed);
        foreach (var other in new[] { "policy.edit.company", "policy.edit.project", "policy.review", "grant.propose", "grant.review",
            "grant.revoke", "request.approve", "finance.check", "payment.record", "report.read" }.Where(x => x != capability))
            Assert.False((await access.CheckAsync(RecipientId, new(WorkspaceId, null), other, default)).Allowed);
    }

    [Fact]
    public async Task Same_context_observes_revocation_and_expiry_without_using_tracked_grants()
    {
        await using var f = await CreateAsync(postgres);
        await f.SeedPeopleAsync();
        var grantId = await f.ApprovedGrantAsync("report.read", ProjectId);
        var current = await f.SessionAsync(RecipientId);
        await using var db = f.CreateContext();
        // Deliberately retain a stale tracked entity while a separate transaction revokes it.
        await db.Set<TPR10.Api.FieldAdvances.Data.GrantAssignment>().SingleAsync(x => x.Id == grantId);
        async Task<bool> AllowedAsync()
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
            var access = Access(db, current, f.Clock);
            await AssertBatchEquivalent(access, RecipientId, "report.read");
            return (await access.CheckAsync(RecipientId, new(WorkspaceId, ProjectId), "report.read", default)).Allowed;
        }
        Assert.True(await AllowedAsync());
        await using (var change = f.CreateContext())
        {
            await using var tx = await change.Database.BeginTransactionAsync();
            await change.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
            var grant = await change.Set<TPR10.Api.FieldAdvances.Data.GrantAssignment>().SingleAsync(x => x.Id == grantId);
            grant.RevokedAtUtc = f.Clock.GetUtcNow(); grant.RevokedBy = AuthorId; grant.RevocationReason = "ถอนจำลอง"; grant.Version++;
            await change.SaveChangesAsync();
            await tx.CommitAsync();
        }
        Assert.False(await AllowedAsync());
        await f.ApprovedGrantAsync("report.read", ProjectId);
        await using (var change = f.CreateContext())
        {
            var proposal = await change.Set<GrantProposal>().SingleAsync(p => change.Set<TPR10.Api.FieldAdvances.Data.GrantAssignment>()
                .Any(a => a.ProposalId == p.Id && a.RevokedAtUtc == null));
            proposal.StartsAtUtc = f.Clock.GetUtcNow();
            proposal.EndsAtUtc = f.Clock.GetUtcNow().AddSeconds(1);
            await change.SaveChangesAsync();
        }
        Assert.True(await AllowedAsync());
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(await AllowedAsync());
    }

    [Fact]
    public async Task Unrecognized_capability_is_denied_even_if_present_in_an_approved_grant()
    {
        await using var f = await CreateAsync(postgres);
        await f.SeedPeopleAsync();
        foreach (var capability in new[] { "unknown", "*", "REPORT.READ", "" })
            await f.ApprovedGrantAsync(capability, ProjectId);
        var current = await f.SessionAsync(RecipientId);
        await using var db = f.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        foreach (var capability in new[] { "unknown", "*", "REPORT.READ", "" })
            Assert.False((await Access(db, current, f.Clock).CheckAsync(RecipientId, new(WorkspaceId, ProjectId), capability, default)).Allowed);
    }

    internal static FinanceAccess Access(Tpr10DbContext db, RequestSession current, TimeProvider clock) =>
        new(db, new ScopeAccess(db, current, new EffectiveRolePolicy(db), clock), new FinancePersonResolver(db, clock), clock);

    private static async Task AssertBatchEquivalent(FinanceAccess access, Guid actor, string capability)
    {
        FinanceScope[] scopes = [new(WorkspaceId, ProjectId), new(WorkspaceId, null), new(OtherWorkspaceId, ProjectId),
            new(WorkspaceId, OtherProjectId), new(OtherWorkspaceId, OtherProjectId), new(Guid.Empty, ProjectId), new(WorkspaceId, Guid.Empty)];
        string[] capabilities = [capability, "unknown", "request.create", "request.delegate"];
        var batch = await access.CheckManyAsync(actor, scopes, capabilities, default);
        foreach (var scope in scopes)
            foreach (var cap in capabilities)
                Assert.Equal((await access.CheckAsync(actor, scope, cap, default)).Allowed, batch[scope].Contains(cap));
        var mismatch = await access.CheckManyAsync(AuthorId, scopes, capabilities, default);
        Assert.All(mismatch.Values, Assert.Empty);
    }
}
