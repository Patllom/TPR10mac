using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Payments;
using TPR10.Api.FieldAdvances.Settlements;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceSettlementAccessTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Alias_or_revoked_grant_cannot_replay_finance_result()
    {
        await using var f = await Ready(postgres);
        var grant = await Grant(f, RecipientId, RecipientPersonId, "finance.check");
        var request = await Request(f);
        var meta = await Meta(f, request);
        var initial = await Execute(f, RecipientId, request, SettlementActions.Close, meta);
        Assert.Equal(200, initial.Status);
        Assert.Equal(initial, await Execute(f, RecipientId, request, SettlementActions.Close, meta));
        await using (var db = f.CreateContext())
        {
            var assignment = await db.Set<GrantAssignment>().SingleAsync(x => x.Id == grant);
            assignment.RevokedAtUtc = f.Clock.GetUtcNow(); assignment.RevokedBy = AuthorId;
            assignment.RevocationReason = "ถอนสิทธิ์ทดสอบ"; assignment.Version++;
            db.Add(new FinanceAccountLink
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                UserId = AliasAccountId,
                PersonId = AuthorPersonId,
                CreatedBy = AuthorId,
                CreatedAtUtc = f.Clock.GetUtcNow().AddMinutes(-1),
                Reason = "บัญชีที่สอง"
            });
            await db.SaveChangesAsync();
        }
        await Grant(f, AliasAccountId, AuthorPersonId, "finance.check");
        await using var verify = f.CreateContext();
        var beforeReceiptCount = await verify.Set<FinanceCommandReceipt>().CountAsync();
        var replayAfterRevocation = await Execute(f, RecipientId, request, SettlementActions.Close, meta);
        var ownerSecondAccountReview = await Execute(f, AliasAccountId, request, SettlementActions.Close, await Meta(f, request));
        Assert.Equal(403, replayAfterRevocation.Status);
        Assert.Equal(403, ownerSecondAccountReview.Status);
        var afterDeniedReceiptCount = await verify.Set<FinanceCommandReceipt>().CountAsync();
        Assert.Equal(beforeReceiptCount, afterDeniedReceiptCount);
    }

    [Theory]
    [InlineData("request.create", SettlementActions.Draft, true)]
    [InlineData("request.create", SettlementActions.Confirm, true)]
    [InlineData("request.create", SettlementActions.Declare, true)]
    [InlineData("finance.check", SettlementActions.Close, false)]
    [InlineData("payment.record", SettlementActions.IncidentPropose, false)]
    [InlineData("report.read", SettlementActions.Read, true)]
    [InlineData("request.create", "unknown", false)]
    public async Task Owner_needs_exact_current_capability_and_never_finance_role(string capability, string action, bool allowed)
    {
        await using var f = await Ready(postgres);
        await Grant(f, AuthorId, AuthorPersonId, capability);
        var request = await Request(f);
        Assert.Equal(allowed, (await Check(f, AuthorId, request, action)).Allowed);
    }

    [Theory]
    [InlineData("Approver", "finance.check", SettlementActions.Close)]
    [InlineData("Checker", "payment.record", SettlementActions.IncidentPropose)]
    [InlineData("Payer", "finance.check", SettlementActions.Close)]
    [InlineData("Delegate", "finance.check", SettlementActions.Close)]
    public async Task Family_history_in_root_blocks_supplement_role_switch(string role, string capability, string action)
    {
        await using var f = await Ready(postgres);
        await Grant(f, RecipientId, RecipientPersonId, capability);
        var root = await Request(f);
        Guid family;
        await using (var db = f.CreateContext())
        {
            family = (await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == root)).FamilyId;
            db.Add(new AdvanceRequestParticipant
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                ProjectId = ProjectId,
                RequestId = root,
                PersonId = RecipientPersonId,
                Role = role,
                CreatedBy = RecipientId,
                CreatedAtUtc = f.Clock.GetUtcNow()
            });
            await db.SaveChangesAsync();
        }
        var target = await Request(f, family, root);
        Assert.False((await Check(f, RecipientId, target, action)).Allowed);
    }

    [Fact]
    public async Task Delegate_can_prepare_but_only_owner_can_confirm_and_neither_becomes_payer()
    {
        await using var f = await Ready(postgres);
        await Grant(f, AuthorId, AuthorPersonId, "request.create");
        await Grant(f, RecipientId, RecipientPersonId, "request.delegate");
        var request = await Request(f, delegatePerson: RecipientPersonId);
        Assert.True((await Check(f, RecipientId, request, SettlementActions.Draft)).Allowed);
        Assert.False((await Check(f, RecipientId, request, SettlementActions.Confirm)).Allowed);
        Assert.Equal(200, (await Execute(f, AuthorId, request, SettlementActions.Declare, await Meta(f, request))).Status);
        Assert.Equal(200, (await Execute(f, RecipientId, request, SettlementActions.Draft, await Meta(f, request))).Status);
        await using var verify = f.CreateContext();
        Assert.Empty(await verify.Set<AdvancePaymentParticipation>().ToArrayAsync());
    }

    [Fact]
    public async Task Scope_and_transaction_are_required_even_for_admin_or_team_member()
    {
        await using var f = await Ready(postgres);
        var request = await Request(f);
        Assert.False((await Check(f, ReviewerId, request, SettlementActions.Read)).Allowed);
        await Grant(f, RecipientId, RecipientPersonId, "finance.check", Guid.Parse("71000000-0000-0000-0000-000000000012"));
        Assert.False((await Check(f, RecipientId, request, SettlementActions.Close)).Allowed);
        await using var db = f.CreateContext();
        var service = await Access(f, db, AuthorId);
        Assert.False((await service.CheckAsync(AuthorId, request, SettlementActions.Draft, null, default)).Allowed);
    }

    [Theory]
    [InlineData("supervisor")]
    [InlineData("project-manager")]
    public async Task Assigned_approval_requires_current_position_and_distinct_revision_person(string level)
    {
        await using var f = await Ready(postgres);
        await Grant(f, ReviewerId, ReviewerPersonId, "request.approve");
        var request = await Request(f);
        Guid stepId;
        await using (var db = f.CreateContext())
        {
            var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request);
            var round = await FieldAdvanceSettlementCommandTests.Row<SettlementRound>(f, db, r); db.Add(round); await db.SaveChangesAsync();
            var revision = await FieldAdvanceSettlementCommandTests.Row<SettlementRevision>(f, db, r);
            revision.RoundId = round.Id; revision.RevisionNumber = 1; revision.Total = 100; revision.SubmittedAtUtc = f.Clock.GetUtcNow();
            revision.ConfirmedBy = AuthorId; revision.ConfirmedByPersonId = AuthorPersonId; revision.ConfirmedAtUtc = f.Clock.GetUtcNow();
            db.Add(revision); await db.SaveChangesAsync();
            var step = await FieldAdvanceSettlementCommandTests.Row<SettlementApprovalStep>(f, db, r);
            step.RevisionId = revision.Id; step.LevelCode = level == "supervisor" ? "Supervisor" : "ProjectManager";
            step.StepOrder = level == "supervisor" ? 1 : 2; step.AssigneePersonId = ReviewerPersonId;
            if (level == "project-manager")
            {
                var previous = await FieldAdvanceSettlementCommandTests.Row<SettlementApprovalStep>(f, db, r);
                previous.RevisionId = revision.Id; previous.LevelCode = "Supervisor"; previous.StepOrder = 1;
                previous.AssigneePersonId = RecipientPersonId; previous.ReviewedBy = RecipientId; previous.ReviewedByPersonId = RecipientPersonId;
                previous.ReviewedAtUtc = f.Clock.GetUtcNow(); previous.AcceptedAtUtc = f.Clock.GetUtcNow(); previous.State = "Accepted";
                db.Add(previous); await db.SaveChangesAsync();
            }
            db.Add(step);
            if (level == "supervisor")
                db.Add(new TPR10.Api.Attendance.Directory.ReportingLine
                {
                    Id = Guid.NewGuid(),
                    EmployeeMembershipId = (await db.Set<TPR10.Api.Attendance.Directory.EmployeeMembership>().SingleAsync(x => x.UserId == AuthorId)).Id,
                    EmployeeUserId = AuthorId,
                    SupervisorUserId = ReviewerId,
                    ValidFromUtc = f.Clock.GetUtcNow().AddDays(-1),
                    CreatedBy = AuthorId,
                    Reason = "สายงานทดสอบ"
                });
            else (await db.Set<GrantProposal>().SingleAsync(x => x.RecipientId == ReviewerId)).PrimaryApprovalLevels = [level];
            await db.SaveChangesAsync(); stepId = step.Id;
        }
        Assert.True((await Check(f, ReviewerId, request, SettlementActions.Approve, stepId)).Allowed);
        await using (var db = f.CreateContext())
        {
            if (level == "supervisor")
                (await db.Set<TPR10.Api.Attendance.Directory.ReportingLine>().SingleAsync()).ValidToUtc = f.Clock.GetUtcNow();
            else (await db.Set<GrantProposal>().SingleAsync(x => x.RecipientId == ReviewerId)).PrimaryApprovalLevels = [];
            await db.SaveChangesAsync();
        }
        Assert.False((await Check(f, ReviewerId, request, SettlementActions.Approve, stepId)).Allowed);
    }

    [Fact]
    public async Task Second_account_of_proposer_cannot_review_and_cannot_borrow_first_accounts_grant()
    {
        await using var f = await Ready(postgres);
        await Grant(f, RecipientId, RecipientPersonId, "finance.check");
        var request = await Request(f);
        var proposal = await FieldAdvanceSettlementCommandTests.Completion(f, request, RecipientId, RecipientPersonId);
        await using (var db = f.CreateContext())
        {
            db.Add(new FinanceAccountLink
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                UserId = AliasAccountId,
                PersonId = RecipientPersonId,
                CreatedBy = AuthorId,
                CreatedAtUtc = f.Clock.GetUtcNow().AddMinutes(-1),
                Reason = "บัญชีที่สอง"
            });
            await db.SaveChangesAsync();
        }
        Assert.False((await Check(f, AliasAccountId, request, SettlementActions.Close)).Allowed);
        await Grant(f, AliasAccountId, RecipientPersonId, "finance.check");
        Assert.True((await Check(f, AliasAccountId, request, SettlementActions.Close)).Allowed);
        Assert.False((await Check(f, AliasAccountId, request, SettlementActions.CompletionReview, proposal.Id)).Allowed);
    }

    [Fact]
    public async Task Finance_round_review_requires_assigned_finance_step_after_two_distinct_approvals()
    {
        await using var f = await Ready(postgres);
        var checker = Guid.NewGuid();
        await using (var db = f.CreateContext())
        {
            db.Add(new FinancePerson { Id = checker, WorkspaceId = WorkspaceId, CreatedBy = AuthorId, Reason = "ผู้ตรวจจำลอง" });
            db.Add(new FinanceAccountLink
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                UserId = AliasAccountId,
                PersonId = checker,
                CreatedBy = AuthorId,
                CreatedAtUtc = f.Clock.GetUtcNow().AddMinutes(-1),
                Reason = "ผู้ตรวจจำลอง"
            });
            await db.SaveChangesAsync();
        }
        await Grant(f, AliasAccountId, checker, "finance.check");
        var request = await Request(f); Guid stepId; Guid revisionId;
        await using (var db = f.CreateContext())
        {
            var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request);
            var round = await FieldAdvanceSettlementCommandTests.Row<SettlementRound>(f, db, r); db.Add(round); await db.SaveChangesAsync();
            var revision = await FieldAdvanceSettlementCommandTests.Row<SettlementRevision>(f, db, r);
            revision.RoundId = round.Id; revision.RevisionNumber = 1; revision.Total = 100; revision.SubmittedAtUtc = f.Clock.GetUtcNow();
            revision.ConfirmedBy = AuthorId; revision.ConfirmedByPersonId = AuthorPersonId; revision.ConfirmedAtUtc = f.Clock.GetUtcNow();
            db.Add(revision); await db.SaveChangesAsync(); revisionId = revision.Id;
            foreach (var (order, level, account, person) in new[] { (1, "Supervisor", ReviewerId, ReviewerPersonId), (2, "ProjectManager", RecipientId, RecipientPersonId) })
            {
                var approval = await FieldAdvanceSettlementCommandTests.Row<SettlementApprovalStep>(f, db, r);
                approval.RevisionId = revision.Id; approval.StepOrder = order; approval.LevelCode = level; approval.AssigneePersonId = person;
                approval.ReviewedBy = account; approval.ReviewedByPersonId = person; approval.ReviewedAtUtc = f.Clock.GetUtcNow();
                approval.AcceptedAtUtc = f.Clock.GetUtcNow(); approval.State = "Accepted";
                db.Add(approval); await db.SaveChangesAsync();
            }
            var step = await FieldAdvanceSettlementCommandTests.Row<SettlementApprovalStep>(f, db, r);
            step.RevisionId = revision.Id; step.StepOrder = 3; step.LevelCode = "Finance"; step.AssigneePersonId = checker;
            db.Add(step); await db.SaveChangesAsync(); stepId = step.Id;
        }
        Assert.True((await Check(f, AliasAccountId, request, SettlementActions.Check, stepId)).Allowed);
        Assert.False((await Check(f, AliasAccountId, request, SettlementActions.Check, revisionId)).Allowed);
        await using (var db = f.CreateContext())
        {
            (await db.Set<SettlementApprovalStep>().SingleAsync(x => x.Id == stepId)).AssigneePersonId = RecipientPersonId;
            await db.SaveChangesAsync();
        }
        Assert.False((await Check(f, AliasAccountId, request, SettlementActions.Check, stepId)).Allowed);
    }

    internal static async Task<FieldAdvanceFixture> Ready(PostgresFixture postgres)
    {
        var f = await CreateAsync(postgres); await f.SeedPoliciesAsync(); return f;
    }
    internal static async Task<Guid> Grant(FieldAdvanceFixture f, Guid actor, Guid person, string capability, Guid? project = null)
    {
        var id = await FieldAdvanceGrantTests.AuthorityAsync(f, actor, person, projectId: project);
        await using var db = f.CreateContext();
        var assignment = await db.Set<GrantAssignment>().SingleAsync(x => x.Id == id);
        var proposal = await db.Set<GrantProposal>().SingleAsync(x => x.Id == assignment.ProposalId);
        proposal.Capabilities = [capability];
        if (capability == "request.approve") proposal.ApprovalLevels = ["supervisor", "project-manager"];
        await db.SaveChangesAsync(); return id;
    }
    internal static async Task<Guid> Request(FieldAdvanceFixture f, Guid? family = null, Guid? root = null, Guid? delegatePerson = null)
    {
        await using var db = f.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        var request = new AdvanceRequest
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            OwnerPersonId = AuthorPersonId,
            DelegatePersonId = delegatePerson,
            CreatedBy = AuthorId,
            State = "Approved"
        };
        if (family is { } id)
        {
            var familyRow = await db.Set<AdvanceFamily>().FromSqlInterpolated($"SELECT * FROM field_advance_families WHERE id={id} FOR UPDATE").SingleAsync();
            request.FamilyId = id; request.RelationKind = "Supplement"; request.SupplementOfRequestId = root;
            request.FamilyVersionAtCreation = ++familyRow.Version;
        }
        db.Add(request); await db.SaveChangesAsync(); await tx.CommitAsync(); return request.Id;
    }
    internal static async Task<SettlementCommand> Meta(FieldAdvanceFixture f, Guid request, long? entityVersion = null)
    {
        await using var db = f.CreateContext(); var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request);
        var family = await db.Set<AdvanceFamily>().SingleAsync(x => x.Id == r.FamilyId);
        return new(Guid.NewGuid(), r.Version, family.Version, entityVersion, "ทดสอบคำสั่งเคลียร์");
    }
    internal static async Task<SettlementAccess> Access(FieldAdvanceFixture f, Tpr10DbContext db, Guid actor)
    {
        var session = await f.SessionAsync(actor);
        return new(db, FieldAdvanceAccessTests.Access(db, session, f.Clock), new(db, f.Clock), f.Clock);
    }
    internal static async Task<SettlementAccessDecision> Check(FieldAdvanceFixture f, Guid actor, Guid request, string action, Guid? entity = null)
    {
        await using var db = f.CreateContext(); var access = await Access(f, db, actor);
        await using var tx = await db.Database.BeginTransactionAsync();
        return await access.CheckAsync(actor, request, action, entity, default);
    }
    internal static async Task<SettlementCommandExecutor> Executor(FieldAdvanceFixture f, Tpr10DbContext db, Guid actor, IAuditEventWriter? audit = null)
    {
        var session = await f.SessionAsync(actor); var finance = FieldAdvanceAccessTests.Access(db, session, f.Clock);
        return new(db, new(db, finance, new(db, f.Clock), f.Clock), new(db, session, finance, new(db, f.Clock), f.Clock),
            audit ?? new AuditEventWriter(db, new CorrelationContext(), f.Clock), f.Clock);
    }
    internal static async Task<FinanceResult<SettlementCommandResult>> Execute(FieldAdvanceFixture f, Guid actor, Guid request,
        string action, SettlementCommand meta, string? hash = null)
    {
        await using var db = f.CreateContext(); var executor = await Executor(f, db, actor);
        return await executor.ExecuteAsync(actor, request, action, meta, hash ?? new string('a', 64),
            _ => Task.FromResult(new FinanceResult<SettlementCommandResult>(200, "ok", new(request, null, "Approved", meta.RequestVersion, meta.FamilyVersion, null))), default);
    }
}
