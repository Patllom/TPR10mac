using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Net.Http.Json;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementCommandTests;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Settlements;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementAccessTests;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementDraftTests;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementReviewTests;
using static TPR10.Api.IntegrationTests.FieldAdvanceRefundTests;
using static TPR10.Api.IntegrationTests.FieldAdvancePolicyHttpTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceExpenseAdjustmentTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Reduction_keeps_document_consumed_until_full_route_approves()
    {
        await using var f = await Ready(postgres);
        var (request, round) = await Draft(f);
        await using var db = f.CreateContext();
        var document = await db.Set<ExpenseDocument>().SingleAsync(); document.Amount = 10000;
        var account = await db.Set<AdvancePaymentAccount>().SingleAsync(); account.Approved = account.Spendable = 10000;
        foreach (var grant in await db.Set<GrantProposal>().Where(x => x.ApprovalLimit != null).ToArrayAsync()) grant.ApprovalLimit = 20000;
        await db.SaveChangesAsync();
        Assert.Equal(200, (await Save(f, request, round, Input(8000, document.Id))).Status);
        await SeedPaid(f, request, 8000);
        await Approve(f, request, round);
        var source = await db.Set<SettlementRevision>().AsNoTracking().SingleAsync(x => x.RoundId == round && x.AcceptedAtUtc != null);
        var line = await db.Set<SettlementLine>().SingleAsync(x => x.RevisionId == source.Id);
        var replacement = new RoundInput([Input(6000, document.Id).Lines[0] with { SourceLineId = line.Id }]);
        var executor = await Executor(f, db, AuthorId);
        var service = new ExpenseAdjustmentService(new(db, await Access(f, db, AuthorId), executor, new(db, executor), f.Clock));
        var proposed = await service.ProposeAsync(AuthorId, request, round, replacement, await Meta(f, request), default);
        Assert.Equal(201, proposed.Status);
        var adjustment = proposed.Value!.EntityId!.Value;
        var pendingTotals = await Totals(f, request);
        var pendingDocument = new { Consumed = await db.Set<ExpenseAllocation>().Where(x => x.DocumentId == document.Id && x.AcceptedAtUtc != null).SumAsync(x => x.Amount) };
        Assert.Equal(8000m, pendingTotals.ApprovedExpense);
        Assert.Equal(8000m, pendingDocument.Consumed);
        await Approve(f, request, adjustment);
        var approvedTotals = await Totals(f, request);
        Assert.Equal(6000m, approvedTotals.ApprovedExpense);
        Assert.Equal(2000m, approvedTotals.OwnerOutstanding);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(150)]
    public async Task Replacement_preserves_originals_applies_once_and_requires_latest_source(decimal amount)
    {
        await using var f = await Ready(postgres); var (request, round) = await Draft(f);
        await Approve(f, request, round);
        await using var db = f.CreateContext();
        var original = await db.Set<SettlementRevision>().AsNoTracking().SingleAsync(x => x.RoundId == round);
        var originalLine = await db.Set<SettlementLine>().AsNoTracking().SingleAsync(x => x.RevisionId == original.Id);
        var doc = await db.Set<ExpenseDocument>().SingleAsync();
        var replacement = Replacement(originalLine.Id, doc.Id, amount);
        var command = await Meta(f, request);
        var service = await Adjustments(f, db);
        var proposal = await service.ProposeAsync(AuthorId, request, round, replacement, command, default);
        Assert.Equal(201, proposal.Status);
        Assert.Equal(proposal, await service.ProposeAsync(AuthorId, request, round, replacement, command, default));
        var adjusted = proposal.Value!.EntityId!.Value;
        Assert.Equal(409, (await service.ProposeAsync(AuthorId, request, round, replacement, await Meta(f, request), default)).Status);
        Assert.Equal(200, (await Submit(f, AuthorId, request, adjusted, await ConfirmCommand(f, request, adjusted))).Status);
        var usage = await ExpenseAllocationLedger.UsageAsync(db, request, doc.Id, null, default);
        Assert.Equal(100m, usage.Consumed); Assert.Equal(Math.Max(0, amount - 100), usage.Reserved);
        Assert.Equal(Math.Max(0, amount - 100), await db.Set<ExpenseAllocation>().Where(x => x.State == "Reserved").SumAsync(x => x.Amount));
        Assert.Equal(200, (await Review(f, ReviewerId, request, adjusted)).Status);
        Assert.Equal(100m, (await Totals(f, request)).ApprovedExpense);
        Assert.Equal(200, (await Review(f, RecipientId, request, adjusted)).Status);
        var finalCommand = await ReviewCommand(f, request, adjusted);
        var final = await Review(f, AliasAccountId, request, adjusted, command: finalCommand);
        Assert.Equal(200, final.Status); Assert.Equal(final, await Review(f, AliasAccountId, request, adjusted, command: finalCommand));
        Assert.Equal(amount, (await Totals(f, request)).ApprovedExpense);
        usage = await ExpenseAllocationLedger.UsageAsync(db, request, doc.Id, null, default);
        Assert.Equal(amount, usage.Consumed); Assert.Equal(0, usage.Reserved);
        Assert.Equal(100, (await db.Set<SettlementRevision>().AsNoTracking().SingleAsync(x => x.Id == original.Id)).Total);
        Assert.Equal(409, (await service.ProposeAsync(AuthorId, request, round, replacement, await Meta(f, request), default)).Status);
        var nextLine = await db.Set<SettlementLine>().AsNoTracking().SingleAsync(x => x.RevisionId != original.Id);
        var next = await service.ProposeAsync(AuthorId, request, adjusted, Replacement(nextLine.Id, doc.Id, 50), await Meta(f, request), default);
        Assert.Equal(201, next.Status); await Approve(f, request, next.Value!.EntityId!.Value);
        Assert.Equal(50m, (await Totals(f, request)).ApprovedExpense);
    }

    [Fact]
    public async Task Return_restarts_entire_route_and_keeps_consumed_until_reapproval()
    {
        await using var f = await Ready(postgres); var (request, round) = await Draft(f); await Approve(f, request, round);
        await FieldAdvanceSettlementReviewTests.ActivateCorrectionPolicy(f, 3);
        await using var db = f.CreateContext(); var doc = await db.Set<ExpenseDocument>().SingleAsync();
        var line = await db.Set<SettlementLine>().SingleAsync(); var service = await Adjustments(f, db);
        var proposal = await service.ProposeAsync(AuthorId, request, round, Replacement(line.Id, doc.Id, 150), await Meta(f, request), default);
        Assert.Equal(201, proposal.Status); var adjustment = proposal.Value!.EntityId!.Value;
        Assert.Equal(200, (await Submit(f, AuthorId, request, adjustment, await ConfirmCommand(f, request, adjustment))).Status);
        Assert.Equal(200, (await Review(f, ReviewerId, request, adjustment)).Status);
        Assert.Equal(200, (await Review(f, RecipientId, request, adjustment, "Return")).Status);
        Assert.Equal(200, (await Save(f, request, adjustment, Replacement(line.Id, doc.Id, 80))).Status);
        Assert.Equal(403, (await Review(f, RecipientId, request, adjustment)).Status);
        Assert.Equal(100m, (await Totals(f, request)).ApprovedExpense);
        await Approve(f, request, adjustment); Assert.Equal(80m, (await Totals(f, request)).ApprovedExpense);
    }

    [Fact]
    public async Task Ordinary_zero_is_rejected_by_storage_as_well_as_service()
    {
        await using var f = await Ready(postgres); var (request, round) = await Draft(f);
        await using var db = f.CreateContext();
        Assert.Equal(422, (await Save(f, request, round, Input(0))).Status);
        var failure = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE field_advance_settlement_lines SET lump_sum=0, amount=0"));
        Assert.Equal("23514", failure.SqlState);
    }

    [Fact]
    public async Task Excess_invoice_12000_cap_10000_tracks_2000_without_changing_expense_or_draft()
    {
        await using var f = await Ready(postgres); var (request, round) = await Draft(f);
        await using var db = f.CreateContext(); using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        var doc = await db.Set<ExpenseDocument>().SingleAsync(); doc.Amount = 12000;
        var account = await db.Set<AdvancePaymentAccount>().SingleAsync(); account.Approved = account.Spendable = 10000;
        await db.SaveChangesAsync();
        Assert.Equal(200, (await Save(f, request, round, Input(12000, doc.Id))).Status);
        var service = new ExpenseExcessService(db, await Access(f, db, AuthorId), await Executor(f, db, AuthorId), f.Clock, files.Options);
        var command = await Meta(f, request);
        var result = await service.TrackAsync(AuthorId, request, round, doc.Id, command, default);
        Assert.Equal(201, result.Status);
        Assert.Equal(result, await service.TrackAsync(AuthorId, request, round, doc.Id, command, default));
        var excess = await db.Set<SettlementExcessCase>().AsNoTracking().SingleAsync();
        Assert.Equal(2000m, excess.Amount); Assert.Equal(12000m, excess.ClaimedAmount);
        var totals = await Totals(f, request); Assert.Equal(0, totals.ApprovedExpense); Assert.True(totals.HasOpenCase);
        Assert.Equal(12000m, (await db.Set<SettlementRevision>().OrderByDescending(x => x.RevisionNumber).FirstAsync()).Total);
        Assert.Equal(200, (await Save(f, request, round, Input(10000, doc.Id))).Status);
        Assert.True((await Totals(f, request)).HasOpenCase);
        Assert.Equal(2000m, (await db.Set<SettlementExcessCase>().AsNoTracking().SingleAsync()).Amount);
    }

    [Fact]
    public async Task Services_resolve_from_actual_application_provider()
    {
        await using var f = await Ready(postgres); using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ExpenseAdjustmentService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ExpenseExcessService>());
    }

    [Theory]
    [InlineData("NoAdditionalPaymentRequired")]
    [InlineData("FinancialSettlementRecorded")]
    public async Task Excess_disposition_requires_checker_evidence_and_real_linked_financial_facts(string disposition)
    {
        await using var f = await Ready(postgres); var request = await Approved(f); var doc = await Document(f, request, 1200);
        await Grant(f, ReviewerId, ReviewerPersonId, "finance.check"); await Grant(f, RecipientId, RecipientPersonId, "payment.record");
        var draft = await Save(f, request, null, Input(1200, doc)); Assert.Equal(201, draft.Status);
        await using var db = f.CreateContext(); using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        async Task<ExpenseExcessService> Service(Guid actor) => new(db, await Access(f, db, actor), await Executor(f, db, actor), f.Clock, files.Options);
        var author = await Service(AuthorId); var checker = await Service(ReviewerId); var payer = await Service(RecipientId);
        var tracked = await author.TrackAsync(AuthorId, request, draft.Value!.EntityId!.Value, doc, await Meta(f, request), default);
        Assert.Equal(201, tracked.Status); var excessId = tracked.Value!.EntityId!.Value;
        var evidence = (await db.Set<AdvanceEvidence>().SingleAsync()).Id;
        async Task<SettlementCommand> CaseCommand() => await Meta(f, request, (await db.Set<SettlementExcessCase>().AsNoTracking().SingleAsync()).Version);
        Assert.Equal(403, (await author.RecordDispositionAsync(AuthorId, request, excessId, disposition, [evidence], await CaseCommand(), default)).Status);
        Assert.Equal(422, (await checker.RecordDispositionAsync(ReviewerId, request, excessId, disposition, [], await CaseCommand(), default)).Status);
        Assert.Equal(409, (await checker.RecordDispositionAsync(ReviewerId, request, excessId, "FinancialSettlementRecorded", [evidence], await CaseCommand(), default)).Status);
        Assert.Equal(200, (await checker.RecordDispositionAsync(ReviewerId, request, excessId, "AwaitingExternalReview", [evidence], await CaseCommand(), default)).Status);
        Assert.True((await Totals(f, request)).HasOpenCase);
        Guid[] paymentIds = [];
        if (disposition == "FinancialSettlementRecorded")
        {
            var input = new ExcessPaymentInput(excessId, AuthorPersonId, 200, new(2026, 10, 20), [evidence]);
            var proposed = await payer.ProposeExternalPaymentAsync(RecipientId, request, input, await Meta(f, request), default);
            Assert.Equal(201, proposed.Status); var payment = proposed.Value!.EntityId!.Value;
            Assert.Equal(409, (await payer.ProposeExternalPaymentAsync(RecipientId, request, input, await Meta(f, request), default)).Status);
            Assert.Equal(403, (await payer.ConfirmExternalPaymentAsync(RecipientId, request, payment, await Meta(f, request, 1), default)).Status);
            Assert.Equal(409, (await checker.RecordDispositionAsync(ReviewerId, request, excessId, new(disposition, [evidence], [payment]), await CaseCommand(), default)).Status);
            var confirm = await Meta(f, request, 1);
            var accepted = await checker.ConfirmExternalPaymentAsync(ReviewerId, request, payment, confirm, default);
            Assert.Equal(200, accepted.Status); Assert.Equal(accepted, await checker.ConfirmExternalPaymentAsync(ReviewerId, request, payment, confirm, default));
            paymentIds = [payment];
            using (var keys = new TestKeyMaterial())
            {
                await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
                using var client = await Client(factory, f, ReviewerId);
                var queue = await client.GetFromJsonAsync<JsonElement>(
                    $"/api/v1/field-advances/settlement-work?workspaceId={WorkspaceId}&projectId={ProjectId}&lane=excess");
                var item = Assert.Single(queue.GetProperty("items").EnumerateArray());
                var linked = item.GetProperty("detail").GetProperty("excess")
                    .GetProperty("acceptedPaymentIds").EnumerateArray().Select(value => value.GetGuid()).ToArray();
                Assert.Equal(paymentIds, linked);
            }
            Assert.Equal(200m, (await db.Set<SettlementExcessPayment>().AsNoTracking().SingleAsync()).Amount);
            Assert.Equal(0, (await Totals(f, request)).OwnerOutstanding);
            var failure = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE field_advance_settlement_excess_payments SET amount=100"));
            Assert.Equal("23514", failure.SqlState);
        }
        var result = await checker.RecordDispositionAsync(ReviewerId, request, excessId, new(disposition, [evidence], paymentIds), await CaseCommand(), default);
        Assert.Equal(200, result.Status); Assert.Equal("Resolved", result.Value!.State);
        Assert.False((await Totals(f, request)).HasOpenCase); Assert.Equal(0, (await Totals(f, request)).ApprovedExpense);
        Assert.Equal(200, (await db.Set<SettlementExcessCase>().AsNoTracking().SingleAsync()).Amount);
        Assert.Equal(2, await db.Set<SettlementExcessDisposition>().CountAsync());
    }

    [Fact]
    public async Task Closed_expense_adjustment_preserves_request_and_closure_and_uses_full_route()
    {
        await using var f = await Ready(postgres); var (request, round) = await Draft(f); await SeedPaid(f, request, 100);
        await Approve(f, request, round);
        await using var db = f.CreateContext(); var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request);
        void Accept<T>(T value) where T : class
        {
            var e = db.Entry(value); e.Property("State").CurrentValue = "Accepted";
            e.Property("AcceptedAtUtc").CurrentValue = e.Property("ReviewedAtUtc").CurrentValue = f.Clock.GetUtcNow();
            e.Property("ReviewedBy").CurrentValue = ReviewerId; e.Property("ReviewedByPersonId").CurrentValue = ReviewerPersonId;
        }
        var completion = await Row<WorkCompletion>(f, db, r); completion.CompletedOn = new(2026, 10, 20);
        completion.ClearingDueOn = completion.RefundDueOn = completion.CompletedOn; Accept(completion); db.Add(completion); await db.SaveChangesAsync();
        var declaration = await Row<SubmissionDeclaration>(f, db, r); declaration.WorkCompletionId = completion.Id; Accept(declaration); db.Add(declaration); await db.SaveChangesAsync();
        var closure = await Row<ClosureRecord>(f, db, r); closure.WorkCompletionId = completion.Id; closure.DeclarationId = declaration.Id;
        closure.RequestVersion = r.Version; closure.FamilyVersion = (await db.Set<AdvanceFamily>().SingleAsync()).Version;
        Accept(closure); db.Add(closure); r.State = "Closed"; await db.SaveChangesAsync();
        var before = JsonSerializer.Serialize(r); var closureBefore = JsonSerializer.Serialize(closure);
        var line = await db.Set<SettlementLine>().SingleAsync(); var doc = await db.Set<ExpenseDocument>().SingleAsync();
        using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        var ownerEvidence = await FieldAdvanceSettlementEvidenceTests.Service(f, db, files, AuthorId);
        var bytes = FieldAdvanceSettlementEvidenceTests.Image();
        Assert.Equal(403, (await ownerEvidence.UploadAsync(AuthorId, request, new MemoryStream(bytes), "image/png", default)).Status);
        var uploaded = await ownerEvidence.UploadAdjustmentAsync(AuthorId, request, new ExpenseEvidenceTarget(round), new MemoryStream(bytes), "image/png", default);
        Assert.Equal(201, uploaded.Status);
        var documents = await FieldAdvanceExpenseDocumentTests.Documents(f, db, files);
        var documentInput = new DocumentInput("ใบใหม่หลังปิด", "NEW-10", false, new(2026, 10, 20), 100, [uploaded.Value]) { EvidenceType = "receipt" };
        Assert.Equal(403, (await documents.RegisterAsync(AuthorId, request, documentInput, await Meta(f, request), default)).Status);
        var registered = await documents.RegisterAdjustmentAsync(AuthorId, request, round, documentInput, await Meta(f, request), default);
        Assert.Equal(201, registered.Status);
        var newDocument = registered.Value!.EntityId!.Value;
        var proposal = await (await Adjustments(f, db)).ProposeAsync(AuthorId, request, round, Replacement(line.Id, newDocument, 80), await Meta(f, request), default);
        Assert.Equal(201, proposal.Status); var adjustment = proposal.Value!.EntityId!.Value;
        Assert.Equal(0, (await Totals(f, request)).OwnerOutstanding);
        await Approve(f, request, adjustment);
        Assert.Equal(20m, (await Totals(f, request)).OwnerOutstanding);
        var adjustmentCase = await db.Set<SettlementAdjustmentCase>().AsNoTracking().SingleAsync();
        Assert.Equal(round, adjustmentCase.RootExpenseRoundId); Assert.Null(adjustmentCase.RootRefundId);
        Assert.Equal(20m, await AdjustmentCaseService.OutstandingAsync(db, adjustmentCase.Id, default));
        Assert.Equal(before, JsonSerializer.Serialize(await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync()));
        Assert.Equal(closureBefore, JsonSerializer.Serialize(await db.Set<ClosureRecord>().AsNoTracking().SingleAsync()));
        Assert.Equal(403, (await Save(f, request, null, Input(10, doc.Id))).Status);
        Assert.Equal(0, (await ExpenseAllocationLedger.UsageAsync(db, request, doc.Id, null, default)).Consumed);
        var cases = new AdjustmentCaseService(db, await Access(f, db, AliasAccountId), await Executor(f, db, AliasAccountId), f.Clock, files.Options);
        Assert.Equal(409, (await cases.CloseCaseAsync(AliasAccountId, request, adjustmentCase.Id, await Meta(f, request), default)).Status);
        var refund = await RefundCall(f, request, "Propose", input: RefundInputFor(20, uploaded.Value) with { AdjustmentCaseId = adjustmentCase.Id });
        Assert.Equal(201, refund.Status); var refundId = refund.Value!.EntityId!.Value;
        Assert.Equal(200, (await RefundCall(f, request, "Confirm", refundId)).Status);
        Assert.Equal(200, (await RefundCall(f, request, "Approve", refundId, actor: AliasAccountId)).Status);
        Assert.Equal(200, (await cases.CloseCaseAsync(AliasAccountId, request, adjustmentCase.Id, await Meta(f, request), default)).Status);
        var current = await db.Set<SettlementRevision>().AsNoTracking().SingleAsync(x => x.RoundId == adjustment);
        var currentLine = await db.Set<SettlementLine>().SingleAsync(x => x.RevisionId == current.Id);
        var next = await (await Adjustments(f, db)).ProposeAsync(AuthorId, request, adjustment, Replacement(currentLine.Id, newDocument, 70), await Meta(f, request), default);
        Assert.Equal(201, next.Status); await Approve(f, request, next.Value!.EntityId!.Value);
        var freshCase = await db.Set<SettlementAdjustmentCase>().AsNoTracking().SingleAsync(x => x.Id != adjustmentCase.Id);
        Assert.Equal(80, freshCase.BaselineAmount); Assert.Equal(10, await AdjustmentCaseService.OutstandingAsync(db, freshCase.Id, default));
        Assert.Equal(closureBefore, JsonSerializer.Serialize(await db.Set<ClosureRecord>().AsNoTracking().SingleAsync()));
        Assert.DoesNotContain(await db.Set<AdvancePaymentParticipation>().ToArrayAsync(), x => x.PersonId == AuthorPersonId && x.Role == "Payer");
    }

    [Fact]
    public async Task Returned_excess_payment_resubmits_and_cancel_releases_proof_and_capacity()
    {
        await using var f = await Ready(postgres); var request = await Approved(f); var doc = await Document(f, request, 1200);
        await Grant(f, ReviewerId, ReviewerPersonId, "finance.check"); await Grant(f, RecipientId, RecipientPersonId, "payment.record");
        var draft = await Save(f, request, null, Input(1200, doc));
        await using var db = f.CreateContext(); using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        async Task<ExpenseExcessService> Service(Guid actor) => new(db, await Access(f, db, actor), await Executor(f, db, actor), f.Clock, files.Options);
        var author = await Service(AuthorId); var checker = await Service(ReviewerId); var payer = await Service(RecipientId);
        var tracked = await author.TrackAsync(AuthorId, request, draft.Value!.EntityId!.Value, doc, await Meta(f, request), default);
        var excessId = tracked.Value!.EntityId!.Value; var evidence = (await db.Set<AdvanceEvidence>().SingleAsync()).Id;
        var input = new ExcessPaymentInput(excessId, AuthorPersonId, 200, new(2026, 10, 20), [evidence]);
        var first = await payer.ProposeExternalPaymentAsync(RecipientId, request, input, await Meta(f, request), default);
        Assert.Equal(201, first.Status); var payment = first.Value!.EntityId!.Value;
        Assert.Equal(200, (await checker.ReturnExternalPaymentAsync(ReviewerId, request, payment, await Meta(f, request, 1), default)).Status);
        Assert.Equal(409, (await checker.ConfirmExternalPaymentAsync(ReviewerId, request, payment, await Meta(f, request, 2), default)).Status);
        var second = await payer.ProposeExternalPaymentAsync(RecipientId, request, input with { SupersedesId = payment }, await Meta(f, request), default);
        Assert.Equal(201, second.Status); var replacement = second.Value!.EntityId!.Value;
        Assert.Equal("Superseded", (await db.Set<SettlementExcessPayment>().AsNoTracking().SingleAsync(x => x.Id == payment)).State);
        Assert.Equal(200, (await payer.CancelExternalPaymentAsync(RecipientId, request, replacement, await Meta(f, request, 1), default)).Status);
        var third = await payer.ProposeExternalPaymentAsync(RecipientId, request, input, await Meta(f, request), default);
        Assert.Equal(201, third.Status);
        Assert.Equal(200, (await checker.RejectExternalPaymentAsync(ReviewerId, request, third.Value!.EntityId!.Value, await Meta(f, request, 1), default)).Status);
        Assert.Empty(await db.Set<SettlementExcessPayment>().Where(x => x.AcceptedAtUtc != null).ToArrayAsync());
        Assert.Equal(0, (await Totals(f, request)).ApprovedExpense);
        Assert.True((await Totals(f, request)).HasOpenCase);
    }

    [Fact]
    public async Task Concurrent_adjustment_proposals_and_document_reservations_do_not_release_pending_reduction()
    {
        await using var f = await Ready(postgres); var (request, round) = await Draft(f);
        await using var db = f.CreateContext(); var doc = await db.Set<ExpenseDocument>().SingleAsync(); doc.Amount = 100; await db.SaveChangesAsync();
        await Approve(f, request, round); var line = await db.Set<SettlementLine>().SingleAsync();
        var command = await Meta(f, request);
        async Task<TPR10.Api.FieldAdvances.FinanceResult<SettlementCommandResult>> Propose()
        {
            await using var context = f.CreateContext(); var service = await Adjustments(f, context);
            return await service.ProposeAsync(AuthorId, request, round, Replacement(line.Id, doc.Id, 60), command with { CommandId = Guid.NewGuid() }, default);
        }
        var results = await Task.WhenAll(Propose(), Propose());
        Assert.Single(results, x => x.Status == 201); Assert.Single(results, x => x.Status == 409);
        var adjustment = results.Single(x => x.Status == 201).Value!.EntityId!.Value;
        Assert.Equal(200, (await Submit(f, AuthorId, request, adjustment, await ConfirmCommand(f, request, adjustment))).Status);
        var other = await Save(f, request, null, Input(40, doc.Id)); Assert.Equal(201, other.Status);
        Assert.Equal("document-budget-exceeded", (await Submit(f, AuthorId, request, other.Value!.EntityId!.Value,
            await ConfirmCommand(f, request, other.Value!.EntityId!.Value))).Code);
        foreach (var actor in new[] { ReviewerId, RecipientId, AliasAccountId }) Assert.Equal(200, (await Review(f, actor, request, adjustment)).Status);
        await Approve(f, request, other.Value!.EntityId!.Value);
        Assert.Equal(100, (await Totals(f, request)).ApprovedExpense);
        Assert.Equal(100, (await ExpenseAllocationLedger.UsageAsync(db, request, doc.Id, null, default)).Consumed);
    }

    [Fact]
    public async Task Proposal_audit_failure_rolls_back_adjustment_receipt_and_version()
    {
        await using var f = await Ready(postgres); var (request, round) = await Draft(f); await Approve(f, request, round);
        await using var db = f.CreateContext(); var doc = await db.Set<ExpenseDocument>().SingleAsync(); var line = await db.Set<SettlementLine>().SingleAsync();
        var before = await FieldAdvancePaymentRaceTests.Snapshot(f);
        var audit = new FieldAdvanceRequestRaceTests.CallbackAudit(new TPR10.Api.Auditing.AuditEventWriter(db, new TPR10.Api.Correlation.CorrelationContext(), f.Clock),
            () => throw new InvalidOperationException("task10 synthetic audit failure"));
        var executor = await Executor(f, db, AuthorId, audit);
        var service = new ExpenseAdjustmentService(new(db, await Access(f, db, AuthorId), executor, new(db, executor), f.Clock));
        var command = await Meta(f, request);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ProposeAsync(AuthorId, request, round,
            Replacement(line.Id, doc.Id, 60), command, default));
        Assert.Equal(before, await FieldAdvancePaymentRaceTests.Snapshot(f));
        Assert.Empty(await db.Set<SettlementExpenseAdjustment>().ToArrayAsync()); Assert.Equal(100, (await Totals(f, request)).ApprovedExpense);
    }

    [Fact]
    public async Task Adjustment_rejects_missing_duplicate_foreign_source_lines_and_over_cap_final_approval()
    {
        await using var f = await Ready(postgres); var (request, round) = await Draft(f); await Approve(f, request, round);
        await using var db = f.CreateContext(); var doc = await db.Set<ExpenseDocument>().SingleAsync(); var line = await db.Set<SettlementLine>().SingleAsync();
        var service = await Adjustments(f, db);
        foreach (var input in new[] { new RoundInput([]), Input(90, doc.Id), Replacement(Guid.NewGuid(), doc.Id, 90), new RoundInput([Replacement(line.Id, doc.Id, 50).Lines[0], Replacement(line.Id, doc.Id, 50).Lines[0]]) })
            Assert.Equal(422, (await service.ProposeAsync(AuthorId, request, round, input, await Meta(f, request), default)).Status);
        var other = await Approved(f);
        Assert.Equal(409, (await service.ProposeAsync(AuthorId, other, round, Replacement(line.Id, doc.Id, 90), await Meta(f, other), default)).Status);
        var account = await db.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == request); account.Approved = account.Spendable = 1000; await db.SaveChangesAsync();
        var proposal = await service.ProposeAsync(AuthorId, request, round, Replacement(line.Id, doc.Id, 150), await Meta(f, request), default);
        Assert.Equal(201, proposal.Status); var adjustment = proposal.Value!.EntityId!.Value;
        Assert.Equal(200, (await Submit(f, AuthorId, request, adjustment, await ConfirmCommand(f, request, adjustment))).Status);
        Assert.Equal(200, (await Review(f, ReviewerId, request, adjustment)).Status); Assert.Equal(200, (await Review(f, RecipientId, request, adjustment)).Status);
        var otherDocument = await Document(f, request, 900);
        var competing = await Save(f, request, null, Input(900, otherDocument)); Assert.Equal(201, competing.Status);
        await Approve(f, request, competing.Value!.EntityId!.Value);
        Assert.Equal("settlement-budget-exceeded", (await Review(f, AliasAccountId, request, adjustment)).Code);
        Assert.Equal(1000, (await Totals(f, request)).ApprovedExpense);
    }

    [Fact]
    public async Task Case_and_excess_payments_share_proof_claims_across_requests_and_release_on_cancel()
    {
        await using var f = await Ready(postgres); await FieldAdvanceRefundCorrectionTests.Payer(f);
        var old = await FieldAdvanceRefundCorrectionTests.NegativeCase(f);
        var request = await Approved(f); var document = await Document(f, request, 1200); var draft = await Save(f, request, null, Input(1200, document));
        await using var db = f.CreateContext(); using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        var evidence = await db.Set<AdvanceEvidence>().SingleAsync(x => x.RequestId == request);
        evidence.Sha256 = (await db.Set<AdvanceEvidence>().SingleAsync(x => x.Id == old.Evidence)).Sha256; await db.SaveChangesAsync();
        var tracking = new ExpenseExcessService(db, await Access(f, db, AuthorId), await Executor(f, db, AuthorId), f.Clock, files.Options);
        var tracked = await tracking.TrackAsync(AuthorId, request, draft.Value!.EntityId!.Value, document, await Meta(f, request), default);
        Assert.Equal(201, tracked.Status); var excess = tracked.Value!.EntityId!.Value;
        var cases = new AdjustmentCaseService(db, await Access(f, db, AliasAccountId), await Executor(f, db, AliasAccountId), f.Clock, files.Options);
        var service = new ExpenseExcessService(db, await Access(f, db, AliasAccountId), await Executor(f, db, AliasAccountId), f.Clock, files.Options);
        var caseInput = new CasePaymentInput(old.Case, 500, new(2026, 10, 20), [old.Evidence]);
        var excessInput = new ExcessPaymentInput(excess, AuthorPersonId, 200, new(2026, 10, 20), [evidence.Id]);
        var first = await cases.ProposeCasePaymentAsync(AliasAccountId, old.Request, caseInput, await Meta(f, old.Request), default);
        Assert.Equal(201, first.Status);
        Assert.Equal("excess-payment-proof-duplicate", (await service.ProposeExternalPaymentAsync(AliasAccountId, request, excessInput, await Meta(f, request), default)).Code);
        Assert.Equal(200, (await cases.CancelCasePaymentAsync(AliasAccountId, old.Request, first.Value!.EntityId!.Value, await Meta(f, old.Request, 1), default)).Status);
        var caseCommand = await Meta(f, old.Request); var excessCommand = await Meta(f, request);
        async Task<TPR10.Api.FieldAdvances.FinanceResult<SettlementCommandResult>> CaseProposal()
        {
            await using var context = f.CreateContext(); var c = new AdjustmentCaseService(context, await Access(f, context, AliasAccountId), await Executor(f, context, AliasAccountId), f.Clock, files.Options);
            return await c.ProposeCasePaymentAsync(AliasAccountId, old.Request, caseInput, caseCommand, default);
        }
        async Task<TPR10.Api.FieldAdvances.FinanceResult<SettlementCommandResult>> ExcessProposal()
        {
            await using var context = f.CreateContext(); var e = new ExpenseExcessService(context, await Access(f, context, AliasAccountId), await Executor(f, context, AliasAccountId), f.Clock, files.Options);
            return await e.ProposeExternalPaymentAsync(AliasAccountId, request, excessInput, excessCommand, default);
        }
        var race = await Task.WhenAll(CaseProposal(), ExcessProposal());
        Assert.Single(race, x => x.Status == 201); Assert.Single(race, x => x.Status == 409);
        Assert.Equal(1, await db.Set<SettlementCasePayment>().CountAsync(x => x.State == "Pending") + await db.Set<SettlementExcessPayment>().CountAsync(x => x.State == "Pending"));
        Assert.Equal(0, (await Totals(f, request)).OwnerOutstanding);
    }

    [Fact]
    public async Task Increase_requires_available_cap_before_submission_without_reserving_documents()
    {
        await using var f = await Ready(postgres); var (request, round) = await Draft(f); await Approve(f, request, round);
        await using var db = f.CreateContext(); var doc = await db.Set<ExpenseDocument>().SingleAsync(); var line = await db.Set<SettlementLine>().SingleAsync();
        var account = await db.Set<AdvancePaymentAccount>().SingleAsync(); account.Approved = account.Spendable = 100; await db.SaveChangesAsync();
        var proposal = await (await Adjustments(f, db)).ProposeAsync(AuthorId, request, round, Replacement(line.Id, doc.Id, 150), await Meta(f, request), default);
        Assert.Equal(201, proposal.Status); var adjustment = proposal.Value!.EntityId!.Value;
        Assert.Equal("settlement-budget-exceeded", (await Submit(f, AuthorId, request, adjustment, await ConfirmCommand(f, request, adjustment))).Code);
        Assert.Equal(0, (await ExpenseAllocationLedger.UsageAsync(db, request, doc.Id, null, default)).Reserved);
        Assert.Equal(100, (await Totals(f, request)).ApprovedExpense);
    }

    [Fact]
    public async Task Excess_tracking_uses_full_returned_replacement_claim_not_retained_reservation()
    {
        await using var f = await Ready(postgres); var (request, round) = await Draft(f);
        await FieldAdvanceSettlementReviewTests.ActivateCorrectionPolicy(f, 3);
        await using var db = f.CreateContext(); var doc = await db.Set<ExpenseDocument>().SingleAsync(); doc.Amount = 1200; await db.SaveChangesAsync();
        Assert.Equal(200, (await Submit(f, AuthorId, request, round, await ConfirmCommand(f, request, round))).Status);
        Assert.Equal(200, (await Review(f, ReviewerId, request, round, "Return")).Status);
        Assert.Equal(200, (await Save(f, request, round, Input(1200, doc.Id))).Status);
        Assert.Equal(100m, await db.Set<ExpenseAllocation>().Where(x => x.State == "Reserved").SumAsync(x => x.Amount));
        using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        var service = new ExpenseExcessService(db, await Access(f, db, AuthorId), await Executor(f, db, AuthorId), f.Clock, files.Options);
        var result = await service.TrackAsync(AuthorId, request, round, doc.Id, await Meta(f, request), default);
        Assert.Equal(201, result.Status);
        Assert.Equal(200m, (await db.Set<SettlementExcessCase>().AsNoTracking().SingleAsync()).Amount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Excess_multidocument_tracks_share_one_envelope_and_payment_capacity(bool concurrent)
    {
        await using var f = await Ready(postgres); var request = await Approved(f);
        var a = await Document(f, request, 6000); var b = await Document(f, request, 6000);
        await Grant(f, RecipientId, RecipientPersonId, "payment.record");
        await using var db = f.CreateContext(); using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        var account = await db.Set<AdvancePaymentAccount>().SingleAsync(); account.Approved = account.Spendable = 10000;
        var proofs = await db.Set<AdvanceEvidence>().OrderBy(x => x.Id).ToArrayAsync(); proofs[1].Sha256 = new('b', 64);
        await db.SaveChangesAsync();
        var draft = await Save(f, request, null, Multi(a, 6000, b, 6000)); Assert.Equal(201, draft.Status);
        var round = draft.Value!.EntityId!.Value; var command = await Meta(f, request);
        async Task<TPR10.Api.FieldAdvances.FinanceResult<SettlementCommandResult>> Track(Guid doc, SettlementCommand meta)
        {
            await using var context = f.CreateContext();
            var service = new ExpenseExcessService(context, await Access(f, context, AuthorId), await Executor(f, context, AuthorId), f.Clock, files.Options);
            return await service.TrackAsync(AuthorId, request, round, doc, meta, default);
        }
        if (concurrent)
        {
            var results = await Task.WhenAll(Track(a, command), Track(b, command with { CommandId = Guid.NewGuid() }));
            Assert.Single(results, x => x.Status == 201); Assert.Single(results, x => x.Status == 409);
            // Retry the stale loser with fresh versions: lock/version checking alone is not attribution.
            var loser = results[0].Status == 201 ? b : a;
            Assert.Equal(409, (await Track(loser, await Meta(f, request))).Status);
        }
        else
        {
            Assert.Equal(201, (await Track(a, command)).Status);
            Assert.Equal(409, (await Track(b, await Meta(f, request))).Status);
        }
        var cases = await db.Set<SettlementExcessCase>().AsNoTracking().ToArrayAsync();
        Assert.Single(cases); Assert.Equal(2000m, cases.Sum(x => x.Amount));
        Assert.True(cases.Sum(x => x.Amount) <= 2000m);
        using var snapshot = JsonDocument.Parse(cases[0].SnapshotJson);
        var basis = snapshot.RootElement.GetProperty("AttributionBasis");
        Assert.Equal(round, basis.GetProperty("SourceRound").GetGuid());
        Assert.Equal(12000m, basis.GetProperty("FullClaim").GetDecimal());
        Assert.Equal(10000m, basis.GetProperty("AvailableCap").GetDecimal());
        Assert.Equal(2000m, basis.GetProperty("ExcessEnvelope").GetDecimal());
        var documentClaims = basis.GetProperty("DocumentClaims").EnumerateArray().ToArray();
        Assert.Equal(2, documentClaims.Length);
        Assert.All(documentClaims, x => Assert.Equal(6000m, x.GetProperty("Claim").GetDecimal()));
        Assert.Equal(new[] { a, b }.Order(), documentClaims.Select(x => x.GetProperty("Document").GetGuid()).Order());
        var payer = new ExpenseExcessService(db, await Access(f, db, RecipientId), await Executor(f, db, RecipientId), f.Clock, files.Options);
        var input = new ExcessPaymentInput(cases[0].Id, AuthorPersonId, 2000, new(2026, 10, 20), [proofs[0].Id]);
        Assert.Equal(201, (await payer.ProposeExternalPaymentAsync(RecipientId, request, input, await Meta(f, request), default)).Status);
        var extra = await payer.ProposeExternalPaymentAsync(RecipientId, request, input with { Amount = 1, EvidenceIds = [proofs[1].Id] }, await Meta(f, request), default);
        Assert.Equal("excess-payment-capacity-exceeded", extra.Code);
        Assert.Equal(2000m, await db.Set<SettlementExcessPayment>().SumAsync(x => x.Amount));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Excess_multidocument_rejects_incomplete_or_overclaimed_document_basis(bool overclaimed)
    {
        await using var f = await Ready(postgres); var request = await Approved(f);
        var a = await Document(f, request, 600); var b = await Document(f, request, overclaimed ? 500 : 600);
        var other = Input(600, b).Lines[0];
        if (!overclaimed) other = other with { Allocations = [] };
        var draft = await Save(f, request, null, new([Input(600, a).Lines[0], other])); Assert.Equal(201, draft.Status);
        await using var db = f.CreateContext(); using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        var service = new ExpenseExcessService(db, await Access(f, db, AuthorId), await Executor(f, db, AuthorId), f.Clock, files.Options);
        Assert.Equal(409, (await service.TrackAsync(AuthorId, request, draft.Value!.EntityId!.Value, a, await Meta(f, request), default)).Status);
        Assert.Empty(await db.Set<SettlementExcessCase>().ToArrayAsync());
    }

    [Theory]
    [InlineData("resolved")]
    [InlineData("revision")]
    [InlineData("reduced")]
    [InlineData("returned")]
    [InlineData("cap")]
    public async Task Excess_multidocument_historical_attribution_survives_resolution_and_basis_changes(string change)
    {
        await using var f = await Ready(postgres); var (request, round) = await Draft(f);
        await using var db = f.CreateContext(); using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        var a = await db.Set<ExpenseDocument>().SingleAsync(); a.Amount = 600; await db.SaveChangesAsync();
        var b = await Document(f, request, 2000);
        if (change == "returned")
        {
            await FieldAdvanceSettlementReviewTests.ActivateCorrectionPolicy(f, 3);
            Assert.Equal(200, (await Submit(f, AuthorId, request, round, await ConfirmCommand(f, request, round))).Status);
            Assert.Equal(200, (await Review(f, ReviewerId, request, round, "Return")).Status);
        }
        Assert.Equal(200, (await Save(f, request, round, Multi(a.Id, 600, b, 2000))).Status);
        var author = new ExpenseExcessService(db, await Access(f, db, AuthorId), await Executor(f, db, AuthorId), f.Clock, files.Options);
        var first = await author.TrackAsync(AuthorId, request, round, a.Id, await Meta(f, request), default); Assert.Equal(201, first.Status);
        var original = await db.Set<SettlementExcessCase>().AsNoTracking().SingleAsync(); Assert.Equal(600m, original.Amount);
        var checker = new ExpenseExcessService(db, await Access(f, db, AliasAccountId), await Executor(f, db, AliasAccountId), f.Clock, files.Options);
        Assert.Equal(200, (await checker.RecordDispositionAsync(AliasAccountId, request, original.Id, "NoAdditionalPaymentRequired", [a.EvidenceId], await Meta(f, request, original.Version), default)).Status);
        if (change is "revision" or "returned" or "reduced")
            Assert.Equal(200, (await Save(f, request, round, Multi(a.Id, change == "reduced" ? 100 : 600, b, 2000))).Status);
        if (change == "cap")
        {
            var account = await db.Set<AdvancePaymentAccount>().SingleAsync(); account.Approved = account.Spendable = 900; await db.SaveChangesAsync();
        }
        var next = await author.TrackAsync(AuthorId, request, round, b, await Meta(f, request), default);
        Assert.Equal(change == "resolved" ? 201 : 409, next.Status);
        if (change != "resolved") Assert.Equal("excess-basis-changed", next.Code);
        var all = await db.Set<SettlementExcessCase>().AsNoTracking().ToArrayAsync();
        Assert.Equal(change == "resolved" ? 1600m : 600m, all.Sum(x => x.Amount));
        Assert.True(all.Sum(x => x.Amount) <= 1600m);
        var preserved = Assert.Single(all, x => x.Id == original.Id);
        Assert.Equal("Resolved", preserved.State); Assert.Equal(original.Amount, preserved.Amount); Assert.Equal(original.SnapshotJson, preserved.SnapshotJson);
        if (change == "resolved") Assert.Equal(1000m, Assert.Single(all, x => x.DocumentId == b).Amount);
    }

    private static RoundInput Multi(Guid a, decimal first, Guid b, decimal second)
        => new([Input(first, a).Lines[0], Input(second, b).Lines[0]]);

    private static RoundInput Replacement(Guid source, Guid document, decimal amount) => new([
        Input(amount, document).Lines[0] with { SourceLineId = source, Allocations = amount == 0 ? [] : [new(document, amount)] }]);
    private static async Task<ExpenseAdjustmentService> Adjustments(FieldAdvanceFixture f, TPR10.Api.Data.Tpr10DbContext db)
    {
        var executor = await Executor(f, db, AuthorId);
        return new(new(db, await Access(f, db, AuthorId), executor, new(db, executor), f.Clock));
    }

    private static async Task Approve(FieldAdvanceFixture f, Guid request, Guid round)
    {
        Assert.Equal(200, (await Submit(f, AuthorId, request, round, await ConfirmCommand(f, request, round))).Status);
        foreach (var actor in new[] { ReviewerId, RecipientId, AliasAccountId })
            Assert.Equal(200, (await Review(f, actor, request, round)).Status);
    }
}
