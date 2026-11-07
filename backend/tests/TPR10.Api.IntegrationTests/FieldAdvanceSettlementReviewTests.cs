using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Policies;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Settlements;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementAccessTests;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementDraftTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceSettlementReviewTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Returned_round_requires_owner_and_all_new_decisions()
    {
        await using var f = await Ready(postgres); var (request, acceptedRound) = await Draft(f);
        Assert.Equal(200, (await Submit(f, AuthorId, request, acceptedRound, await ConfirmCommand(f, request, acceptedRound))).Status);
        foreach (var actor in new[] { ReviewerId, RecipientId, AliasAccountId }) Assert.Equal(200, (await Review(f, actor, request, acceptedRound)).Status);
        await ActivateCorrectionPolicy(f, 3);
        await using var db = f.CreateContext(); var doc = await db.Set<ExpenseDocument>().SingleAsync();
        var saved = await Save(f, request, null, Input(100, doc.Id)); Assert.Equal(201, saved.Status);
        var roundId = saved.Value!.EntityId!.Value;
        Assert.Equal(200, (await Submit(f, AuthorId, request, roundId, await ConfirmCommand(f, request, roundId))).Status);
        Assert.Equal(200, (await Review(f, ReviewerId, request, roundId)).Status);
        Assert.Equal(200, (await Review(f, RecipientId, request, roundId, "Return")).Status);
        var oldRevision = await db.Set<SettlementRevision>().AsNoTracking().SingleAsync(x => x.RoundId == roundId);
        Assert.Equal(1, await db.Set<SettlementApprovalStep>().CountAsync(x => x.RevisionId == oldRevision.Id && x.State == "Accepted"));
        var oldAcceptedTotal = await db.Set<SettlementRevision>().Where(x => x.AcceptedAtUtc != null).SumAsync(x => x.Total);
        Assert.Equal(100m, oldAcceptedTotal);
        Assert.Equal(200, (await Save(f, request, roundId, Input(90, doc.Id))).Status);
        var revisedRound = await db.Set<SettlementRound>().AsNoTracking().SingleAsync(x => x.Id == roundId);
        var latest = await db.Set<SettlementRevision>().AsNoTracking().Where(x => x.RoundId == roundId).OrderByDescending(x => x.RevisionNumber).FirstAsync();
        var newRevision = new { AcceptedStepCount = await db.Set<SettlementApprovalStep>().CountAsync(x => x.RevisionId == latest.Id && x.State == "Accepted") };
        var totals = new { ApprovedExpense = await db.Set<SettlementRevision>().Where(x => x.AcceptedAtUtc != null).SumAsync(x => x.Total) };
        Assert.Equal(0, newRevision.AcceptedStepCount);
        Assert.Equal("AwaitingOwner", revisedRound.State);
        Assert.Equal(oldAcceptedTotal, totals.ApprovedExpense);
        Assert.Null(latest.ConfirmedAtUtc);
        Assert.Equal("Returned", (await db.Set<SettlementRevision>().AsNoTracking().SingleAsync(x => x.Id == oldRevision.Id)).State);
    }

    [Fact]
    public async Task Owner_confirmation_then_three_distinct_decisions_consumes_once_and_replays()
    {
        await using var f = await Ready(postgres); var (request, round) = await Draft(f);
        var command = await ConfirmCommand(f, request, round);
        var submitted = await Submit(f, AuthorId, request, round, command);
        Assert.Equal(200, submitted.Status);
        Assert.Equal(submitted, await Submit(f, AuthorId, request, round, command));
        Assert.Equal(403, (await Review(f, RecipientId, request, round)).Status);
        Assert.Equal(200, (await Review(f, ReviewerId, request, round)).Status);
        Assert.Equal(200, (await Review(f, RecipientId, request, round)).Status);
        var finalCommand = await ReviewCommand(f, request, round);
        var final = await Review(f, AliasAccountId, request, round, command: finalCommand);
        Assert.Equal(200, final.Status); Assert.Equal("Accepted", final.Value!.State);
        Assert.Equal(final, await Review(f, AliasAccountId, request, round, command: finalCommand));
        await using var db = f.CreateContext();
        Assert.Equal(100m, await db.Set<SettlementRevision>().Where(x => x.RequestId == request && x.AcceptedAtUtc != null).SumAsync(x => x.Total));
        Assert.Equal(3, await db.Set<SettlementApprovalStep>().CountAsync(x => x.State == "Accepted"));
        Assert.All(await db.Set<ExpenseAllocation>().ToArrayAsync(), x => { Assert.Equal("Accepted", x.State); Assert.NotNull(x.AcceptedAtUtc); });
        Assert.Equal(0m, await db.Set<ExpenseAllocation>().Where(x => x.State == "Reserved").SumAsync(x => x.Amount));
        Assert.Equal(409, (await Save(f, request, round, Input(50))).Status);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("document")]
    [InlineData("evidence")]
    [InlineData("revision")]
    public async Task Confirmation_requires_reviewed_revision_and_document_content(string change)
    {
        await using var f = await Ready(postgres); var (request, round) = await Draft(f);
        var command = await ConfirmCommand(f, request, round);
        if (change == "missing") command = command with { ConfirmationHash = null };
        else if (change == "revision")
        {
            await using var db = f.CreateContext(); var doc = await db.Set<ExpenseDocument>().SingleAsync();
            Assert.Equal(200, (await Save(f, request, round, Input(90, doc.Id))).Status);
            command = (await Meta(f, request, 1)) with { ConfirmationHash = command.ConfirmationHash };
        }
        else if (change == "evidence")
        {
            await using var db = f.CreateContext(); var evidence = await db.Set<AdvanceEvidence>().SingleAsync();
            evidence.Sha256 = new('b', 64); await db.SaveChangesAsync();
        }
        else
        {
            await using var db = f.CreateContext(); var doc = await db.Set<ExpenseDocument>().SingleAsync();
            doc.Issuer = "แก้ร้าน"; doc.Version++; await db.SaveChangesAsync();
        }
        Assert.Equal(409, (await Submit(f, AuthorId, request, round, command)).Status);
        await using var verify = f.CreateContext();
        Assert.Empty(await verify.Set<SettlementRevision>().Where(x => x.SubmittedAtUtc != null).ToArrayAsync());
        Assert.Empty(await verify.Set<ExpenseAllocation>().Where(x => x.State == "Reserved").ToArrayAsync());
    }

    [Fact]
    public async Task Delegate_cannot_preview_or_confirm_and_missing_route_holds_without_reserving()
    {
        await using var f = await Ready(postgres); var (request, round) = await Draft(f);
        await Grant(f, RecipientId, RecipientPersonId, "request.delegate");
        await using var db = f.CreateContext();
        var service = await Submission(f, db, RecipientId);
        Assert.Equal(403, (await service.PreviewAsync(RecipientId, request, round, default)).Status);
        Assert.Equal(403, (await Submit(f, RecipientId, request, round, await ConfirmCommand(f, request, round))).Status);
        await db.Set<ReportingLine>().ExecuteUpdateAsync(x => x.SetProperty(y => y.ValidToUtc, f.Clock.GetUtcNow()));
        Assert.Equal(409, (await Submit(f, AuthorId, request, round, await ConfirmCommand(f, request, round))).Status);
        Assert.Empty(await db.Set<ExpenseAllocation>().Where(x => x.State == "Reserved").ToArrayAsync());
    }

    [Fact]
    public async Task Return_requires_policy_then_preserves_window_and_reservations_and_new_votes()
    {
        await using var f = await Ready(postgres); var (request, round) = await Draft(f);
        Assert.Equal(200, (await Submit(f, AuthorId, request, round, await ConfirmCommand(f, request, round))).Status);
        Assert.Equal(409, (await Review(f, ReviewerId, request, round, "Return")).Status);
        await using var db = f.CreateContext();
        await ActivateCorrectionPolicy(f, 3);
        Assert.Equal(200, (await Review(f, ReviewerId, request, round, "Return")).Status);
        var returned = await db.Set<SettlementRevision>().AsNoTracking().SingleAsync(x => x.RoundId == round);
        var local = TimeZoneInfo.ConvertTime(f.Clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById("Asia/Bangkok"));
        Assert.Equal(DateOnly.FromDateTime(local.DateTime).AddDays(3), returned.CorrectionDueOn);
        var returnStep = await db.Set<SettlementApprovalStep>().AsNoTracking().SingleAsync(x => x.State == "Returned");
        using var snapshot = JsonDocument.Parse(returnStep.SnapshotJson);
        Assert.Equal(3, snapshot.RootElement.GetProperty("CorrectionWindow").GetProperty("Days").GetInt32());
        Assert.Equal(100m, await db.Set<ExpenseAllocation>().Where(x => x.State == "Reserved").SumAsync(x => x.Amount));
        var doc = await db.Set<ExpenseDocument>().SingleAsync();
        Assert.Equal(200, (await Save(f, request, round, Input(90, doc.Id))).Status);
        Assert.Equal(90m, await db.Set<ExpenseAllocation>().Where(x => x.State == "Reserved").SumAsync(x => x.Amount));
        Assert.Equal(403, (await Review(f, ReviewerId, request, round)).Status);
        Assert.Equal(200, (await Submit(f, AuthorId, request, round, await ConfirmCommand(f, request, round))).Status);
        Assert.Equal(200, (await Review(f, ReviewerId, request, round)).Status);
        Assert.Equal(200, (await Review(f, RecipientId, request, round)).Status);
        Assert.Equal(200, (await Review(f, AliasAccountId, request, round)).Status);
        Assert.Equal("Returned", (await db.Set<SettlementRevision>().AsNoTracking().SingleAsync(x => x.Id == returned.Id)).State);
        Assert.Equal(returnStep.SnapshotJson, (await db.Set<SettlementApprovalStep>().AsNoTracking().SingleAsync(x => x.Id == returnStep.Id)).SnapshotJson);
    }

    [Theory]
    [InlineData("Reject", "เหตุผล")]
    [InlineData("Return", "")]
    [InlineData("Approve", "")]
    public async Task Unsupported_decision_or_missing_reason_does_not_record_vote(string decision, string reason)
    {
        await using var f = await Ready(postgres); var (request, round) = await Draft(f);
        Assert.Equal(200, (await Submit(f, AuthorId, request, round, await ConfirmCommand(f, request, round))).Status);
        Assert.Equal(422, (await Review(f, ReviewerId, request, round, decision, reason)).Status);
        await using var db = f.CreateContext(); Assert.Empty(await db.Set<SettlementApprovalStep>().Where(x => x.ReviewedAtUtc != null).ToArrayAsync());
    }


    [Theory]
    [InlineData("policy")]
    [InlineData("identity")]
    public async Task Changed_approved_basis_requires_new_draft_without_repricing_frozen_lines(string change)
    {
        await using var f = await Ready(postgres); var (request, round) = await Draft(f);
        var command = await ConfirmCommand(f, request, round);
        await using var db = f.CreateContext();
        var approved = await db.Set<AdvanceRequestRevision>().SingleAsync(x => x.RequestId == request);
        if (change == "policy") approved.PolicySnapshotJson = approved.PolicySnapshotJson!.Replace("50", "60");
        else
        {
            var replacement = (AdvanceRequestRevision)db.Entry(approved).CurrentValues.Clone().ToObject();
            replacement.Id = Guid.NewGuid(); replacement.RevisionNumber = 2; approved.IsCurrent = false;
            await db.SaveChangesAsync(); db.Add(replacement); await db.SaveChangesAsync();
            (await db.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == request)).ApprovedRevisionId = replacement.Id;
        }
        await db.SaveChangesAsync();
        Assert.Equal(409, (await Submit(f, AuthorId, request, round, command)).Status);
        Assert.Equal(409, (await (await Submission(f, db, AuthorId)).PreviewAsync(AuthorId, request, round, default)).Status);
        Assert.Equal(100m, (await db.Set<SettlementLine>().SingleAsync()).Amount);
    }

    [Fact]
    public async Task Current_supervisor_escalates_on_ineligible_grant_and_PM_uses_distinct_next_level()
    {
        await using var f = await Ready(postgres); var (request, round) = await Draft(f);
        await using var db = f.CreateContext();
        var supervisor = await db.Set<GrantProposal>().SingleAsync(x => x.RecipientId == ReviewerId);
        supervisor.ApprovalLimit = 50;
        // Candidate for supervisor escalates to PM; PM then escalates further to a fourth person.
        var checkerPerson = (await db.Set<FinanceAccountLink>().SingleAsync(x => x.UserId == AliasAccountId)).PersonId;
        db.Add(new ReportingLine
        {
            Id = Guid.NewGuid(),
            EmployeeMembershipId = (await db.Set<EmployeeMembership>().SingleAsync(x => x.UserId == ReviewerId)).Id,
            EmployeeUserId = ReviewerId,
            SupervisorUserId = RecipientId,
            ValidFromUtc = f.Clock.GetUtcNow().AddDays(-1),
            CreatedBy = AuthorId,
            Reason = "เลื่อนสาย"
        });
        await db.SaveChangesAsync();
        // No distinct next PM exists, so submission must hold rather than reuse the supervisor.
        var held = await Submit(f, AuthorId, request, round, await ConfirmCommand(f, request, round));
        Assert.Equal(409, held.Status); Assert.Equal("settlement-route-unavailable", held.Code);
        await using var tx = await db.Database.BeginTransactionAsync();
        var route = await new SettlementRouteResolver(db, new(db, f.Clock), f.Clock).ResolveAsync(request, round, default);
        Assert.Equal(RecipientPersonId, route.Supervisor.PersonId); Assert.Null(route.ProjectManager.PersonId);
        Assert.Equal(checkerPerson, route.FinanceChecker.PersonId);
    }

    [Fact]
    public async Task Review_revocation_after_audit_rolls_back_and_revoked_actor_cannot_replay()
    {
        await using var f = await Ready(postgres); var (request, round) = await Draft(f);
        Assert.Equal(200, (await Submit(f, AuthorId, request, round, await ConfirmCommand(f, request, round))).Status);
        var command = await ReviewCommand(f, request, round);
        await using var db = f.CreateContext(); var session = await f.SessionAsync(ReviewerId);
        var audit = new TPR10.Api.Auditing.AuditEventWriter(db, new TPR10.Api.Correlation.CorrelationContext(), f.Clock);
        var hooked = new FieldAdvanceRequestRaceTests.CallbackAudit(audit, async () =>
        {
            var grant = await db.Set<GrantAssignment>().SingleAsync(x => db.Set<GrantProposal>().Any(p => p.Id == x.ProposalId && p.RecipientId == ReviewerId));
            grant.RevokedAtUtc = f.Clock.GetUtcNow(); grant.RevokedBy = AuthorId; grant.RevocationReason = "ถอนระหว่าง audit"; await db.SaveChangesAsync();
        });
        var policies = new PolicyResolver(db, FieldAdvanceAccessTests.Access(db, session, f.Clock), session, f.Clock);
        var service = new SettlementReviewService(db, await Access(f, db, ReviewerId), await Executor(f, db, ReviewerId, hooked),
            new(db, new(db, f.Clock), f.Clock), new(db, policies, f.Clock), f.Clock);
        Assert.Equal(403, (await service.ReviewAsync(ReviewerId, request, round, new("Approve", "ตรวจแล้ว"), command, default)).Status);
        Assert.Empty(await db.Set<SettlementApprovalStep>().Where(x => x.ReviewedAtUtc != null).ToArrayAsync());
        Assert.False(await db.Set<FinanceCommandReceipt>().AnyAsync(x => x.CommandId == command.CommandId));
        Assert.Equal(200, (await Review(f, ReviewerId, request, round, command: command)).Status);
        var grantNow = await db.Set<GrantAssignment>().SingleAsync(x => db.Set<GrantProposal>().Any(p => p.Id == x.ProposalId && p.RecipientId == ReviewerId));
        grantNow.RevokedAtUtc = f.Clock.GetUtcNow(); grantNow.RevokedBy = AuthorId; grantNow.RevocationReason = "ถอนก่อน replay"; await db.SaveChangesAsync();
        Assert.Equal(403, (await Review(f, ReviewerId, request, round, command: command)).Status);
    }

    [Fact]
    public async Task Final_budget_failure_preserves_reserved_document_and_accepted_prior_round()
    {
        await using var f = await Ready(postgres); var (request, round) = await Draft(f);
        Assert.Equal(200, (await Submit(f, AuthorId, request, round, await ConfirmCommand(f, request, round))).Status);
        Assert.Equal(200, (await Review(f, ReviewerId, request, round)).Status);
        Assert.Equal(200, (await Review(f, RecipientId, request, round)).Status);
        Assert.Equal(200, (await Review(f, AliasAccountId, request, round)).Status);
        await using var db = f.CreateContext();
        var accepted = await db.Set<SettlementRevision>().AsNoTracking().SingleAsync(x => x.RoundId == round);
        var doc = await Document(f, request, 1000);
        var saved = await Save(f, request, null, Input(950, doc)); Assert.Equal(201, saved.Status);
        var second = saved.Value!.EntityId!.Value;
        Assert.Equal(200, (await Submit(f, AuthorId, request, second, await ConfirmCommand(f, request, second))).Status);
        Assert.Equal(200, (await Review(f, ReviewerId, request, second)).Status);
        Assert.Equal(200, (await Review(f, RecipientId, request, second)).Status);
        Assert.Equal("settlement-budget-exceeded", (await Review(f, AliasAccountId, request, second)).Code);
        Assert.Equal(950m, await db.Set<ExpenseAllocation>().Where(x => x.State == "Reserved").SumAsync(x => x.Amount));
        Assert.Equal(100m, await db.Set<SettlementRevision>().Where(x => x.AcceptedAtUtc != null).SumAsync(x => x.Total));
        Assert.Equal(JsonSerializer.Serialize(accepted), JsonSerializer.Serialize(await db.Set<SettlementRevision>().AsNoTracking().SingleAsync(x => x.Id == accepted.Id)));
    }

    [Fact]
    public async Task Stale_confirmation_blocks_approval_but_allows_return_for_new_owner_review()
    {
        await using var f = await Ready(postgres); var (request, round) = await Draft(f);
        await ActivateCorrectionPolicy(f, 3);
        Assert.Equal(200, (await Submit(f, AuthorId, request, round, await ConfirmCommand(f, request, round))).Status);
        await using var db = f.CreateContext();
        var basis = await db.Set<AdvanceRequestRevision>().SingleAsync(x => x.RequestId == request);
        basis.PolicySnapshotJson = basis.PolicySnapshotJson!.Replace("50", "60"); await db.SaveChangesAsync();
        Assert.Equal("settlement-confirmation-stale", (await Review(f, ReviewerId, request, round)).Code);
        Assert.Equal(200, (await Review(f, ReviewerId, request, round, "Return")).Status);
        Assert.Equal(100m, await db.Set<ExpenseAllocation>().Where(x => x.State == "Reserved").SumAsync(x => x.Amount));
    }


    [Fact]
    public async Task Concurrent_submission_replays_one_reserved_revision_and_preview_does_not_mutate()
    {
        await using var f = await Ready(postgres); var (request, round) = await Draft(f);
        var before = await Meta(f, request);
        await using var db = f.CreateContext(); var receiptCount = await db.Set<FinanceCommandReceipt>().CountAsync();
        var command = await ConfirmCommand(f, request, round);
        var after = await Meta(f, request);
        Assert.Equal(before.RequestVersion, after.RequestVersion); Assert.Equal(before.FamilyVersion, after.FamilyVersion);
        Assert.Equal(receiptCount, await db.Set<FinanceCommandReceipt>().CountAsync());
        var results = await Task.WhenAll(Submit(f, AuthorId, request, round, command), Submit(f, AuthorId, request, round, command));
        Assert.Equal(200, results[0].Status); Assert.Equal(results[0], results[1]);
        Assert.Equal(3, await db.Set<SettlementApprovalStep>().CountAsync());
        Assert.Equal(100m, await db.Set<ExpenseAllocation>().Where(x => x.State == "Reserved").SumAsync(x => x.Amount));
    }

    [Fact]
    public async Task Pending_assignment_rechecks_route_and_escalates_after_grant_limit_changes()
    {
        await using var f = await Ready(postgres); var (request, round) = await Draft(f);
        using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        var bytes = FieldAdvanceSettlementEvidenceTests.Image();
        Guid proof;
        await using (var evidenceDb = f.CreateContext())
        {
            var uploader = await FieldAdvanceSettlementEvidenceTests.Service(f, evidenceDb, files);
            var uploaded = await uploader.UploadAsync(AuthorId, request, new MemoryStream(bytes), "image/png", default);
            Assert.Equal(201, uploaded.Status); proof = uploaded.Value;
            var document = await evidenceDb.Set<ExpenseDocument>().AsNoTracking().SingleAsync(x => x.RequestId == request);
            var docs = await FieldAdvanceExpenseDocumentTests.Documents(f, evidenceDb, files);
            var input = new DocumentInput(document.Issuer, document.DocumentNumber, document.HasNoNumber,
                document.IssuedOn, document.Amount, [proof])
            { EvidenceType = "receipt" };
            Assert.Equal(200, (await docs.UpdateAsync(AuthorId, request, document.Id, input, await Meta(f, request, document.Version), default)).Status);
        }
        Assert.Equal(200, (await Submit(f, AuthorId, request, round, await ConfirmCommand(f, request, round))).Status);
        await using var db = f.CreateContext();
        var grant = await db.Set<GrantProposal>().SingleAsync(x => x.RecipientId == ReviewerId); grant.ApprovalLimit = 50;
        db.Add(new ReportingLine
        {
            Id = Guid.NewGuid(),
            EmployeeMembershipId = (await db.Set<EmployeeMembership>().SingleAsync(x => x.UserId == ReviewerId)).Id,
            EmployeeUserId = ReviewerId,
            SupervisorUserId = RecipientId,
            ValidFromUtc = f.Clock.GetUtcNow().AddDays(-1),
            CreatedBy = AuthorId,
            Reason = "สายงานสำรอง"
        });
        await db.SaveChangesAsync();
        using var keys = new TestKeyMaterial();
        var settings = new Dictionary<string, string?>(keys.Settings) { ["FinanceEvidence:ActiveStorageId"] = files.Options.ActiveStorageId.ToString() };
        foreach (var (storage, root) in files.Options.Roots) settings[$"FinanceEvidence:Roots:{storage}"] = root;
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, settings);
        using var replacement = await FieldAdvancePolicyHttpTests.Client(factory, f, RecipientId);
        var response = await replacement.GetAsync($"/api/v1/field-advances/settlement-work?workspaceId={WorkspaceId}&projectId={ProjectId}&lane=rounds");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains(page.RootElement.GetProperty("items").EnumerateArray(), x => x.GetProperty("requestId").GetGuid() == request);
        Assert.Equal(403, (await Review(f, ReviewerId, request, round)).Status);
        var revision = (await db.Set<SettlementRevision>().AsNoTracking().SingleAsync(x => x.RoundId == round)).Id;
        var basePath = $"/api/v1/field-advances/requests/{request}/settlement";
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await replacement.GetAsync(basePath)).StatusCode);
        var detail = await replacement.GetAsync(basePath + $"/rounds/{round}/revisions/{revision}");
        Assert.Equal(System.Net.HttpStatusCode.OK, detail.StatusCode);
        var evidence = await replacement.GetAsync(basePath + $"/rounds/{round}/revisions/{revision}/evidence/{proof}");
        Assert.Equal(System.Net.HttpStatusCode.OK, evidence.StatusCode);
        Assert.Equal(bytes, await evidence.Content.ReadAsByteArrayAsync());
        Assert.True(evidence.Headers.CacheControl!.NoStore);
        var reviewed = await FieldAdvancePolicyHttpTests.Send(replacement, System.Net.Http.HttpMethod.Post,
            $"requests/{request}/settlement/rounds/{round}/review",
            new { input = new ReviewInput("Approve", "ตรวจโดยผู้มีสิทธิ์สำรอง"), meta = await ReviewCommand(f, request, round) });
        Assert.Equal(System.Net.HttpStatusCode.OK, reviewed.StatusCode);
        var step = await db.Set<SettlementApprovalStep>().AsNoTracking().SingleAsync(x => x.StepOrder == 1);
        Assert.Equal(RecipientPersonId, step.AssigneePersonId); Assert.Equal(RecipientPersonId, step.ReviewedByPersonId);
        Assert.Equal(403, (await Review(f, RecipientId, request, round)).Status);
    }

    [Fact]
    public async Task Pending_assignment_refresh_rolls_back_on_denial_and_stale_version()
    {
        await using var f = await Ready(postgres); var (request, round) = await Draft(f);
        Assert.Equal(200, (await Submit(f, AuthorId, request, round, await ConfirmCommand(f, request, round))).Status);
        Guid stepId;
        Guid familyId;
        await using (var setup = f.CreateContext())
        {
            stepId = (await setup.Set<SettlementApprovalStep>().SingleAsync(x => x.RequestId == request && x.StepOrder == 1)).Id;
            familyId = (await setup.Set<AdvanceRequest>().SingleAsync(x => x.Id == request)).FamilyId;
            var grant = await setup.Set<GrantProposal>().SingleAsync(x => x.RecipientId == ReviewerId);
            grant.ApprovalLimit = 50;
            setup.Add(new ReportingLine
            {
                Id = Guid.NewGuid(),
                EmployeeMembershipId = (await setup.Set<EmployeeMembership>().SingleAsync(x => x.UserId == ReviewerId)).Id,
                EmployeeUserId = ReviewerId,
                SupervisorUserId = RecipientId,
                ValidFromUtc = f.Clock.GetUtcNow().AddDays(-1),
                CreatedBy = AuthorId,
                Reason = "สายงานสำรองสำหรับตรวจ rollback"
            });
            await setup.SaveChangesAsync();
        }

        // A fresh context after every command observes persisted state, never tracked values.
        async Task<(Guid? Assignee, long EntityVersion, long RequestVersion, long FamilyVersion,
            int Receipts, int Decisions, int PaymentParticipation, int RequestParticipation)> Persisted()
        {
            await using var verify = f.CreateContext();
            var step = await verify.Set<SettlementApprovalStep>().SingleAsync(x => x.Id == stepId);
            var storedRequest = await verify.Set<AdvanceRequest>().SingleAsync(x => x.Id == request);
            var family = await verify.Set<AdvanceFamily>().SingleAsync(x => x.Id == familyId);
            return (step.AssigneePersonId, step.Version, storedRequest.Version, family.Version,
                await verify.Set<FinanceCommandReceipt>().CountAsync(),
                await verify.Set<SettlementApprovalStep>().CountAsync(x => x.RequestId == request && x.ReviewedAtUtc != null),
                await verify.Set<AdvancePaymentParticipation>().CountAsync(x => x.FamilyId == familyId),
                await verify.Set<AdvanceRequestParticipant>().CountAsync(x => x.RequestId == request));
        }

        var baseline = await Persisted();
        Assert.Equal(ReviewerPersonId, baseline.Assignee);
        Assert.Equal(0, baseline.Decisions);
        Assert.Equal(0, baseline.PaymentParticipation);
        async Task UnchangedAfterFailure(Guid commandId)
        {
            var after = await Persisted();
            Assert.Equal(baseline.Assignee, after.Assignee);
            Assert.Equal(baseline.EntityVersion, after.EntityVersion);
            Assert.Equal(baseline.RequestVersion, after.RequestVersion);
            Assert.Equal(baseline.FamilyVersion, after.FamilyVersion);
            Assert.Equal(baseline.Receipts, after.Receipts);
            Assert.Equal(baseline.Decisions, after.Decisions);
            Assert.Equal(baseline.PaymentParticipation, after.PaymentParticipation);
            Assert.Equal(baseline.RequestParticipation, after.RequestParticipation);
            await using var verify = f.CreateContext();
            Assert.False(await verify.Set<FinanceCommandReceipt>().AnyAsync(x => x.CommandId == commandId));
            var step = await verify.Set<SettlementApprovalStep>().SingleAsync(x => x.Id == stepId);
            Assert.Equal("Pending", step.State);
            Assert.Null(step.ReviewedBy); Assert.Null(step.ReviewedByPersonId);
            Assert.Null(step.ReviewedAtUtc); Assert.Null(step.AcceptedAtUtc);
        }

        var denied = await ReviewCommand(f, request, round);
        Assert.Equal(403, (await Review(f, ReviewerId, request, round, command: denied)).Status);
        await UnchangedAfterFailure(denied.CommandId);

        foreach (var staleField in new[] { "entity", "request", "family" })
        {
            var current = await ReviewCommand(f, request, round);
            var stale = staleField switch
            {
                "entity" => current with { EntityVersion = baseline.EntityVersion + 1 },
                "request" => current with { RequestVersion = baseline.RequestVersion + 1 },
                _ => current with { FamilyVersion = baseline.FamilyVersion + 1 }
            };
            Assert.Equal(409, (await Review(f, RecipientId, request, round, command: stale)).Status);
            await UnchangedAfterFailure(stale.CommandId);
        }

        var fresh = await ReviewCommand(f, request, round);
        Assert.Equal(200, (await Review(f, RecipientId, request, round, command: fresh)).Status);
        var committed = await Persisted();
        Assert.Equal(RecipientPersonId, committed.Assignee);
        Assert.Equal(baseline.EntityVersion + 1, committed.EntityVersion);
        Assert.Equal(baseline.RequestVersion + 1, committed.RequestVersion);
        Assert.Equal(baseline.FamilyVersion + 1, committed.FamilyVersion);
        Assert.Equal(baseline.Receipts + 1, committed.Receipts);
        Assert.Equal(baseline.Decisions + 1, committed.Decisions);
        Assert.Equal(baseline.PaymentParticipation + 1, committed.PaymentParticipation);
        Assert.Equal(baseline.RequestParticipation, committed.RequestParticipation);
        await using var final = f.CreateContext();
        Assert.True(await final.Set<FinanceCommandReceipt>().AnyAsync(x => x.CommandId == fresh.CommandId));
        var accepted = await final.Set<SettlementApprovalStep>().SingleAsync(x => x.Id == stepId);
        Assert.Equal("Accepted", accepted.State);
        Assert.Equal(RecipientPersonId, accepted.ReviewedByPersonId);
        Assert.Equal(RecipientId, accepted.ReviewedBy);
        Assert.NotNull(accepted.AcceptedAtUtc);
    }

    [Fact]
    public void Legacy_command_json_remains_unchanged_when_confirmation_is_absent()
    {
        var command = new SettlementCommand(Guid.Empty, 2, 3, 4, "test");
        Assert.Equal("{\"CommandId\":\"00000000-0000-0000-0000-000000000000\",\"RequestVersion\":2,\"FamilyVersion\":3,\"EntityVersion\":4,\"Reason\":\"test\"}", JsonSerializer.Serialize(command));
    }

    [Fact]
    public async Task Finance_candidate_with_prior_family_approver_role_cannot_be_auto_assigned()
    {
        await using var f = await Ready(postgres); var (request, round) = await Draft(f);
        await using var db = f.CreateContext();
        var person = (await db.Set<FinanceAccountLink>().SingleAsync(x => x.UserId == AliasAccountId)).PersonId;
        db.Add(new AdvanceRequestParticipant
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            RequestId = request,
            PersonId = person,
            Role = "Approver",
            CreatedBy = AliasAccountId,
            CreatedAtUtc = f.Clock.GetUtcNow()
        });
        await db.SaveChangesAsync();
        Assert.Equal("settlement-route-unavailable", (await Submit(f, AuthorId, request, round, await ConfirmCommand(f, request, round))).Code);
        Assert.Empty(await db.Set<SettlementApprovalStep>().ToArrayAsync());
    }

    [Fact]
    public async Task Replay_authority_uses_original_revision_amount_after_returned_round_is_reduced()
    {
        await using var f = await Ready(postgres); var (request, round) = await Draft(f);
        await ActivateCorrectionPolicy(f, 3);
        Assert.Equal(200, (await Submit(f, AuthorId, request, round, await ConfirmCommand(f, request, round))).Status);
        var originalCommand = await ReviewCommand(f, request, round);
        Assert.Equal(200, (await Review(f, ReviewerId, request, round, command: originalCommand)).Status);
        Assert.Equal(200, (await Review(f, RecipientId, request, round, "Return")).Status);
        await using var db = f.CreateContext(); var doc = await db.Set<ExpenseDocument>().SingleAsync();
        Assert.Equal(200, (await Save(f, request, round, Input(90, doc.Id))).Status);
        var grant = await db.Set<GrantProposal>().SingleAsync(x => x.RecipientId == ReviewerId && x.Capabilities.Contains("request.approve"));
        grant.ApprovalLimit = 95; await db.SaveChangesAsync();
        Assert.Equal(403, (await Review(f, ReviewerId, request, round, command: originalCommand)).Status);
    }

    internal static async Task ActivateCorrectionPolicy(FieldAdvanceFixture f, int days)
    {
        foreach (var (actor, person) in new[] { (AuthorId, AuthorPersonId), (ReviewerId, ReviewerPersonId) })
        {
            var id = await FieldAdvanceGrantTests.AuthorityAsync(f, actor, person, company: true);
            await using var db = f.CreateContext();
            var assignment = await db.Set<GrantAssignment>().SingleAsync(x => x.Id == id);
            var grant = await db.Set<GrantProposal>().SingleAsync(x => x.Id == assignment.ProposalId);
            grant.Capabilities = ["policy.edit.company", "policy.review"]; await db.SaveChangesAsync();
        }
        var content = FieldAdvancePolicyTests.Content() with { CorrectionDays = days };
        var created = await FieldAdvancePolicyTests.Run(f, AuthorId, service => service.CreateDraftAsync(AuthorId,
            new(WorkspaceId, null), null, content,
            new(Guid.NewGuid(), 1, "นโยบายแก้ไข") { Policy = new("Company", CompanyRevisionId) }, default));
        Assert.True(created.Status == 201, created.Code);
        await FieldAdvancePolicyTests.Review(f, await FieldAdvancePolicyTests.Submit(f, created.Value!));
    }

    internal static async Task<(Guid Request, Guid Round)> Draft(FieldAdvanceFixture f)
    {
        var request = await Approved(f); var doc = await Document(f, request);
        await Grant(f, ReviewerId, ReviewerPersonId, "request.approve");
        await Grant(f, RecipientId, RecipientPersonId, "request.approve");
        await using var db = f.CreateContext();
        var checker = Guid.NewGuid();
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
        var pm = await db.Set<GrantProposal>().SingleAsync(x => x.RecipientId == RecipientId);
        pm.PrimaryApprovalLevels = ["project-manager"];
        db.Add(new ReportingLine
        {
            Id = Guid.NewGuid(),
            EmployeeMembershipId = (await db.Set<EmployeeMembership>().SingleAsync(x => x.UserId == AuthorId)).Id,
            EmployeeUserId = AuthorId,
            SupervisorUserId = ReviewerId,
            ValidFromUtc = f.Clock.GetUtcNow().AddDays(-1),
            CreatedBy = AuthorId,
            Reason = "สายงาน"
        });
        await db.SaveChangesAsync(); await Grant(f, AliasAccountId, checker, "finance.check");
        var saved = await Save(f, request, null, Input(100, doc)); Assert.Equal(201, saved.Status);
        return (request, saved.Value!.EntityId!.Value);
    }
    private static async Task<SettlementSubmissionService> Submission(FieldAdvanceFixture f, Tpr10DbContext db, Guid actor)
    {
        var executor = await Executor(f, db, actor);
        return new(db, await Access(f, db, actor), executor, new(db, executor), new(db, new(db, f.Clock), f.Clock), f.Clock);
    }
    internal static async Task<SettlementCommand> ConfirmCommand(FieldAdvanceFixture f, Guid request, Guid round)
    {
        await using var db = f.CreateContext(); var preview = await (await Submission(f, db, AuthorId)).PreviewAsync(AuthorId, request, round, default);
        Assert.Equal(200, preview.Status);
        return (await Meta(f, request, preview.Value!.RevisionVersion)) with { ConfirmationHash = preview.Value.ConfirmationHash };
    }
    internal static async Task<FinanceResult<SettlementCommandResult>> Submit(FieldAdvanceFixture f, Guid actor, Guid request, Guid round, SettlementCommand command)
    {
        await using var db = f.CreateContext(); return await (await Submission(f, db, actor)).SubmitAsync(actor, request, round, command, default);
    }
    internal static async Task<SettlementCommand> ReviewCommand(FieldAdvanceFixture f, Guid request, Guid round)
    {
        await using var db = f.CreateContext(); var revision = await db.Set<SettlementRevision>().Where(x => x.RoundId == round).OrderByDescending(x => x.RevisionNumber).FirstAsync();
        var step = await db.Set<SettlementApprovalStep>().Where(x => x.RevisionId == revision.Id && x.State == "Pending").OrderBy(x => x.StepOrder).FirstOrDefaultAsync();
        return await Meta(f, request, step?.Version);
    }
    internal static async Task<FinanceResult<SettlementCommandResult>> Review(FieldAdvanceFixture f, Guid actor, Guid request, Guid round,
        string decision = "Approve", string reason = "ตรวจแล้ว", SettlementCommand? command = null)
    {
        await using var db = f.CreateContext(); var session = await f.SessionAsync(actor);
        var policies = new PolicyResolver(db, FieldAdvanceAccessTests.Access(db, session, f.Clock), session, f.Clock);
        var service = new SettlementReviewService(db, await Access(f, db, actor), await Executor(f, db, actor),
            new(db, new(db, f.Clock), f.Clock), new(db, policies, f.Clock), f.Clock);
        return await service.ReviewAsync(actor, request, round, new(decision, reason), command ?? await ReviewCommand(f, request, round), default);
    }
}
