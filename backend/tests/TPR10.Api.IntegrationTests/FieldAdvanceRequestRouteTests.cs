using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Policies;
using TPR10.Api.FieldAdvances.Requests;
using TPR10.Api.Identity.Data;
using TPR10.Api.Organization.Data;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvanceGrantTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceRequestRouteTests(PostgresFixture postgres)
{
    // Wrong owner/delegate origin or a candidate-session requirement breaks this test.
    [Fact]
    public async Task Resolves_owner_supervisor_not_delegate_supervisor()
    {
        await using var f = await ReadyRouteAsync();
        var ids = await RequestAsync(f, delegated: true);
        await LineAsync(f, AuthorId, ReviewerId);
        await LineAsync(f, RecipientId, AuthorId);
        await GrantAsync(f, ReviewerId, ReviewerPersonId, "supervisor");
        var choice = await ResolveAsync(f, ids, "supervisor");
        Assert.Equal(ReviewerPersonId, choice.PersonId);
        Assert.Equal(ReviewerId, choice.AccountId);
        Assert.Null(choice.HoldCode);
        Assert.Equal([ReviewerPersonId], choice.TraversedPersonIds);
        await using var db = f.CreateContext();
        Assert.Empty(await db.Set<IdentitySession>().ToArrayAsync());
        Assert.Empty(await db.Set<AdvanceApprovalStep>().ToArrayAsync());
        Assert.Empty(await db.Set<AdvanceRequestEvent>().ToArrayAsync());
        Assert.Equal("Draft", (await db.Set<AdvanceRequest>().SingleAsync()).State);
        Assert.Equal(1, (await db.Set<AdvanceRequest>().SingleAsync()).Version);
    }

    // Escalation must retain the requested level and revision-local separation.
    [Fact]
    public async Task Same_person_escalates_without_skipping_level()
    {
        await using var f = await ReadyRouteAsync();
        var ids = await RequestAsync(f);
        await GrantAsync(f, ReviewerId, ReviewerPersonId, "project-manager", primary: true);
        await GrantAsync(f, RecipientId, RecipientPersonId, "project-manager");
        await LineAsync(f, ReviewerId, RecipientId);
        await ApprovedAsync(f, ids, ReviewerId, ReviewerPersonId);
        var choice = await ResolveAsync(f, ids, "project-manager");
        Assert.Equal(RecipientPersonId, choice.PersonId);
        Assert.Equal([ReviewerPersonId, RecipientPersonId], choice.TraversedPersonIds);
        Assert.Null(choice.HoldCode);
        Assert.Equal(["supervisor", "project-manager", "director"], ApprovalLevelEvaluator.Evaluate(100m, Snapshot()).Value!);
        await using var db = f.CreateContext();
        var step = await db.Set<AdvanceApprovalStep>().SingleAsync();
        Assert.Equal("supervisor", step.LevelCode);
        Assert.Equal("Approved", step.Outcome);
        Assert.Equal(ReviewerPersonId, step.DecidedByPersonId);
    }

    [Fact]
    public async Task High_limit_without_level_denied()
    {
        await using var f = await ReadyRouteAsync();
        var ids = await RequestAsync(f);
        await LineAsync(f, AuthorId, ReviewerId);
        await GrantAsync(f, ReviewerId, ReviewerPersonId, "director", limit: 10000m);
        Held(await ResolveAsync(f, ids, "supervisor"));
    }

    [Theory]
    [InlineData("cycle")]
    [InlineData("ambiguous")]
    [InlineData("missing-primary")]
    public async Task Cycle_or_ambiguous_role_holds(string scenario)
    {
        await using var f = await ReadyRouteAsync();
        var ids = await RequestAsync(f);
        await GrantAsync(f, ReviewerId, ReviewerPersonId, "project-manager", primary: scenario != "missing-primary");
        if (scenario == "ambiguous")
            await GrantAsync(f, RecipientId, RecipientPersonId, "project-manager", primary: true);
        if (scenario == "cycle")
        {
            await ApprovedAsync(f, ids, ReviewerId, ReviewerPersonId);
            await LineAsync(f, ReviewerId, AuthorId);
            await LineAsync(f, AuthorId, ReviewerId);
        }
        var choice = await ResolveAsync(f, ids, "project-manager");
        Held(choice);
        if (scenario == "cycle") Assert.Equal([ReviewerPersonId, AuthorPersonId], choice.TraversedPersonIds);
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("cent-short", false)]
    [InlineData("sum-limits", false)]
    [InlineData("split-level", false)]
    [InlineData("expired", false)]
    [InlineData("future", false)]
    [InlineData("revoked", false)]
    [InlineData("other-project", false)]
    [InlineData("company", false)]
    [InlineData("inactive-user", false)]
    [InlineData("inactive-project", false)]
    [InlineData("ended-employment", false)]
    [InlineData("missing-identity", false)]
    [InlineData("ended-identity", false)]
    [InlineData("legacy-level", false)]
    [InlineData("future-review", false)]
    [InlineData("future-assignment", false)]
    public async Task Candidate_requires_current_identity_and_one_whole_project_grant(string defect, bool allowed)
    {
        await using var f = await ReadyRouteAsync();
        var ids = await RequestAsync(f);
        var candidate = defect == "missing-identity" ? AliasAccountId : ReviewerId;
        await LineAsync(f, AuthorId, candidate);
        var grant = await GrantAsync(f, candidate, ReviewerPersonId, "supervisor",
            limit: defect is "sum-limits" or "split-level" ? 60m : defect == "cent-short" ? 99.99m : 100m,
            project: defect == "other-project" ? Guid.Parse("71000000-0000-0000-0000-000000000012") : null,
            company: defect == "company");
        if (defect is "sum-limits" or "split-level")
            await GrantAsync(f, ReviewerId, ReviewerPersonId, defect == "split-level" ? "director" : "supervisor",
                limit: defect == "split-level" ? 10000m : 60m);
        await using (var db = f.CreateContext())
        {
            var assignment = await db.Set<GrantAssignment>().SingleAsync(x => x.Id == grant);
            var source = await db.Set<GrantProposal>().SingleAsync(x => x.Id == assignment.ProposalId);
            var now = f.Clock.GetUtcNow();
            switch (defect)
            {
                case "expired": source.EndsAtUtc = now; break;
                case "future": source.StartsAtUtc = now.AddSeconds(1); break;
                case "legacy-level": source.ApprovalLevels = []; break;
                case "future-review": (await db.Set<GrantReview>().SingleAsync(x => x.Id == assignment.ReviewId)).CreatedAtUtc = now.AddSeconds(1); break;
                case "future-assignment": assignment.CreatedAtUtc = now.AddSeconds(1); break;
                case "revoked": assignment.RevokedAtUtc = now; assignment.RevokedBy = AuthorId; assignment.RevocationReason = "test"; assignment.Version++; break;
                case "inactive-user": (await db.Set<IdentityUser>().SingleAsync(x => x.Id == ReviewerId)).IsActive = false; break;
                case "inactive-project": (await db.Set<Project>().SingleAsync(x => x.Id == ProjectId)).IsActive = false; break;
                case "ended-employment": (await db.Set<EmployeeMembership>().SingleAsync(x => x.UserId == ReviewerId)).ValidToUtc = now; break;
                case "ended-identity":
                    var link = await db.Set<FinanceAccountLink>().SingleAsync(x => x.UserId == ReviewerId);
                    link.EndedAtUtc = now; link.EndedBy = AuthorId; link.EndReason = "test"; link.Version++; break;
            }
            await db.SaveChangesAsync();
        }
        var choice = await ResolveAsync(f, ids, "supervisor");
        if (allowed) { Assert.Equal(ReviewerPersonId, choice.PersonId); Assert.Null(choice.HoldCode); }
        else Held(choice);
    }

    [Theory]
    [InlineData("project-manager")]
    [InlineData("director")]
    public async Task Aliases_merge_before_primary_ambiguity_and_fallback_is_not_primary(string level)
    {
        await using var f = await ReadyRouteAsync();
        var ids = await RequestAsync(f);
        await AliasAsync(f, ReviewerPersonId);
        await GrantAsync(f, ReviewerId, ReviewerPersonId, level, primary: true);
        await GrantAsync(f, AliasAccountId, ReviewerPersonId, level, primary: true);
        await GrantAsync(f, RecipientId, RecipientPersonId, level);
        var choice = await ResolveAsync(f, ids, level);
        Assert.Equal(ReviewerPersonId, choice.PersonId);
        Assert.Contains(choice.AccountId, new Guid?[] { ReviewerId, AliasAccountId });
        Assert.Null(choice.HoldCode);
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("Delegate")]
    [InlineData("Checker")]
    [InlineData("Payer")]
    [InlineData("Approver")]
    public async Task Retained_history_applies_to_aliases_but_old_revision_approval_is_allowed(string role)
    {
        await using var f = await ReadyRouteAsync();
        var ids = await RequestAsync(f);
        await AliasAsync(f, ReviewerPersonId);
        await GrantAsync(f, AliasAccountId, ReviewerPersonId, "project-manager", primary: true);
        await using (var db = f.CreateContext())
        {
            db.Add(new AdvanceRequestParticipant
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                ProjectId = ProjectId,
                RequestId = ids.Request,
                PersonId = ReviewerPersonId,
                Role = role,
                CreatedBy = AuthorId
            });
            await db.SaveChangesAsync();
        }
        var choice = await ResolveAsync(f, ids, "project-manager");
        if (role == "Approver") Assert.Equal(ReviewerPersonId, choice.PersonId);
        else Held(choice);
    }

    [Fact]
    public async Task Supervisor_alias_lines_merge_by_person_but_distinct_supervisors_hold()
    {
        await using var f = await ReadyRouteAsync();
        var ids = await RequestAsync(f);
        await AliasAsync(f, AuthorPersonId);
        await LineAsync(f, AuthorId, ReviewerId);
        await LineAsync(f, AliasAccountId, ReviewerId);
        await GrantAsync(f, ReviewerId, ReviewerPersonId, "supervisor");
        Assert.Equal(ReviewerPersonId, (await ResolveAsync(f, ids, "supervisor")).PersonId);
        await using (var db = f.CreateContext())
        {
            (await db.Set<ReportingLine>().SingleAsync(x => x.EmployeeUserId == AliasAccountId)).SupervisorUserId = RecipientId;
            await db.SaveChangesAsync();
        }
        Held(await ResolveAsync(f, ids, "supervisor"));
    }

    [Fact]
    public async Task Unknown_level_or_revision_and_missing_transaction_fail_closed()
    {
        await using var f = await ReadyRouteAsync();
        var ids = await RequestAsync(f);
        await GrantAsync(f, ReviewerId, ReviewerPersonId, "unrequired", primary: true);
        Held(await ResolveAsync(f, ids, "unrequired"));
        Held(await ResolveAsync(f, (ids.Request, Guid.NewGuid()), "supervisor"));
        await using var db = f.CreateContext();
        Held(await InvokeAsync(db, f, ids, "supervisor"));
    }

    [Theory]
    [InlineData("unmapped-primary")]
    [InlineData("under-limit-primary")]
    public async Task Ineligible_primary_does_not_silently_remove_ambiguity(string defect)
    {
        await using var f = await ReadyRouteAsync();
        var ids = await RequestAsync(f);
        await GrantAsync(f, ReviewerId, ReviewerPersonId, "project-manager", primary: true);
        await GrantAsync(f, defect == "unmapped-primary" ? AliasAccountId : RecipientId,
            RecipientPersonId, "project-manager", primary: true, limit: 99.99m);
        Held(await ResolveAsync(f, ids, "project-manager"));
    }

    [Fact]
    public async Task Current_primary_replacement_is_reread_and_ordinary_authority_is_not_promoted()
    {
        await using var f = await ReadyRouteAsync();
        var ids = await RequestAsync(f);
        var first = await GrantAsync(f, ReviewerId, ReviewerPersonId, "project-manager", primary: true);
        var next = await GrantAsync(f, RecipientId, RecipientPersonId, "project-manager");
        await using var db = f.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        var resolver = new ApprovalRouteResolver(db, new FinancePersonResolver(db, f.Clock), f.Clock);
        Assert.Equal(ReviewerPersonId, (await resolver.ResolveAsync(ids.Request, ids.Revision, "project-manager", default)).PersonId);
        var assignment = await db.Set<GrantAssignment>().SingleAsync(x => x.Id == first);
        assignment.RevokedAtUtc = f.Clock.GetUtcNow(); assignment.RevokedBy = AuthorId; assignment.RevocationReason = "test"; assignment.Version++;
        await db.SaveChangesAsync();
        Held(await resolver.ResolveAsync(ids.Request, ids.Revision, "project-manager", default));
        var nextAssignment = await db.Set<GrantAssignment>().SingleAsync(x => x.Id == next);
        (await db.Set<GrantProposal>().SingleAsync(x => x.Id == nextAssignment.ProposalId)).PrimaryApprovalLevels = ["project-manager"];
        await db.SaveChangesAsync();
        Assert.Equal(RecipientPersonId, (await resolver.ResolveAsync(ids.Request, ids.Revision, "project-manager", default)).PersonId);
    }

    // Stale grant/identity caches would return the former candidate after the writer commits.
    [Theory]
    [InlineData("revoke")]
    [InlineData("identity")]
    [InlineData("level")]
    [InlineData("amount")]
    [InlineData("reporting")]
    public async Task Routing_reads_committed_current_rights_after_caller_identity_lock(string change)
    {
        await using var f = await ReadyRouteAsync();
        var ids = await RequestAsync(f);
        await LineAsync(f, AuthorId, ReviewerId);
        var grant = await GrantAsync(f, ReviewerId, ReviewerPersonId, "supervisor");
        Assert.Equal(ReviewerPersonId, (await ResolveAsync(f, ids, "supervisor")).PersonId);
        await using var writer = f.CreateContext();
        await using var tx = await writer.Database.BeginTransactionAsync();
        await writer.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        var pending = ResolveAsync(f, ids, "supervisor");
        await using var probe = f.CreateContext();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (!await probe.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS(SELECT 1 FROM pg_locks WHERE locktype='advisory' AND objid=7241002 AND NOT granted) AS "Value"
                """).SingleAsync(deadline.Token))
            {
                Assert.False(pending.IsCompleted);
                await Task.Delay(20, deadline.Token);
            }
            var assignment = await writer.Set<GrantAssignment>().SingleAsync(x => x.Id == grant);
            var source = await writer.Set<GrantProposal>().SingleAsync(x => x.Id == assignment.ProposalId);
            switch (change)
            {
                case "revoke":
                    assignment.RevokedAtUtc = f.Clock.GetUtcNow(); assignment.RevokedBy = AuthorId;
                    assignment.RevocationReason = "test"; assignment.Version++; break;
                case "identity":
                    var link = await writer.Set<FinanceAccountLink>().SingleAsync(x => x.UserId == ReviewerId);
                    link.EndedAtUtc = f.Clock.GetUtcNow(); link.EndedBy = AuthorId; link.EndReason = "test"; link.Version++; break;
                case "level": source.ApprovalLevels = ["director"]; break;
                case "amount": source.ApprovalLimit = 99.99m; break;
                case "reporting": (await writer.Set<ReportingLine>().SingleAsync()).ValidToUtc = f.Clock.GetUtcNow(); break;
            }
            await writer.SaveChangesAsync();
        }
        finally { await tx.CommitAsync(); }
        Held(await pending.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Expiry_at_exact_instant_rechecks_same_resolver_and_caller_transaction_is_retained()
    {
        await using var f = await ReadyRouteAsync();
        var ids = await RequestAsync(f);
        await LineAsync(f, AuthorId, ReviewerId);
        await GrantAsync(f, ReviewerId, ReviewerPersonId, "supervisor");
        await using var db = f.CreateContext();
        var source = await db.Set<GrantProposal>().SingleAsync();
        source.EndsAtUtc = f.Clock.GetUtcNow().AddSeconds(1);
        await db.SaveChangesAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        var resolver = new ApprovalRouteResolver(db, new FinancePersonResolver(db, f.Clock), f.Clock);
        Assert.Equal(ReviewerPersonId, (await resolver.ResolveAsync(ids.Request, ids.Revision, "supervisor", default)).PersonId);
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        Held(await resolver.ResolveAsync(ids.Request, ids.Revision, "supervisor", default));
        Assert.Same(tx, db.Database.CurrentTransaction);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    private async Task<FieldAdvanceFixture> ReadyRouteAsync()
    {
        var f = await CreateAsync(postgres);
        await f.SeedPoliciesAsync();
        return f;
    }

    private static PolicySnapshot Snapshot() => new(CompanyRevisionId, ProjectRevisionId,
        new(7, [new(1, "director", 1000m)], [new(Guid.Parse("73000000-0000-0000-0000-000000000021"), "test", true, null, null, ["receipt"])]), new('a', 64));

    private static async Task<(Guid Request, Guid Revision)> RequestAsync(FieldAdvanceFixture f, bool delegated = false)
    {
        await using var db = f.CreateContext();
        var request = new AdvanceRequest
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            OwnerPersonId = AuthorPersonId,
            DelegatePersonId = delegated ? RecipientPersonId : null,
            CreatedBy = AuthorId
        };
        var revision = new AdvanceRequestRevision
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            RequestId = request.Id,
            RevisionNumber = 1,
            IsCurrent = true,
            Total = 100m,
            ContentJson = "{}",
            ContentHash = new('a', 64),
            CreatedBy = AuthorId,
            CreatedByPersonId = AuthorPersonId,
            CompanyPolicyRevisionId = CompanyRevisionId,
            ProjectPolicyRevisionId = ProjectRevisionId,
            PolicySnapshotJson = JsonSerializer.Serialize(Snapshot()),
            PolicyHash = new('a', 64)
        };
        db.AddRange(request, revision);
        await db.SaveChangesAsync();
        return (request.Id, revision.Id);
    }

    private static async Task<Guid> GrantAsync(FieldAdvanceFixture f, Guid account, Guid person, string level,
        bool primary = false, decimal limit = 100m, Guid? project = null, bool company = false)
    {
        var id = await AuthorityAsync(f, account, person, limit, company, project);
        await using var db = f.CreateContext();
        var assignment = await db.Set<GrantAssignment>().SingleAsync(x => x.Id == id);
        var source = await db.Set<GrantProposal>().SingleAsync(x => x.Id == assignment.ProposalId);
        source.Capabilities = ["request.approve"]; source.ApprovalLevels = company ? [] : [level]; source.PrimaryApprovalLevels = primary ? [level] : [];
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task LineAsync(FieldAdvanceFixture f, Guid employee, Guid supervisor)
    {
        await using var db = f.CreateContext();
        db.Add(new ReportingLine
        {
            Id = Guid.NewGuid(),
            EmployeeMembershipId = (await db.Set<EmployeeMembership>().SingleAsync(x => x.UserId == employee)).Id,
            EmployeeUserId = employee,
            SupervisorUserId = supervisor,
            ValidFromUtc = f.Clock.GetUtcNow().AddDays(-1),
            CreatedBy = AuthorId,
            Reason = "test"
        });
        await db.SaveChangesAsync();
    }

    private static async Task AliasAsync(FieldAdvanceFixture f, Guid person)
    {
        await using var db = f.CreateContext();
        db.Add(new FinanceAccountLink
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            UserId = AliasAccountId,
            PersonId = person,
            CreatedAtUtc = f.Clock.GetUtcNow(),
            CreatedBy = AuthorId,
            Reason = "test alias"
        });
        await db.SaveChangesAsync();
    }

    private static async Task ApprovedAsync(FieldAdvanceFixture f, (Guid Request, Guid Revision) ids, Guid account, Guid person)
    {
        await using var db = f.CreateContext();
        db.Add(new AdvanceApprovalStep
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            RequestId = ids.Request,
            RevisionId = ids.Revision,
            StepOrder = 1,
            LevelCode = "supervisor",
            Outcome = "Approved",
            DecidedBy = account,
            DecidedByPersonId = person,
            DecidedAtUtc = f.Clock.GetUtcNow(),
            DecisionReason = "test"
        });
        await db.SaveChangesAsync();
    }

    private static void Held(RouteChoice choice)
    {
        Assert.Null(choice.PersonId); Assert.Null(choice.AccountId); Assert.False(string.IsNullOrWhiteSpace(choice.HoldCode));
    }

    private static async Task<RouteChoice> ResolveAsync(FieldAdvanceFixture f, (Guid Request, Guid Revision) ids, string level)
    {
        await using var db = f.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        return await InvokeAsync(db, f, ids, level);
    }

    private static Task<RouteChoice> InvokeAsync(Tpr10DbContext db, FieldAdvanceFixture f,
        (Guid Request, Guid Revision) ids, string level) =>
        new ApprovalRouteResolver(db, new FinancePersonResolver(db, f.Clock), f.Clock)
            .ResolveAsync(ids.Request, ids.Revision, level, default);
}
