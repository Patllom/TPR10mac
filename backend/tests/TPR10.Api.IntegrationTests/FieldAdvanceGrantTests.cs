using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Grants;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Authorization;
using TPR10.Api.Organization.Data;
using TPR10.Api.Scopes;
using TPR10.Api.Scopes.Assignments;
using GrantAssignment = TPR10.Api.FieldAdvances.Data.GrantAssignment;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceGrantTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("propose", "session")]
    [InlineData("review", "session")]
    [InlineData("revoke", "session")]
    [InlineData("propose", "mfa")]
    [InlineData("review", "mfa")]
    [InlineData("revoke", "mfa")]
    public async Task Workflow_checks_current_session_and_mfa_before_writing(string operation, string defect)
    {
        await using var f = await ReadyAsync(postgres);
        var id = Guid.Empty;
        if (operation != "propose")
        {
            id = (await ProposeAsync(f)).Value!.Id;
            if (operation == "revoke")
            {
                Assert.Equal(200, (await ReviewAsync(f, id)).Status);
                id = await AssignmentIdAsync(f, id);
            }
        }
        var actor = operation == "review" ? ReviewerId : AuthorId;
        var current = await f.SessionAsync(actor);
        await using (var mutate = f.CreateContext())
        {
            var session = await mutate.Set<IdentitySession>().SingleAsync(x => x.Id == current.Entity!.Id);
            if (defect == "session") session.RevokedAtUtc = f.Clock.GetUtcNow();
            else session.MfaVerifiedAtUtc = null;
            await mutate.SaveChangesAsync();
        }
        await using var db = f.CreateContext();
        var receipts = await db.Set<FinanceCommandReceipt>().CountAsync();
        var result = operation switch
        {
            "propose" => await Service(f, db, current).ProposeAsync(actor, Spec(f), Meta(), default),
            "review" => await Service(f, db, current).ReviewAsync(actor, id, "approve", Meta(), default),
            _ => await Lifecycle(f, db, current).RevokeAsync(actor, id, Meta(), default)
        };
        Assert.Equal(403, result.Status);
        Assert.Equal(receipts, await db.Set<FinanceCommandReceipt>().CountAsync());
    }

    [Theory]
    [InlineData("proposer")]
    [InlineData("recipient")]
    public async Task Review_rechecks_retained_person_identity(string changed)
    {
        await using var f = await ReadyAsync(postgres);
        var proposal = (await ProposeAsync(f)).Value!;
        await using (var db = f.CreateContext())
        {
            var account = changed == "proposer" ? AuthorId : RecipientId;
            var link = await db.Set<FinanceAccountLink>().SingleAsync(x => x.UserId == account);
            link.EndedAtUtc = f.Clock.GetUtcNow(); link.EndedBy = ReviewerId; link.EndReason = "สิ้นสุดตัวตนจำลอง"; link.Version++;
            await db.SaveChangesAsync();
        }
        Assert.Equal(403, (await ReviewAsync(f, proposal.Id)).Status);
        await using var check = f.CreateContext();
        Assert.False(await check.Set<GrantAssignment>().AnyAsync(x => x.ProposalId == proposal.Id));
    }

    [Theory]
    [InlineData(60, 403)]
    [InlineData(100, 201)]
    public async Task Each_capability_needs_its_own_whole_limit_coverage(int secondLimit, int expected)
    {
        await using var f = await ReadyAsync(postgres);
        await using (var db = f.CreateContext())
        {
            var richSource = await db.Set<GrantProposal>().SingleAsync(x => x.RecipientId == AuthorId);
            richSource.DelegableCapabilities = ["report.read"];
            await db.SaveChangesAsync();
        }
        var second = await AuthorityAsync(f, AuthorId, AuthorPersonId, secondLimit);
        await using (var db = f.CreateContext())
        {
            var source = await db.Set<GrantProposal>().SingleAsync(p => db.Set<GrantAssignment>().Any(a => a.Id == second && a.ProposalId == p.Id));
            source.DelegableCapabilities = ["payment.record"];
            await db.SaveChangesAsync();
        }
        var result = await ProposeAsync(f, Spec(f) with { Capabilities = ["report.read", "payment.record"], ApprovalLimit = 100m });
        Assert.Equal(expected, result.Status);
    }

    [Theory]
    [InlineData("recipient")]
    [InlineData("scope")]
    [InlineData("capability")]
    [InlineData("delegable")]
    [InlineData("amount")]
    [InlineData("period")]
    public async Task Receipt_binds_the_entire_proposal_payload(string changed)
    {
        await using var f = await ReadyAsync(postgres);
        await AuthorityAsync(f, AuthorId, AuthorPersonId, company: true);
        var spec = Spec(f);
        var meta = Meta();
        Assert.Equal(201, (await ProposeAsync(f, spec, meta)).Status);
        var altered = changed switch
        {
            "recipient" => spec with { RecipientId = ReviewerId },
            "scope" => spec with { Scope = new(WorkspaceId, null) },
            "capability" => spec with { Capabilities = ["payment.record"] },
            "delegable" => spec with { DelegableCapabilities = ["payment.record"] },
            "amount" => spec with { ApprovalLimit = 50m },
            _ => spec with { EndsAt = spec.EndsAt!.Value.AddDays(1) }
        };
        Assert.Equal(409, (await ProposeAsync(f, altered, meta)).Status);
        await using var db = f.CreateContext();
        Assert.Equal(1, await db.Set<GrantProposal>().CountAsync(x => x.State == "Pending"));
    }

    [Fact]
    public async Task Expanding_to_another_project_requires_new_authority_and_proposal()
    {
        await using var f = await ReadyAsync(postgres);
        var first = (await ProposeAsync(f)).Value!;
        Assert.Equal(200, (await ReviewAsync(f, first.Id)).Status);
        var sibling = Guid.Parse("71000000-0000-0000-0000-000000000012");
        var spec = Spec(f) with { Scope = new(WorkspaceId, sibling) };
        Assert.Equal(403, (await ProposeAsync(f, spec)).Status);
        await AuthorityAsync(f, AuthorId, AuthorPersonId, projectId: sibling);
        await AuthorityAsync(f, ReviewerId, ReviewerPersonId, projectId: sibling);
        var second = (await ProposeAsync(f, spec)).Value!;
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(200, (await ReviewAsync(f, second.Id)).Status);
        await using var db = f.CreateContext();
        Assert.Equal(ProjectId, (await db.Set<GrantProposal>().SingleAsync(x => x.Id == first.Id)).ProjectId);
        Assert.Equal(sibling, (await db.Set<GrantProposal>().SingleAsync(x => x.Id == second.Id)).ProjectId);
    }

    [Fact]
    public async Task Existing_organization_lifecycle_revokes_workflow_grant_once_and_reopening_does_not_restore()
    {
        await using var f = await ReadyAsync(postgres);
        var proposal = (await ProposeAsync(f)).Value!;
        Assert.Equal(200, (await ReviewAsync(f, proposal.Id)).Status);
        var id = await AssignmentIdAsync(f, proposal.Id);
        await using (var db = f.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
            var lifecycle = new AssignmentLifecycle(db, new AuditEventWriter(db, new CorrelationContext(), f.Clock), f.Clock, new PermissionContext());
            (await db.Set<Project>().SingleAsync(x => x.Id == ProjectId)).IsActive = false;
            var affected = await lifecycle.RevokeForScopeAsync(new ScopeKey(WorkspaceId, ProjectId, null), AuthorId, "ปิดโครงการจำลอง", default);
            Assert.Contains(RecipientId, affected);
            await lifecycle.RevokeForScopeAsync(new ScopeKey(WorkspaceId, ProjectId, null), AuthorId, "ปิดโครงการจำลอง", default);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        await using (var db = f.CreateContext())
        {
            (await db.Set<Project>().SingleAsync(x => x.Id == ProjectId)).IsActive = true;
            await db.SaveChangesAsync();
            Assert.NotNull((await db.Set<GrantAssignment>().SingleAsync(x => x.Id == id)).RevokedAtUtc);
            Assert.Equal(1, await db.AuditEvents.CountAsync(x => x.TargetId == id && x.EventType == "field-advance.grant.revoked"));
        }
        Assert.False(await AllowedAsync(f));
    }
    [Theory]
    [InlineData("propose", 201)]
    [InlineData("review", 200)]
    [InlineData("revoke", 200)]
    [InlineData("invalid", 400)]
    [InlineData("no-session", 403)]
    [InlineData("stale-version", 409)]
    public async Task Endpoint_adapter_uses_server_session_and_preserves_service_status(string operation, int expected)
    {
        await using var f = await ReadyAsync(postgres);
        var id = Guid.Empty;
        if (operation is "review" or "revoke" or "stale-version")
        {
            id = (await ProposeAsync(f)).Value!.Id;
            if (operation == "revoke")
            {
                Assert.Equal(200, (await ReviewAsync(f, id)).Status);
                id = await AssignmentIdAsync(f, id);
            }
        }
        var actor = operation is "review" or "stale-version" ? ReviewerId : AuthorId;
        var current = await f.SessionAsync(actor);
        if (operation == "no-session") current.Entity = null;
        await using var db = f.CreateContext();
        var grants = Service(f, db, current);
        var meta = operation == "invalid" ? Meta() with { Reason = "" } : Meta(operation == "stale-version" ? 99 : 1);
        var result = operation switch
        {
            "review" or "stale-version" => await GrantEndpoints.ReviewAsync(grants, current, id, new("approve", meta), default),
            "revoke" => await GrantEndpoints.RevokeAsync(Lifecycle(f, db, current), current, id, meta, default),
            _ => await GrantEndpoints.ProposeAsync(grants, current, new(Spec(f), meta), default)
        };
        Assert.Equal(expected, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        if (expected < 300) Assert.IsType<GrantView>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        else Assert.IsType<Microsoft.AspNetCore.Mvc.ProblemDetails>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
    }
    [Fact]
    public async Task Review_approved_fixture_proposal_persists_an_assignment()
    {
        await using var f = await ReadyAsync(postgres);
        var spec = Spec(f);
        var id = Guid.NewGuid();
        await using (var db = f.CreateContext())
        {
            db.Add(new GrantProposal
            {
                Id = id,
                WorkspaceId = WorkspaceId,
                ProjectId = ProjectId,
                RecipientId = RecipientId,
                RecipientPersonId = RecipientPersonId,
                ProposedBy = AuthorId,
                ProposedByPersonId = AuthorPersonId,
                State = "Pending",
                Capabilities = spec.Capabilities,
                DelegableCapabilities = [],
                StartsAtUtc = spec.StartsAt,
                EndsAtUtc = spec.EndsAt,
                CreatedAtUtc = f.Clock.GetUtcNow(),
                Reason = "ข้อเสนอจำลองก่อนตรวจ"
            });
            await db.SaveChangesAsync();
        }
        Assert.Equal(200, (await ReviewAsync(f, id)).Status);
        Assert.True(await AllowedAsync(f));
    }

    [Fact]
    public async Task Revoke_existing_grant_preserves_history_and_removes_usage()
    {
        await using var f = await ReadyAsync(postgres);
        var id = await f.ApprovedGrantAsync("report.read", ProjectId);
        Assert.True(await AllowedAsync(f));
        Assert.Equal(200, (await RevokeAsync(f, id)).Status);
        Assert.False(await AllowedAsync(f));
        await using var db = f.CreateContext();
        var row = await db.Set<GrantAssignment>().SingleAsync(x => x.Id == id);
        Assert.NotNull(row.RevokedAtUtc);
        Assert.Equal(2, row.Version);
    }
    // Regression intent: approving a proposal must create exactly one usable grant,
    // while merely proposing it must never grant usage authority.
    [Fact]
    public async Task Three_people_propose_then_review_creates_one_usable_grant()
    {
        await using var f = await ReadyAsync(postgres);
        var proposed = await ProposeAsync(f);
        Assert.Equal(201, proposed.Status);
        Assert.Equal("Pending", proposed.Value!.State);
        Assert.False(await AllowedAsync(f));
        var approved = await ReviewAsync(f, proposed.Value.Id);
        Assert.Equal(200, approved.Status);
        Assert.Equal("Approved", approved.Value!.State);
        Assert.Equal(2, approved.Value.Version);
        Assert.True(await AllowedAsync(f));
        await using var db = f.CreateContext();
        var assignment = Assert.Single(await db.Set<GrantAssignment>().Where(x => x.ProposalId == proposed.Value.Id).ToArrayAsync());
        Assert.Equal(ReviewerId, assignment.CreatedBy);
        Assert.Equal(1, assignment.Version);
        Assert.Equal(2, await db.AuditEvents.CountAsync(x => x.EventType.StartsWith("field-advance.grant.")));
    }

    [Theory]
    [InlineData("proposer-recipient", false)]
    [InlineData("reviewer-recipient", false)]
    [InlineData("reviewer-proposer", false)]
    [InlineData("proposer-recipient", true)]
    [InlineData("reviewer-recipient", true)]
    [InlineData("reviewer-proposer", true)]
    public async Task Any_same_person_pair_including_alias_accounts_is_forbidden(string pair, bool alias)
    {
        await using var f = await ReadyAsync(postgres);
        var recipient = pair == "proposer-recipient" ? AuthorId : pair == "reviewer-recipient" ? ReviewerId : RecipientId;
        var reviewer = pair == "reviewer-proposer" ? AuthorId : ReviewerId;
        if (alias)
        {
            var person = pair == "reviewer-proposer" ? AuthorPersonId : pair == "proposer-recipient" ? AuthorPersonId : ReviewerPersonId;
            await using var db = f.CreateContext();
            db.Add(new FinanceAccountLink
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                UserId = AliasAccountId,
                PersonId = person,
                CreatedBy = AuthorId,
                CreatedAtUtc = f.Clock.GetUtcNow(),
                Reason = "บัญชีจำลองของบุคคลเดิม"
            });
            await db.SaveChangesAsync();
            if (pair == "reviewer-proposer")
            {
                reviewer = AliasAccountId;
                await AuthorityAsync(f, AliasAccountId, AuthorPersonId);
            }
            else recipient = AliasAccountId;
        }
        var proposed = await ProposeAsync(f, Spec(f) with { RecipientId = recipient });
        var result = pair == "proposer-recipient" ? proposed : await ReviewAsync(f, proposed.Value!.Id, reviewer: reviewer);
        Assert.Equal(403, result.Status);
        await using var check = f.CreateContext();
        Assert.False(await check.Set<GrantAssignment>().AnyAsync(x => x.ProposalId == (proposed.Value == null ? Guid.Empty : proposed.Value.Id)));
    }

    [Theory]
    [InlineData("propose", "capability")]
    [InlineData("propose", "usage-only")]
    [InlineData("propose", "company-scope")]
    [InlineData("propose", "amount")]
    [InlineData("propose", "null-limit")]
    [InlineData("propose", "sum-limits")]
    [InlineData("propose", "expiry")]
    [InlineData("propose", "unbounded-expiry")]
    [InlineData("review", "capability")]
    [InlineData("review", "amount")]
    [InlineData("review", "null-limit")]
    [InlineData("review", "expiry")]
    [InlineData("review", "unbounded-expiry")]
    [InlineData("proposer-at-review", "capability")]
    [InlineData("proposer-at-review", "amount")]
    [InlineData("proposer-at-review", "expiry")]
    [InlineData("proposer-at-review", "revoked")]
    public async Task Delegation_requires_current_capability_scope_whole_amount_and_period_coverage(string step, string defect)
    {
        await using var f = await ReadyAsync(postgres);
        var spec = Spec(f) with { Capabilities = ["request.approve"], ApprovalLimit = 100m };
        if (defect == "unbounded-expiry") spec = spec with { EndsAt = null };
        FinanceResult<GrantView>? proposed = null;
        if (step != "propose") proposed = await ProposeAsync(f, spec);
        var actor = step == "review" ? ReviewerId : AuthorId;
        await using (var db = f.CreateContext())
        {
            var source = await db.Set<GrantProposal>().SingleAsync(x => x.RecipientId == actor);
            switch (defect)
            {
                case "capability": source.DelegableCapabilities = ["report.read"]; break;
                case "usage-only": source.DelegableCapabilities = []; source.Capabilities = [.. source.Capabilities, "request.approve"]; break;
                case "company-scope":
                    var assignment = await db.Set<GrantAssignment>().SingleAsync(x => x.ProposalId == source.Id);
                    // Use a separate valid company authority, then revoke the project one.
                    assignment.RevokedAtUtc = f.Clock.GetUtcNow(); assignment.RevokedBy = actor; assignment.RevocationReason = "ถอนต้นทางจำลอง";
                    break;
                case "amount": case "sum-limits": source.ApprovalLimit = 60m; break;
                case "null-limit": source.ApprovalLimit = null; break;
                case "expiry": source.EndsAtUtc = f.Clock.GetUtcNow().AddHours(1); break;
                case "unbounded-expiry": source.EndsAtUtc = f.Clock.GetUtcNow().AddDays(10); break;
                case "revoked":
                    var row = await db.Set<GrantAssignment>().SingleAsync(x => x.ProposalId == source.Id);
                    row.RevokedAtUtc = f.Clock.GetUtcNow(); row.RevokedBy = actor; row.RevocationReason = "ถอนต้นทางจำลอง"; break;
            }
            await db.SaveChangesAsync();
        }
        if (defect == "company-scope") await AuthorityAsync(f, actor, AuthorPersonId, company: true);
        if (defect == "sum-limits") await AuthorityAsync(f, actor, AuthorPersonId, limit: 60m);
        var result = step == "propose" ? await ProposeAsync(f, spec) : await ReviewAsync(f, proposed!.Value!.Id);
        Assert.Equal(403, result.Status);
        await using var check = f.CreateContext();
        Assert.False(await check.Set<GrantAssignment>().AnyAsync(x => x.ProposalId == (proposed == null ? Guid.Empty : proposed.Value!.Id)));
    }

    [Fact]
    public async Task Null_target_limit_does_not_acquire_monetary_authority_and_delegable_is_not_usage()
    {
        await using var f = await ReadyAsync(postgres);
        var proposed = await ProposeAsync(f, Spec(f) with { DelegableCapabilities = ["request.approve"], ApprovalLimit = null });
        Assert.Equal(201, proposed.Status);
        var reviewed = await ReviewAsync(f, proposed.Value!.Id);
        Assert.Equal(200, reviewed.Status);
        Assert.Null(reviewed.Value!.Spec.ApprovalLimit);
        Assert.False(await AllowedAsync(f, "request.approve"));
    }

    [Fact]
    public async Task Future_start_and_exact_expiry_control_usage()
    {
        await using var f = await ReadyAsync(postgres);
        var now = f.Clock.GetUtcNow();
        var proposal = await ProposeAsync(f, Spec(f) with { StartsAt = now.AddMinutes(1), EndsAt = now.AddMinutes(2) });
        Assert.Equal(200, (await ReviewAsync(f, proposal.Value!.Id)).Status);
        Assert.False(await AllowedAsync(f));
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await AllowedAsync(f));
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(await AllowedAsync(f));
    }

    [Theory]
    [InlineData("return", "Draft")]
    [InlineData("reject", "Rejected")]
    public async Task Return_and_reject_require_reason_and_never_create_grants(string decision, string state)
    {
        await using var f = await ReadyAsync(postgres);
        var proposal = await ProposeAsync(f);
        var invalid = await ReviewAsync(f, proposal.Value!.Id, decision, meta: Meta() with { Reason = " " });
        Assert.Equal(400, invalid.Status);
        var reviewed = await ReviewAsync(f, proposal.Value.Id, decision);
        Assert.Equal(200, reviewed.Status);
        Assert.Equal(state, reviewed.Value!.State);
        Assert.Equal(409, (await ReviewAsync(f, proposal.Value.Id, meta: Meta(2))).Status);
        await using var db = f.CreateContext();
        Assert.False(await db.Set<GrantAssignment>().AnyAsync(x => x.ProposalId == proposal.Value.Id));
        Assert.Single(await db.Set<GrantReview>().Where(x => x.ProposalId == proposal.Value.Id).ToArrayAsync());
    }

    [Fact]
    public async Task No_reviewer_leaves_pending_without_automatic_approval()
    {
        await using var f = await ReadyAsync(postgres, reviewer: false);
        var proposal = await ProposeAsync(f);
        Assert.Equal(201, proposal.Status);
        Assert.Equal(403, (await ReviewAsync(f, proposal.Value!.Id)).Status);
        await using var db = f.CreateContext();
        Assert.Equal("Pending", (await db.Set<GrantProposal>().SingleAsync(x => x.Id == proposal.Value.Id)).State);
        Assert.False(await db.Set<GrantAssignment>().AnyAsync(x => x.ProposalId == proposal.Value.Id));
    }

    [Theory]
    [InlineData("propose")]
    [InlineData("review")]
    [InlineData("revoke")]
    public async Task Command_replay_is_exact_and_changed_payload_or_actor_conflicts(string operation)
    {
        await using var f = await ReadyAsync(postgres);
        var command = Meta();
        var proposal = await ProposeAsync(f, meta: command);
        var original = proposal;
        var id = proposal.Value!.Id;
        if (operation != "propose")
        {
            command = Meta();
            var reviewed = await ReviewAsync(f, id, meta: command);
            Assert.Equal(200, reviewed.Status);
            original = reviewed;
            if (operation == "revoke")
            {
                id = await AssignmentIdAsync(f, id);
                command = Meta();
                original = await RevokeAsync(f, id, command);
                Assert.Equal(200, original.Status);
            }
        }
        async Task<FinanceResult<GrantView>> Run(CommandMeta meta, Guid? actor = null) => operation switch
        {
            "propose" => await ProposeAsync(f, meta: meta, actor: actor ?? AuthorId),
            "review" => await ReviewAsync(f, id, meta: meta, reviewer: actor ?? ReviewerId),
            _ => await RevokeAsync(f, id, meta, actor ?? AuthorId)
        };
        var replay = await Run(command);
        Assert.Equal(operation == "propose" ? 201 : 200, replay.Status);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(original), System.Text.Json.JsonSerializer.Serialize(replay));
        Assert.Equal(409, (await Run(command with { Reason = "เปลี่ยนเนื้อหาคำสั่งจำลอง" })).Status);
        Assert.Equal(409, (await Run(command, operation == "review" ? AuthorId : ReviewerId)).Status);
        await using var db = f.CreateContext();
        Assert.Single(await db.Set<FinanceCommandReceipt>().Where(x => x.CommandId == command.CommandId).ToArrayAsync());
    }

    [Theory]
    [InlineData("capability")]
    [InlineData("amount")]
    [InlineData("usage")]
    public async Task Revoke_requires_delegation_coverage_in_addition_to_revoke_capability(string defect)
    {
        await using var f = await ReadyAsync(postgres);
        var proposal = await ProposeAsync(f, Spec(f) with { Capabilities = ["request.approve"], ApprovalLimit = 100m });
        Assert.Equal(200, (await ReviewAsync(f, proposal.Value!.Id)).Status);
        var id = await AssignmentIdAsync(f, proposal.Value.Id);
        await using (var db = f.CreateContext())
        {
            var source = await db.Set<GrantProposal>().SingleAsync(x => x.RecipientId == AuthorId);
            if (defect == "capability") source.DelegableCapabilities = ["report.read"];
            if (defect == "amount") source.ApprovalLimit = null;
            if (defect == "usage") source.Capabilities = ["grant.propose"];
            await db.SaveChangesAsync();
        }
        Assert.Equal(403, (await RevokeAsync(f, id)).Status);
        Assert.True(await AllowedAsync(f, "request.approve"));
    }

    [Fact]
    public async Task Revocation_is_immediate_and_restoration_or_expansion_is_a_new_proposal()
    {
        await using var f = await ReadyAsync(postgres);
        var first = await ProposeAsync(f);
        Assert.Equal(200, (await ReviewAsync(f, first.Value!.Id)).Status);
        var id = await AssignmentIdAsync(f, first.Value.Id);
        Assert.Equal(409, (await RevokeAsync(f, id, Meta(99))).Status);
        var revoked = await RevokeAsync(f, id);
        Assert.Equal(200, revoked.Status);
        Assert.Equal("Revoked", revoked.Value!.State);
        Assert.Equal(id, revoked.Value.Id);
        Assert.Equal(2, revoked.Value.Version);
        Assert.False(await AllowedAsync(f));
        var second = await ProposeAsync(f, Spec(f) with { Capabilities = ["report.read", "request.approve"], ApprovalLimit = 200m, EndsAt = f.Clock.GetUtcNow().AddDays(2) });
        Assert.NotEqual(first.Value.Id, second.Value!.Id);
        Assert.False(await AllowedAsync(f));
        Assert.Equal(200, (await ReviewAsync(f, second.Value.Id)).Status);
        Assert.True(await AllowedAsync(f));
        await using var db = f.CreateContext();
        Assert.NotNull((await db.Set<GrantAssignment>().SingleAsync(x => x.Id == id)).RevokedAtUtc);
        Assert.Equal("Approved", (await db.Set<GrantProposal>().SingleAsync(x => x.Id == first.Value.Id)).State);
        Assert.Equal(409, (await ReviewAsync(f, first.Value.Id, meta: Meta(2))).Status);
    }

    [Theory]
    [InlineData("capabilities-null")]
    [InlineData("unknown-capability")]
    [InlineData("empty-capability")]
    [InlineData("negative-limit")]
    [InlineData("sub-cent-limit")]
    [InlineData("invalid-period")]
    [InlineData("empty-command")]
    [InlineData("bad-version")]
    public async Task Malformed_requests_cannot_persist_proposals(string defect)
    {
        await using var f = await ReadyAsync(postgres);
        var spec = Spec(f);
        var meta = Meta();
        switch (defect)
        {
            case "capabilities-null": spec = spec with { Capabilities = null! }; break;
            case "unknown-capability": spec = spec with { Capabilities = ["*"] }; break;
            case "empty-capability": spec = spec with { Capabilities = [] }; break;
            case "negative-limit": spec = spec with { ApprovalLimit = -1m }; break;
            case "sub-cent-limit": spec = spec with { ApprovalLimit = 1.001m }; break;
            case "invalid-period": spec = spec with { EndsAt = spec.StartsAt }; break;
            case "empty-command": meta = meta with { CommandId = Guid.Empty }; break;
            case "bad-version": meta = Meta(0); break;
        }
        Assert.Equal(400, (await ProposeAsync(f, spec, meta)).Status);
        await using var db = f.CreateContext();
        Assert.False(await db.Set<GrantProposal>().AnyAsync(x => x.RecipientId == RecipientId));
    }

    internal static CommandMeta Meta(long version = 1) => new(Guid.NewGuid(), version, "เหตุผลจำลองสำหรับทดสอบ");
    internal static GrantService Service(FieldAdvanceFixture f, Tpr10DbContext db, RequestSession current, IAuditEventWriter? audit = null) =>
        new(db, FieldAdvanceAccessTests.Access(db, current, f.Clock), new FinancePersonResolver(db, f.Clock),
            audit ?? new AuditEventWriter(db, new CorrelationContext(), f.Clock), f.Clock);
    internal static GrantLifecycle Lifecycle(FieldAdvanceFixture f, Tpr10DbContext db, RequestSession current, IAuditEventWriter? audit = null) =>
        new(db, FieldAdvanceAccessTests.Access(db, current, f.Clock), new FinancePersonResolver(db, f.Clock),
            audit ?? new AuditEventWriter(db, new CorrelationContext(), f.Clock), f.Clock);
    internal static GrantSpec Spec(FieldAdvanceFixture f) => new(RecipientId, new(WorkspaceId, ProjectId),
        ["report.read"], [], null, f.Clock.GetUtcNow(), f.Clock.GetUtcNow().AddDays(1));

    internal static async Task<FieldAdvanceFixture> ReadyAsync(PostgresFixture postgres, bool reviewer = true)
    {
        var f = await CreateAsync(postgres);
        await f.SeedPeopleAsync();
        await AuthorityAsync(f, AuthorId, AuthorPersonId);
        if (reviewer) await AuthorityAsync(f, ReviewerId, ReviewerPersonId);
        return f;
    }

    internal static async Task<Guid> AuthorityAsync(FieldAdvanceFixture f, Guid actor, Guid person,
        decimal? limit = 1000m, bool company = false, Guid? projectId = null)
    {
        await using var db = f.CreateContext();
        var others = new[] { (AuthorId, AuthorPersonId), (ReviewerId, ReviewerPersonId), (RecipientId, RecipientPersonId) }
            .Where(x => x.Item2 != person).ToArray();
        var at = f.Clock.GetUtcNow().AddMinutes(-1);
        var source = new GrantProposal
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = company ? null : projectId ?? ProjectId,
            RecipientId = actor,
            RecipientPersonId = person,
            ProposedBy = others[0].Item1,
            ProposedByPersonId = others[0].Item2,
            State = "Approved",
            Capabilities = ["grant.propose", "grant.review", "grant.revoke"],
            DelegableCapabilities = ["report.read", "request.approve", "payment.record"],
            ApprovalLimit = limit,
            StartsAtUtc = at,
            EndsAtUtc = null,
            CreatedAtUtc = at,
            Reason = "ต้นทางอำนาจจำลอง"
        };
        var review = new GrantReview
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProposalId = source.Id,
            ReviewerId = others[1].Item1,
            ReviewerPersonId = others[1].Item2,
            Outcome = "Approved",
            CreatedAtUtc = at,
            Reason = "ตรวจต้นทางจำลอง"
        };
        var assignment = new GrantAssignment
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = source.ProjectId,
            ProposalId = source.Id,
            ReviewId = review.Id,
            CreatedBy = review.ReviewerId,
            CreatedAtUtc = at,
            Reason = "สิทธิ์ต้นทางจำลอง"
        };
        db.AddRange(source, review, assignment);
        await db.SaveChangesAsync();
        return assignment.Id;
    }

    internal static Task<FinanceResult<GrantView>> ProposeAsync(FieldAdvanceFixture f, GrantSpec? spec = null, CommandMeta? meta = null, Guid? actor = null) =>
        InvokeAsync(f, actor ?? AuthorId, (db, current) => Service(f, db, current)
            .ProposeAsync(actor ?? AuthorId, spec ?? Spec(f), meta ?? Meta(), default));
    internal static Task<FinanceResult<GrantView>> ReviewAsync(FieldAdvanceFixture f, Guid id, string decision = "approve", CommandMeta? meta = null, Guid? reviewer = null) =>
        InvokeAsync(f, reviewer ?? ReviewerId, (db, current) => Service(f, db, current)
            .ReviewAsync(reviewer ?? ReviewerId, id, decision, meta ?? Meta(), default));
    internal static Task<FinanceResult<GrantView>> RevokeAsync(FieldAdvanceFixture f, Guid id, CommandMeta? meta = null, Guid? actor = null) =>
        InvokeAsync(f, actor ?? AuthorId, (db, current) => Lifecycle(f, db, current)
            .RevokeAsync(actor ?? AuthorId, id, meta ?? Meta(), default));

    private static async Task<FinanceResult<GrantView>> InvokeAsync(FieldAdvanceFixture f, Guid actor,
        Func<Tpr10DbContext, RequestSession, Task<FinanceResult<GrantView>>> run)
    {
        var current = await f.SessionAsync(actor);
        await using var db = f.CreateContext();
        return await run(db, current);
    }

    internal static async Task<Guid> AssignmentIdAsync(FieldAdvanceFixture f, Guid proposal)
    {
        await using var db = f.CreateContext();
        return await db.Set<GrantAssignment>().Where(x => x.ProposalId == proposal).Select(x => x.Id).SingleAsync();
    }

    internal static async Task<bool> AllowedAsync(FieldAdvanceFixture f, string capability = "report.read")
    {
        var current = await f.SessionAsync(RecipientId);
        await using var db = f.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        return (await FieldAdvanceAccessTests.Access(db, current, f.Clock).CheckAsync(RecipientId, new(WorkspaceId, ProjectId), capability, default)).Allowed;
    }
}
