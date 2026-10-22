using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Requests;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvanceGrantTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceRequestAccessTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Legacy_empty_level_receipt_replays_without_rewriting_or_adding_authority()
    {
        await using var f = await ReadyAsync(postgres);
        var spec = Spec(f);
        var meta = Meta();
        var legacySpec = new
        {
            spec.RecipientId,
            spec.Scope,
            spec.Capabilities,
            spec.DelegableCapabilities,
            spec.ApprovalLimit,
            spec.StartsAt,
            spec.EndsAt
        };
        var id = Guid.NewGuid();
        var legacyResult = JsonSerializer.Serialize(new
        {
            Status = 201,
            Code = "grant-proposed",
            Value = new { Id = id, State = "Pending", Version = 1L, Spec = legacySpec }
        });
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(new { spec = legacySpec, meta })));
        await using var db = f.CreateContext();
        db.Add(new FinanceCommandReceipt
        {
            CommandId = meta.CommandId,
            WorkspaceId = WorkspaceId,
            ActorId = AuthorId,
            Action = "grant.propose",
            PayloadSha256 = hash,
            Status = 201,
            ResultJson = legacyResult,
            CreatedAtUtc = f.Clock.GetUtcNow()
        });
        await db.SaveChangesAsync();
        var originalStored = await db.Set<FinanceCommandReceipt>().AsNoTracking().SingleAsync();
        var replay = await ProposeAsync(f, spec, meta);
        Assert.Equal(201, replay.Status);
        Assert.Equal(id, replay.Value!.Id);
        Assert.Empty(replay.Value.Spec.ApprovalLevels);
        Assert.Empty(replay.Value.Spec.DelegableApprovalLevels);
        Assert.Empty(replay.Value.Spec.PrimaryApprovalLevels);
        Assert.Empty(replay.Value.Spec.DelegablePrimaryApprovalLevels);
        var retained = await db.Set<FinanceCommandReceipt>().AsNoTracking().SingleAsync();
        Assert.Equal(hash, retained.PayloadSha256);
        Assert.Equal(originalStored.ResultJson, retained.ResultJson);
        var tampered = spec with { Capabilities = ["request.approve"], ApprovalLimit = 100m, ApprovalLevels = ["supervisor"] };
        Assert.Equal(409, (await ProposeAsync(f, tampered, meta)).Status);
    }
    [Fact]
    public async Task Report_reader_can_read_terminal_copy_source_but_cannot_edit_or_confirm()
    {
        await using var f = await ReadyAsync(postgres);
        await f.ApprovedGrantAsync("report.read", ProjectId);
        await using var db = f.CreateContext();
        var request = new AdvanceRequest
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            OwnerPersonId = AuthorPersonId,
            CreatedBy = AuthorId,
            State = "Rejected"
        };
        db.Add(request);
        await db.SaveChangesAsync();
        Assert.True(await CheckRequestAsync(f, request.Id, RecipientId, "read"));
        Assert.True(await CheckRequestAsync(f, request.Id, RecipientId, "copy"));
        Assert.False(await CheckRequestAsync(f, request.Id, RecipientId, "edit"));
        Assert.False(await CheckRequestAsync(f, request.Id, RecipientId, "confirm"));
        Assert.False(await AllowedAsync(f, "request.create"));
        Assert.False(await AllowedAsync(f, "request.delegate"));
    }

    [Fact]
    public async Task Delegate_can_prepare_only_own_request_and_never_confirm_or_withdraw()
    {
        await using var f = await ReadyAsync(postgres);
        await f.ApprovedGrantAsync("request.delegate", ProjectId);
        await using var db = f.CreateContext();
        var request = new AdvanceRequest
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            OwnerPersonId = AuthorPersonId,
            DelegatePersonId = RecipientPersonId,
            CreatedBy = RecipientId
        };
        var other = new AdvanceRequest
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            OwnerPersonId = AuthorPersonId,
            CreatedBy = AuthorId
        };
        db.AddRange(request, other);
        await db.SaveChangesAsync();
        foreach (var action in new[] { "read", "edit", "send-to-owner" })
        {
            Assert.True(await CheckRequestAsync(f, request.Id, RecipientId, action));
            Assert.False(await CheckRequestAsync(f, other.Id, RecipientId, action));
        }
        request.State = "AwaitingOwner";
        await db.SaveChangesAsync();
        foreach (var action in new[] { "edit", "confirm", "withdraw", "return-to-author" })
            Assert.False(await CheckRequestAsync(f, request.Id, RecipientId, action));
    }
    [Theory]
    [InlineData("valid", true)]
    [InlineData("legacy", false)]
    [InlineData("amount", false)]
    [InlineData("expired", false)]
    [InlineData("unassigned", false)]
    [InlineData("held", false)]
    [InlineData("checker", false)]
    [InlineData("payer", false)]
    [InlineData("delegate-history", false)]
    [InlineData("previous-approver", true)]
    [InlineData("current-approver", false)]
    [InlineData("revoked-regranted", true)]
    [InlineData("revoked-regranted-checker", false)]
    public async Task Assigned_reviewer_needs_current_single_source_and_retained_separation(string scenario, bool allowed)
    {
        await using var f = await CreateAsync(postgres);
        await f.SeedPoliciesAsync();
        var assignmentId = await AuthorityAsync(f, ReviewerId, ReviewerPersonId);
        await using var db = f.CreateContext();
        var source = await db.Set<GrantProposal>().SingleAsync(x => x.RecipientId == ReviewerId);
        source.Capabilities = ["request.approve"];
        source.ApprovalLevels = scenario == "legacy" ? [] : ["supervisor"];
        source.ApprovalLimit = scenario == "amount" ? 99.99m : 100m;
        if (scenario == "expired") source.EndsAtUtc = f.Clock.GetUtcNow();
        var request = new AdvanceRequest
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            OwnerPersonId = AuthorPersonId,
            DelegatePersonId = RecipientPersonId,
            CreatedBy = RecipientId,
            State = scenario == "held" ? "Held" : "PendingApproval"
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
            CreatedBy = RecipientId,
            CreatedByPersonId = RecipientPersonId,
            CompanyPolicyRevisionId = CompanyRevisionId,
            PolicySnapshotJson = "{}",
            PolicyHash = new('b', 64)
        };
        var step = new AdvanceApprovalStep
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            RequestId = request.Id,
            RevisionId = revision.Id,
            StepOrder = 2,
            LevelCode = "supervisor",
            AssigneePersonId = scenario == "unassigned" ? RecipientPersonId : ReviewerPersonId
        };
        db.AddRange(request, revision, step);
        if (scenario == "current-approver") db.Add(new AdvanceApprovalStep
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            RequestId = request.Id,
            RevisionId = revision.Id,
            StepOrder = 1,
            LevelCode = "project-manager",
            Outcome = "Approved",
            DecidedBy = ReviewerId,
            DecidedByPersonId = ReviewerPersonId,
            DecidedAtUtc = f.Clock.GetUtcNow(),
            DecisionReason = "อนุมัติจำลอง"
        });
        var role = scenario switch
        {
            "checker" or "revoked-regranted-checker" => "Checker",
            "payer" => "Payer",
            "delegate-history" => "Delegate",
            "previous-approver" => "Approver",
            _ => null
        };
        if (role is not null) db.Add(new AdvanceRequestParticipant
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            RequestId = request.Id,
            PersonId = ReviewerPersonId,
            Role = role,
            CreatedBy = ReviewerId
        });
        await db.SaveChangesAsync();
        revision.SubmittedAtUtc = f.Clock.GetUtcNow(); revision.ConfirmedByPersonId = AuthorPersonId;
        await db.SaveChangesAsync();
        if (scenario.StartsWith("revoked-regranted", StringComparison.Ordinal))
        {
            var assignment = await db.Set<GrantAssignment>().SingleAsync(x => x.Id == assignmentId);
            assignment.RevokedAtUtc = f.Clock.GetUtcNow(); assignment.RevokedBy = AuthorId;
            assignment.RevocationReason = "ถอนจำลอง"; assignment.Version++;
            await db.SaveChangesAsync();
            Assert.False(await CheckRequestAsync(f, request.Id, ReviewerId, "review"));
            var newId = await AuthorityAsync(f, ReviewerId, ReviewerPersonId);
            var newSource = await db.Set<GrantProposal>().SingleAsync(x => db.Set<GrantAssignment>().Any(a => a.Id == newId && a.ProposalId == x.Id));
            newSource.Capabilities = ["request.approve"]; newSource.ApprovalLevels = ["supervisor"]; newSource.ApprovalLimit = 100m;
            await db.SaveChangesAsync();
        }
        Assert.Equal(allowed, await CheckRequestAsync(f, request.Id, ReviewerId, "review"));
        Assert.Equal(allowed, await CheckRequestAsync(f, request.Id, ReviewerId, "read"));
        Assert.False(await CheckRequestAsync(f, request.Id, ReviewerId, "withdraw"));
        Assert.False(await CheckRequestAsync(f, request.Id, RecipientId, "read"));
        Assert.False(await CheckRequestAsync(f, request.Id, AuthorId, "read"));
    }

    private static async Task<bool> CheckRequestAsync(FieldAdvanceFixture f, Guid request, Guid actor, string action)
    {
        var session = await f.SessionAsync(actor);
        await using var db = f.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        return (await new RequestAccess(db, FieldAdvanceAccessTests.Access(db, session, f.Clock), new FinancePersonResolver(db, f.Clock), f.Clock)
            .CheckAsync(actor, request, action, default)).Allowed;
    }
    [Fact]
    public async Task Migration_keeps_legacy_grants_empty_and_does_not_discard_new_authority()
    {
        await using var f = await ReadyAsync(postgres);
        await using var db = f.CreateContext();
        var count = await db.Set<GrantAssignment>().CountAsync();
        await db.GetService<IMigrator>().MigrateAsync("20261022124725_AddFieldAdvanceRequests");
        await db.Database.MigrateAsync();
        Assert.Equal(count, await db.Set<GrantAssignment>().CountAsync());
        var old = await db.Set<GrantProposal>().AsNoTracking().ToArrayAsync();
        Assert.All(old, p =>
        {
            Assert.Empty(p.ApprovalLevels); Assert.Empty(p.DelegableApprovalLevels);
            Assert.Empty(p.PrimaryApprovalLevels); Assert.Empty(p.DelegablePrimaryApprovalLevels);
        });
        await LevelAuthorityAsync(f);
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync("20261022124725_AddFieldAdvanceRequests"));
        Assert.Equal("23514", error.SqlState);
        Assert.Equal(count, await db.Set<GrantAssignment>().CountAsync());
    }

    [Theory]
    [InlineData("primary")]
    [InlineData("delegable-primary")]
    [InlineData("null-limit")]
    [InlineData("null-element")]
    [InlineData("duplicate")]
    [InlineData("blank")]
    [InlineData("oversized")]
    public async Task Database_rejects_unsupported_approval_metadata(string defect)
    {
        await using var f = await ReadyAsync(postgres);
        await using var db = f.CreateContext();
        var source = await db.Set<GrantProposal>().SingleAsync(x => x.RecipientId == AuthorId);
        switch (defect)
        {
            case "primary": source.PrimaryApprovalLevels = ["project-manager"]; break;
            case "delegable-primary": source.DelegablePrimaryApprovalLevels = ["project-manager"]; break;
            case "null-limit": source.DelegableApprovalLevels = ["project-manager"]; source.ApprovalLimit = null; break;
            case "duplicate": source.DelegableApprovalLevels = ["project-manager", "PROJECT-MANAGER"]; break;
            case "blank": source.DelegableApprovalLevels = [" "]; break;
            case "oversized": source.DelegableApprovalLevels = [new string('a', 65)]; break;
            default: source.DelegableApprovalLevels = [null!]; break;
        }
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23514", Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }
    [Theory]
    [InlineData("read", true)]
    [InlineData("edit", true)]
    [InlineData("send-to-owner", false)]
    [InlineData("confirm", true)]
    [InlineData("withdraw", true)]
    [InlineData("review", false)]
    [InlineData("return-to-author", false)]
    [InlineData("revise", false)]
    [InlineData("copy", false)]
    [InlineData("request.create", false)]
    public async Task Owner_access_is_current_and_action_specific(string action, bool allowed)
    {
        await using var f = await ReadyAsync(postgres);
        await using var db = f.CreateContext();
        var source = await db.Set<GrantProposal>().SingleAsync(x => x.RecipientId == AuthorId);
        source.Capabilities = ["request.create"];
        await db.SaveChangesAsync();
        var request = new AdvanceRequest
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            OwnerPersonId = AuthorPersonId,
            CreatedBy = AuthorId,
            State = "Draft"
        };
        db.Add(request);
        await db.SaveChangesAsync();
        var current = await f.SessionAsync(AuthorId);
        var access = new RequestAccess(db, FieldAdvanceAccessTests.Access(db, current, f.Clock), new FinancePersonResolver(db, f.Clock), f.Clock);
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        Assert.Equal(allowed, (await access.CheckAsync(AuthorId, request.Id, action, default)).Allowed);
        Assert.False((await access.CheckAsync(AuthorId, Guid.Empty, action, default)).Allowed);
        Assert.False((await access.CheckAsync(AuthorId, Guid.NewGuid(), action, default)).Allowed);
    }
    [Fact]
    public async Task One_person_alias_cannot_self_approve()
    {
        await using var f = await CreateAsync(postgres);
        await f.SeedPeopleAsync();
        await using var db = f.CreateContext();
        db.Add(new FinanceAccountLink
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            UserId = AliasAccountId,
            PersonId = RecipientPersonId,
            CreatedBy = AuthorId,
            CreatedAtUtc = f.Clock.GetUtcNow(),
            Reason = "บัญชีจำลองบุคคลเดียวกัน"
        });
        await db.SaveChangesAsync();
        var person = await new FinancePersonResolver(db, f.Clock).ResolveAsync(AliasAccountId, WorkspaceId, default);
        Assert.Equal(RecipientPersonId, person);
        Assert.False(RequestSeparation.Check(person!.Value, "request.approve",
            new([RecipientPersonId], [], [], [], []), []).Allowed);
        Assert.False(RequestSeparation.Check(person.Value, "request.approve",
            new([AuthorPersonId], [RecipientPersonId], [], [], []), []).Allowed);
    }

    [Fact]
    public void Previous_revision_approver_is_allowed_but_cannot_check_or_pay()
    {
        RequestParticipationHistory history = new([AuthorPersonId], [RecipientPersonId], [ReviewerPersonId], [], []);
        Assert.True(RequestSeparation.Check(ReviewerPersonId, "request.approve", history, []).Allowed);
        Assert.False(RequestSeparation.Check(ReviewerPersonId, "request.approve", history, [ReviewerPersonId]).Allowed);
        Assert.False(RequestSeparation.Check(ReviewerPersonId, "finance.check", history, []).Allowed);
        Assert.False(RequestSeparation.Check(ReviewerPersonId, "payment.record", history, []).Allowed);
    }

    [Theory]
    [InlineData("request.approve", "checker")]
    [InlineData("request.approve", "payer")]
    [InlineData("finance.check", "payer")]
    [InlineData("payment.record", "checker")]
    [InlineData("unknown", "none")]
    public void Cross_duty_history_never_disappears(string action, string previous)
    {
        Assert.False(RequestSeparation.Check(ReviewerPersonId, action,
            new([AuthorPersonId], [], [], previous == "checker" ? [ReviewerPersonId] : [], previous == "payer" ? [ReviewerPersonId] : []), []).Allowed);
    }
    [Theory]
    [InlineData("request.create")]
    [InlineData("request.delegate")]
    public async Task Only_explicit_project_grants_allow_create_or_delegate(string capability)
    {
        await using var f = await CreateAsync(postgres);
        await f.SeedPeopleAsync();
        Assert.False(await AllowedAsync(f, capability));
        await f.ApprovedGrantAsync(capability, null);
        Assert.False(await AllowedAsync(f, capability));
        var current = await f.SessionAsync(RecipientId);
        await using (var db = f.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
            Assert.False((await FieldAdvanceAccessTests.Access(db, current, f.Clock)
                .CheckAsync(RecipientId, new(WorkspaceId, null), capability, default)).Allowed);
        }
        await f.ApprovedGrantAsync(capability, ProjectId);
        Assert.True(await AllowedAsync(f, capability));
    }

    // Removing level coverage must allow this adversarial proposal, making the assertion fail.
    [Fact]
    public async Task One_source_must_cover_level_amount_and_period()
    {
        await using var f = await ReadyAsync(postgres);
        var target = JsonSerializer.Deserialize<GrantSpec>(JsonSerializer.Serialize(new
        {
            RecipientId,
            Scope = new FinanceScope(WorkspaceId, ProjectId),
            Capabilities = new[] { "request.approve" },
            DelegableCapabilities = Array.Empty<string>(),
            ApprovalLimit = 100m,
            StartsAt = f.Clock.GetUtcNow(),
            EndsAt = f.Clock.GetUtcNow().AddDays(1),
            ApprovalLevels = new[] { "project-manager" },
            DelegableApprovalLevels = Array.Empty<string>(),
            PrimaryApprovalLevels = Array.Empty<string>(),
            DelegablePrimaryApprovalLevels = Array.Empty<string>()
        }))!;
        Assert.Equal(403, (await ProposeAsync(f, target)).Status);
        await using var db = f.CreateContext();
        Assert.False(await db.Set<GrantProposal>().AnyAsync(x => x.State == "Pending"));
    }

    [Theory]
    [InlineData("amount")]
    [InlineData("period")]
    [InlineData("level")]
    [InlineData("primary")]
    [InlineData("expired")]
    [InlineData("revoked")]
    public async Task Separate_sources_cannot_be_combined_to_delegate_approval_authority(string defect)
    {
        await using var f = await ReadyAsync(postgres);
        var second = await AuthorityAsync(f, AuthorId, AuthorPersonId);
        await using (var db = f.CreateContext())
        {
            var extra = await db.Set<GrantAssignment>().SingleAsync(x => x.Id == second);
            var source = await db.Set<GrantProposal>().SingleAsync(x => x.RecipientId == AuthorId && x.Id != extra.ProposalId);
            source.DelegableApprovalLevels = ["project-manager"];
            source.DelegablePrimaryApprovalLevels = defect == "primary" ? [] : ["project-manager"];
            if (defect == "amount") source.ApprovalLimit = 99.99m;
            if (defect == "period") source.EndsAtUtc = f.Clock.GetUtcNow().AddHours(1);
            if (defect == "level") { source.DelegableApprovalLevels = ["supervisor"]; source.DelegablePrimaryApprovalLevels = ["supervisor"]; }
            if (defect == "expired") source.EndsAtUtc = f.Clock.GetUtcNow();
            if (defect == "revoked")
            {
                var assignment = await db.Set<GrantAssignment>().SingleAsync(x => x.ProposalId == source.Id);
                assignment.RevokedAtUtc = f.Clock.GetUtcNow(); assignment.RevokedBy = ReviewerId;
                assignment.RevocationReason = "ถอนจำลอง"; assignment.Version++;
            }
            await db.SaveChangesAsync();
        }
        var result = await ProposeAsync(f, LevelSpec(f));
        Assert.Equal(403, result.Status);
    }

    [Fact]
    public async Task Three_people_grant_preserves_levels_in_http_read_and_receipt_and_detects_tampering()
    {
        await using var f = await ReadyAsync(postgres);
        await LevelAuthorityAsync(f);
        var spec = LevelSpec(f);
        var meta = Meta();
        var current = await f.SessionAsync(AuthorId);
        await using var db = f.CreateContext();
        var wire = JsonSerializer.Deserialize<TPR10.Api.FieldAdvances.Grants.GrantHttpSpec>(JsonSerializer.Serialize(spec))!;
        var response = await TPR10.Api.FieldAdvances.Grants.GrantHttp.Propose(Service(f, db, current), current, new(wire, meta), default);
        Assert.Equal(201, Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(response).StatusCode);
        var view = Assert.IsType<TPR10.Api.FieldAdvances.Grants.GrantHttpView>(Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IValueHttpResult>(response).Value);
        Assert.Equal(["project-manager"], view.Spec.ApprovalLevels);
        Assert.Equal(["project-manager"], view.Spec.PrimaryApprovalLevels);
        Assert.Equal(["project-manager"], view.Spec.DelegableApprovalLevels);
        Assert.Equal(["project-manager"], view.Spec.DelegablePrimaryApprovalLevels);
        Assert.Equal(200, (await ReviewAsync(f, view.Id)).Status);
        var replay = await ProposeAsync(f, spec, meta);
        Assert.Equal(["project-manager"], replay.Value!.Spec.PrimaryApprovalLevels);
        foreach (var altered in new[] { spec with { ApprovalLevels = ["project-manager", "supervisor"] },
            spec with { DelegableApprovalLevels = ["project-manager", "supervisor"] },
            spec with { PrimaryApprovalLevels = [] }, spec with { DelegablePrimaryApprovalLevels = [] } })
        {
            var tampered = JsonSerializer.Deserialize<TPR10.Api.FieldAdvances.Grants.GrantHttpSpec>(JsonSerializer.Serialize(altered))!;
            var result = await TPR10.Api.FieldAdvances.Grants.GrantHttp.Propose(Service(f, db, current), current, new(tampered, meta), default);
            Assert.Equal(409, Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(result).StatusCode);
        }
        var stored = await db.Set<GrantProposal>().AsNoTracking().SingleAsync(x => x.Id == view.Id);
        Assert.Equal(["project-manager"], stored.PrimaryApprovalLevels);
        Assert.Single(await db.Set<GrantAssignment>().Where(x => x.ProposalId == view.Id).ToArrayAsync());
    }

    private static GrantSpec LevelSpec(FieldAdvanceFixture f) => Spec(f) with
    {
        Capabilities = ["request.approve"],
        DelegableCapabilities = ["request.approve"],
        ApprovalLimit = 100m,
        ApprovalLevels = ["project-manager"],
        DelegableApprovalLevels = ["project-manager"],
        PrimaryApprovalLevels = ["project-manager"],
        DelegablePrimaryApprovalLevels = ["project-manager"]
    };

    private static async Task LevelAuthorityAsync(FieldAdvanceFixture f)
    {
        await using var db = f.CreateContext();
        foreach (var source in await db.Set<GrantProposal>().Where(x => x.RecipientId == AuthorId || x.RecipientId == ReviewerId).ToArrayAsync())
        {
            source.DelegableApprovalLevels = ["supervisor", "project-manager"];
            source.DelegablePrimaryApprovalLevels = ["supervisor", "project-manager"];
        }
        await db.SaveChangesAsync();
    }
}
