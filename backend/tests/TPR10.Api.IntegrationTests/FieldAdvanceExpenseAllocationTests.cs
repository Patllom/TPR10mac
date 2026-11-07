using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Settlements;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementAccessTests;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementDraftTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceExpenseAllocationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Pending_and_returned_rounds_share_one_document_budget()
    {
        await using var f = await Ready(postgres); var request = await Approved(f); var doc = await Document(f, request);
        var first = await Save(f, request, null, Input(700, doc)); Assert.Equal(201, first.Status);
        var second = await Save(f, request, null, Input(400, doc)); Assert.Equal(201, second.Status);
        Assert.Equal(200, (await Reserve(f, request, first.Value!.EntityId!.Value, [new(doc, 700)])).Status);
        await Return(f, first.Value.EntityId.Value);
        var competingReservation = await Reserve(f, request, second.Value!.EntityId!.Value, [new(doc, 400)]);
        Assert.Equal(409, competingReservation.Status);
        await using var db = f.CreateContext();
        var returnedRoundReserved = await db.Set<ExpenseAllocation>().Where(x => x.DocumentId == doc && x.State == "Reserved").SumAsync(x => x.Amount);
        var documentRemaining = 1000m - returnedRoundReserved;
        Assert.Equal(700m, returnedRoundReserved);
        Assert.Equal(300m, documentRemaining);
    }

    [Fact]
    public async Task Returned_edit_releases_only_reduction_and_increase_waits_for_submission()
    {
        await using var f = await Ready(postgres); var request = await Approved(f); var doc = await Document(f, request);
        var first = await Save(f, request, null, Input(700, doc)); Assert.Equal(201, first.Status); var round = first.Value!.EntityId!.Value;
        Assert.Equal(200, (await Reserve(f, request, round, [new(doc, 700)])).Status); await Return(f, round);
        await using var db = f.CreateContext(); var prior = await db.Set<SettlementRevision>().SingleAsync(); var snapshot = prior.SnapshotJson;
        Assert.Equal(200, (await Save(f, request, round, Input(500, doc))).Status);
        Assert.Equal(500m, await Reserved(db, doc));
        Assert.Equal(200, (await Save(f, request, round, Input(800, doc))).Status);
        Assert.Equal(500m, await Reserved(db, doc));
        var second = await Save(f, request, null, Input(400, doc));
        Assert.Equal(200, (await Reserve(f, request, second.Value!.EntityId!.Value, [new(doc, 400)])).Status);
        Assert.Equal(409, (await Reserve(f, request, round, [new(doc, 800)])).Status);
        Assert.Equal(900m, await Reserved(db, doc));
        Assert.Equal(snapshot, (await db.Set<SettlementRevision>().AsNoTracking().SingleAsync(x => x.Id == prior.Id)).SnapshotJson);
        Assert.Equal(200, (await Save(f, request, round, new([]))).Status);
        Assert.Equal(400m, await Reserved(db, doc));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("not-ready")]
    [InlineData("foreign")]
    [InlineData("mismatch")]
    public async Task Incomplete_or_unusable_evidence_cannot_reserve(string mode)
    {
        await using var f = await Ready(postgres); var request = await Approved(f); var doc = await Document(f, request);
        var saved = await Save(f, request, null, Input(100, mode == "missing" ? null : doc)); Assert.Equal(201, saved.Status);
        if (mode == "not-ready")
        { await using var db = f.CreateContext(); await db.Set<AdvanceEvidence>().ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Pending")); }
        if (mode == "foreign") doc = await Document(f, await Approved(f));
        var result = await Reserve(f, request, saved.Value!.EntityId!.Value, mode == "missing" ? [] : [new(doc, mode == "mismatch" ? 99 : 100)]);
        Assert.Equal(409, result.Status);
        await using var verify = f.CreateContext(); Assert.Equal(0m, await Reserved(verify, doc));
    }

    [Fact]
    public async Task Ledger_cannot_run_outside_executor_even_in_arbitrary_transaction()
    {
        await using var f = await Ready(postgres); await using var db = f.CreateContext(); var executor = await Executor(f, db, AuthorId);
        var ledger = new ExpenseAllocationLedger(db, executor);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), [], default));
        await using var tx = await db.Database.BeginTransactionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), [], default));
    }

    [Fact]
    public async Task Concurrent_connections_wait_then_enforce_budget_with_fresh_request_versions()
    {
        await using var f = await Ready(postgres); var request = await Approved(f); var doc = await Document(f, request);
        var a = await Save(f, request, null, Input(700, doc)); var b = await Save(f, request, null, Input(400, doc));
        await using var firstDb = f.CreateContext(); await using var secondDb = f.CreateContext();
        var firstExecutor = await Executor(f, firstDb, AuthorId); var secondExecutor = await Executor(f, secondDb, AuthorId);
        await secondDb.Database.OpenConnectionAsync(); var pid = ((Npgsql.NpgsqlConnection)secondDb.Database.GetDbConnection()).ProcessID;
        var meta = await Meta(f, request, 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = ReserveIn(f, firstDb, firstExecutor, request, a.Value!.EntityId!.Value, [new(doc, 700)], meta, async () =>
        { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(15)); });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var second = ReserveIn(f, secondDb, secondExecutor, request, b.Value!.EntityId!.Value, [new(doc, 400)],
            meta with { CommandId = Guid.NewGuid(), RequestVersion = meta.RequestVersion + 1, FamilyVersion = meta.FamilyVersion + 1 });
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await using var probe = f.CreateContext();
            while (await probe.Database.SqlQuery<int>($"SELECT cardinality(pg_blocking_pids({pid})) AS \"Value\"").SingleAsync(deadline.Token) == 0)
            { Assert.False(second.IsCompleted); await Task.Delay(20, deadline.Token); }
        }
        finally { release.TrySetResult(); }
        Assert.Equal(200, (await first).Status);
        var denied = await second.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(409, denied.Status); Assert.Equal("document-budget-exceeded", denied.Code);
        await using var verify = f.CreateContext(); Assert.Equal(700m, await Reserved(verify, doc));
    }

    [Fact]
    public async Task Accepted_allocations_count_once_and_accepted_round_cannot_be_reduced()
    {
        await using var f = await Ready(postgres); var request = await Approved(f); var doc = await Document(f, request);
        var first = await Save(f, request, null, Input(700, doc)); var round = first.Value!.EntityId!.Value;
        Assert.Equal(200, (await Reserve(f, request, round, [new(doc, 700)])).Status);
        await using (var db = f.CreateContext())
        {
            var revision = await db.Set<SettlementRevision>().SingleAsync();
            // Both facts carry accepted timestamps: the ledger must still count only 700, not 1400.
            var allocation = await db.Set<ExpenseAllocation>().SingleAsync();
            allocation.State = "Accepted"; allocation.AcceptedAtUtc = f.Clock.GetUtcNow();
            allocation.ReviewedBy = ReviewerId; allocation.ReviewedByPersonId = ReviewerPersonId; allocation.ReviewedAtUtc = f.Clock.GetUtcNow();
            await db.SaveChangesAsync();
            revision.State = "Accepted"; revision.AcceptedAtUtc = f.Clock.GetUtcNow();
            revision.ReviewedBy = ReviewerId; revision.ReviewedByPersonId = ReviewerPersonId; revision.ReviewedAtUtc = f.Clock.GetUtcNow();
            var r = await db.Set<SettlementRound>().SingleAsync(); r.State = "Accepted"; r.AcceptedAtUtc = f.Clock.GetUtcNow();
            r.ReviewedBy = ReviewerId; r.ReviewedByPersonId = ReviewerPersonId; r.ReviewedAtUtc = f.Clock.GetUtcNow(); await db.SaveChangesAsync();
        }
        Assert.Equal(409, (await Save(f, request, round, Input(100, doc))).Status);
        var second = await Save(f, request, null, Input(300, doc));
        Assert.Equal(200, (await Reserve(f, request, second.Value!.EntityId!.Value, [new(doc, 300)])).Status);
        var third = await Save(f, request, null, Input(1, doc));
        Assert.Equal("document-budget-exceeded", (await Reserve(f, request, third.Value!.EntityId!.Value, [new(doc, 1)])).Code);
    }

    [Fact]
    public async Task One_document_can_cover_multiple_lines_and_resubmission_does_not_double_count()
    {
        await using var f = await Ready(postgres); var request = await Approved(f); var doc = await Document(f, request);
        var saved = await Save(f, request, null, new([Input(300, doc).Lines[0], Input(400, doc).Lines[0]])); var round = saved.Value!.EntityId!.Value;
        Assert.Equal(200, (await Reserve(f, request, round, [new(doc, 300), new(doc, 400)])).Status);
        await Return(f, round);
        Assert.Equal(200, (await Save(f, request, round, new([Input(100, doc).Lines[0], Input(500, doc).Lines[0]]))).Status);
        await using var db = f.CreateContext(); Assert.Equal(600m, await Reserved(db, doc));
        Assert.Equal(200, (await Reserve(f, request, round, [new(doc, 600)])).Status);
        Assert.Equal(600m, await Reserved(db, doc));
        Assert.Equal(700m, await db.Set<ExpenseAllocation>().Where(x => x.State == "Released").SumAsync(x => x.Amount));
    }

    [Fact]
    public async Task Failed_executor_body_rolls_back_reservation_revision_and_receipt()
    {
        await using var f = await Ready(postgres); var request = await Approved(f); var doc = await Document(f, request);
        var saved = await Save(f, request, null, Input(700, doc)); var round = saved.Value!.EntityId!.Value;
        var command = await Meta(f, request, 1);
        await using var db = f.CreateContext(); var executor = await Executor(f, db, AuthorId);
        var result = await ReserveIn(f, db, executor, request, round, [new(doc, 700)], command, failAfterReserve: true);
        Assert.Equal(409, result.Status); Assert.Equal("test-rollback", result.Code);
        Assert.Equal(0m, await Reserved(db, doc)); Assert.Null((await db.Set<SettlementRevision>().SingleAsync()).SubmittedAtUtc);
        Assert.False(await db.Set<FinanceCommandReceipt>().AnyAsync(x => x.CommandId == command.CommandId));
        Assert.Equal(command.RequestVersion, (await Meta(f, request)).RequestVersion);
    }

    [Fact]
    public async Task Duplicate_status_is_version_bound_on_both_sides_and_checked_inside_reservation()
    {
        await using var f = await Ready(postgres); var request = await Approved(f); var otherRequest = await Approved(f);
        var doc = await Document(f, request); var otherDoc = await Document(f, otherRequest);
        var a = await Save(f, request, null, Input(100, doc)); var b = await Save(f, otherRequest, null, Input(100, otherDoc));
        Guid pairId;
        await using (var db = f.CreateContext())
        {
            var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request);
            var other = await db.Set<ExpenseDocument>().SingleAsync(x => x.Id == otherDoc);
            var pair = await FieldAdvanceSettlementCommandTests.Row<ExpenseDuplicateCase>(f, db, r);
            pair.DocumentId = doc; pair.DocumentVersion = 1; pair.OtherDocumentId = otherDoc; pair.OtherDocumentVersion = 1;
            pair.OtherRequestId = otherRequest; pair.OtherFamilyId = other.FamilyId; pair.OtherProjectId = other.ProjectId; pair.OtherWorkspaceId = other.WorkspaceId;
            pair.State = "Pending"; db.Add(pair); await db.SaveChangesAsync(); pairId = pair.Id;
        }
        Assert.Equal(409, (await Reserve(f, request, a.Value!.EntityId!.Value, [new(doc, 100)])).Status);
        Assert.Equal(409, (await Reserve(f, otherRequest, b.Value!.EntityId!.Value, [new(otherDoc, 100)])).Status);
        await using (var db = f.CreateContext())
        {
            (await db.Set<ExpenseDuplicateCase>().SingleAsync(x => x.Id == pairId)).State = "DistinctDocument";
            (await db.Set<ExpenseDocument>().SingleAsync(x => x.Id == otherDoc)).Version++;
            await db.SaveChangesAsync();
        }
        Assert.Equal(409, (await Reserve(f, request, a.Value.EntityId.Value, [new(doc, 100)])).Status);
        await using (var db = f.CreateContext())
        {
            var pair = await db.Set<ExpenseDuplicateCase>().SingleAsync(x => x.Id == pairId); pair.OtherDocumentVersion = 2;
            await db.SaveChangesAsync();
        }
        Assert.Equal(200, (await Reserve(f, request, a.Value.EntityId.Value, [new(doc, 100)])).Status);
        Assert.Equal(200, (await Reserve(f, otherRequest, b.Value.EntityId.Value, [new(otherDoc, 100)])).Status);
    }

    [Fact]
    public async Task Old_revision_command_cannot_reserve_new_draft()
    {
        await using var f = await Ready(postgres); var request = await Approved(f); var doc = await Document(f, request);
        var first = await Save(f, request, null, Input(100, doc)); var round = first.Value!.EntityId!.Value;
        await using var db = f.CreateContext(); var original = await db.Set<SettlementRevision>().AsNoTracking().SingleAsync();
        Assert.Equal(200, (await Save(f, request, round, Input(200, doc))).Status);
        var executor = await Executor(f, db, AuthorId); var command = await Meta(f, request, original.Version);
        var result = await executor.ExecuteAsync(AuthorId, request, SettlementActions.Confirm, original.Id, command,
            SettlementCommandExecutor.Hash(new { operation = "test-stale-revision", round }), async ct =>
            {
                var decision = await new ExpenseAllocationLedger(db, executor).ReserveAsync(request, round, [new(doc, 200)], ct);
                return new(decision.Allowed ? 200 : 409, decision.Code, decision.Allowed ? new(request, original.Id, "Draft", 0, 0, 1) : null);
            }, default);
        Assert.Equal(409, result.Status); Assert.Equal(0m, await Reserved(db, doc));
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("certificate")]
    [InlineData("ใบรับรองค่าใช้จ่าย")]
    [InlineData(" certificate ")]
    public async Task Explicit_kind_can_match_any_approved_category_alternative(string kind)
    {
        await using var f = await Ready(postgres);
        var request = await Approved(f, evidenceTypes: ["receipt", " certificate ", "ใบรับรองค่าใช้จ่าย"]);
        var doc = await Document(f, request, evidenceType: kind);
        var saved = await Save(f, request, null, Input(100, doc)); Assert.Equal(201, saved.Status);
        Assert.Equal(200, (await Reserve(f, request, saved.Value!.EntityId!.Value, [new(doc, 100)])).Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-kind")]
    [InlineData("Receipt")]
    public async Task Missing_or_wrong_explicit_kind_cannot_be_inferred_from_ready_image(string? kind)
    {
        await using var f = await Ready(postgres); var request = await Approved(f);
        var doc = await Document(f, request, evidenceType: kind);
        var saved = await Save(f, request, null, Input(100, doc)); Assert.Equal(201, saved.Status);
        Assert.Equal(409, (await Reserve(f, request, saved.Value!.EntityId!.Value, [new(doc, 100)])).Status);
        await using var db = f.CreateContext(); Assert.Equal(0m, await Reserved(db, doc));
    }

    [Fact]
    public async Task Same_document_must_match_each_funded_category_and_cover_the_entire_line()
    {
        await using var f = await Ready(postgres);
        var request = await Approved(f, evidenceTypes: ["receipt"], fixedEvidenceTypes: ["certificate"]);
        var doc = await Document(f, request, evidenceType: "receipt");
        var input = new RoundInput([Input(50, doc).Lines[0], new(null, FixedCategory, "คนละหมวด", new(2026, 10, 30), 1m, null, null, [new(doc, 50)])]);
        var saved = await Save(f, request, null, input);
        Assert.Equal(409, (await Reserve(f, request, saved.Value!.EntityId!.Value, [new(doc, 100)])).Status);
        var partial = await Save(f, request, null, new([Input(100, doc).Lines[0] with { Allocations = [new(doc, 50)] }]));
        Assert.Equal(409, (await Reserve(f, request, partial.Value!.EntityId!.Value, [new(doc, 50)])).Status);
        await using var db = f.CreateContext(); Assert.Equal(0m, await Reserved(db, doc));
    }

    internal static Task<decimal> Reserved(Tpr10DbContext db, Guid doc) => db.Set<ExpenseAllocation>().Where(x => x.DocumentId == doc && x.State == "Reserved").SumAsync(x => x.Amount);
    internal static async Task Return(FieldAdvanceFixture f, Guid round)
    {
        await using var db = f.CreateContext(); var r = await db.Set<SettlementRound>().SingleAsync(x => x.Id == round); r.State = "Returned";
        var rev = await db.Set<SettlementRevision>().Where(x => x.RoundId == round).OrderByDescending(x => x.RevisionNumber).FirstAsync(); rev.State = "Returned";
        await db.SaveChangesAsync();
    }
    internal static async Task<FinanceResult<SettlementCommandResult>> Reserve(FieldAdvanceFixture f, Guid request, Guid round, EvidenceAllocation[] allocations)
    {
        await using var db = f.CreateContext(); var executor = await Executor(f, db, AuthorId);
        return await ReserveIn(f, db, executor, request, round, allocations);
    }
    internal static async Task<FinanceResult<SettlementCommandResult>> ReserveIn(FieldAdvanceFixture f, Tpr10DbContext db,
        SettlementCommandExecutor executor, Guid request, Guid round, EvidenceAllocation[] allocations,
        SettlementCommand? meta = null, Func<Task>? beforeReserve = null, bool failAfterReserve = false)
    {
        var revision = await db.Set<SettlementRevision>().AsNoTracking().Where(x => x.RoundId == round).OrderByDescending(x => x.RevisionNumber).FirstAsync();
        var command = meta ?? await Meta(f, request, revision.Version);
        return await executor.ExecuteAsync(AuthorId, request, SettlementActions.Confirm, revision.Id, command,
            SettlementCommandExecutor.Hash(new { operation = "test-reserve", round, allocations }), async ct =>
            {
                if (beforeReserve is not null) await beforeReserve();
                var decision = await new ExpenseAllocationLedger(db, executor).ReserveAsync(request, round, allocations, ct);
                if (!decision.Allowed) return new(409, decision.Code, null);
                var current = await db.Set<SettlementRevision>().SingleAsync(x => x.Id == revision.Id, ct);
                current.SubmittedAtUtc = f.Clock.GetUtcNow(); current.State = "Pending";
                (await db.Set<SettlementRound>().SingleAsync(x => x.Id == round, ct)).State = "Pending";
                if (failAfterReserve) return new(409, "test-rollback", null);
                return new(200, "reserved", new(request, revision.Id, "Pending", 0, 0, revision.Version));
            }, default);
    }
}
