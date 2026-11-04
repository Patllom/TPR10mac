using Microsoft.EntityFrameworkCore;
using Npgsql;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Policies;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Settlements;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementAccessTests;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementReviewTests;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementDeliverySupport;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceSettlementRaceTests(PostgresFixture postgres)
{
    // A partial commit of final approval would consume the document or mint a receipt.
    // Closure has its own transaction: it is tested at its own deferred COMMIT below.
    [Fact]
    public async Task Commit_failure_rolls_back_money_allocation_receipt_and_closure()
    {
        await using var f = await Ready(postgres);
        var (request, round) = await FinalReviewReady(f);
        var command = await ReviewCommand(f, request, round);
        var beforeLedger = await Ledger(f);
        var beforeReceipts = await Rows<FinanceCommandReceipt>(f);
        var beforeClosures = await Rows<ClosureRecord>(f);
        await using var db = f.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION task18_fail_commit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'task18-deferred-commit' USING ERRCODE='23514'; END $$;
            CREATE CONSTRAINT TRIGGER task18_fail_commit AFTER INSERT ON field_advance_command_receipts
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION task18_fail_commit();
            """);
        var error = await Assert.ThrowsAsync<PostgresException>(() => Review(f, AliasAccountId, request, round, command: command));
        Assert.Equal("23514", error.SqlState); Assert.Equal("task18-deferred-commit", error.MessageText);
        var afterFailureLedger = await Ledger(f);
        var afterFailureReceipts = await Rows<FinanceCommandReceipt>(f);
        var afterFailureClosures = await Rows<ClosureRecord>(f);
        Assert.Equal(beforeLedger, afterFailureLedger);
        Assert.Equal(beforeReceipts, afterFailureReceipts);
        Assert.Equal(beforeClosures, afterFailureClosures);
        Assert.Equal(0m, (await FieldAdvanceRefundTests.Totals(f, request)).ApprovedExpense);
        Assert.Equal("Reserved", (await db.Set<ExpenseAllocation>().SingleAsync()).State);
        await db.Database.ExecuteSqlRawAsync("DROP FUNCTION task18_fail_commit() CASCADE");
        var sameCommandAfterRecovery = await Review(f, AliasAccountId, request, round, command: command);
        Assert.Equal(200, sameCommandAfterRecovery.Status);
        Assert.Equal(100m, (await FieldAdvanceRefundTests.Totals(f, request)).ApprovedExpense);
        Assert.Equal("Accepted", (await db.Set<ExpenseAllocation>().AsNoTracking().SingleAsync()).State);
        var afterSuccess = await Ledger(f);
        Assert.Equal(sameCommandAfterRecovery, await Review(f, AliasAccountId, request, round, command: command));
        Assert.Equal(afterSuccess, await Ledger(f));

        await Eligible(f, request);
        var beforeClose = await Ledger(f);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION task18_fail_close() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'task18-close-commit' USING ERRCODE='23514'; END $$;
            CREATE CONSTRAINT TRIGGER task18_fail_close AFTER INSERT ON field_advance_closure_records
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION task18_fail_close();
            """);
        var closeError = await Assert.ThrowsAsync<PostgresException>(() => Closure(f, db).TryCloseAsync(request, default));
        Assert.Equal("task18-close-commit", closeError.MessageText);
        Assert.Equal(beforeClose, await Ledger(f));
        Assert.Empty(await db.Set<ClosureRecord>().ToArrayAsync());
        await db.Database.ExecuteSqlRawAsync("DROP FUNCTION task18_fail_close() CASCADE");
        Assert.True(await Closure(f, db).TryCloseAsync(request, default));
        Assert.False(await Closure(f, db).TryCloseAsync(request, default));
        Assert.Single(await db.Set<ClosureRecord>().ToArrayAsync());
        Assert.Equal("Closed", (await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request)).State);
    }

    // Removing the command/family DB lock would permit both consumers to use old capacity.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refund_and_final_expense_wait_on_postgres_and_recheck_capacity(bool refundFirst)
    {
        await using var f = await Ready(postgres);
        var (request, round) = await FinalReviewReady(f);
        await using var firstDb = f.CreateContext(); await using var secondDb = f.CreateContext();
        await firstDb.Database.OpenConnectionAsync(); await secondDb.Database.OpenConnectionAsync();
        var holder = ((NpgsqlConnection)firstDb.Database.GetDbConnection()).ProcessID;
        var waiter = ((NpgsqlConnection)secondDb.Database.GetDbConnection()).ProcessID;
        Assert.NotEqual(holder, waiter);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstActor = refundFirst ? AuthorId : AliasAccountId;
        var secondActor = refundFirst ? AliasAccountId : AuthorId;
        var audit = new FieldAdvanceRequestRaceTests.CallbackAudit(new TPR10.Api.Auditing.AuditEventWriter(firstDb, new TPR10.Api.Correlation.CorrelationContext(), f.Clock), async () =>
        { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(15)); });
        var firstExecutor = await Executor(f, firstDb, firstActor, audit);
        var secondExecutor = await Executor(f, secondDb, secondActor);
        var reviewMeta = await ReviewCommand(f, request, round); var refundMeta = await Meta(f, request);
        var proof = await firstDb.Set<AdvanceEvidence>().SingleAsync();
        using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        async Task<FinanceResult<SettlementCommandResult>> Execute(Tpr10DbContext context, SettlementCommandExecutor executor, bool refund)
        {
            var actor = refund ? AuthorId : AliasAccountId;
            if (refund) return await new RefundService(context, await Access(f, context, actor), executor, f.Clock, files.Options)
                .ProposeAsync(actor, request, FieldAdvanceRefundTests.RefundInputFor(100m, proof.Id), refundMeta, default);
            var session = await f.SessionAsync(actor);
            var policies = new PolicyResolver(context, FieldAdvanceAccessTests.Access(context, session, f.Clock), session, f.Clock);
            return await new SettlementReviewService(context, await Access(f, context, actor), executor,
                new(context, new(context, f.Clock), f.Clock), new(context, policies, f.Clock), f.Clock)
                .ReviewAsync(actor, request, round, new("Approve", "ตรวจจำลอง"), reviewMeta, default);
        }
        var first = Execute(firstDb, firstExecutor, refundFirst);
        Task<FinanceResult<SettlementCommandResult>>? second = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            second = Execute(secondDb, secondExecutor, !refundFirst);
            await using var probe = f.CreateContext();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!await probe.Database.SqlQuery<bool>($"SELECT {holder} = ANY(pg_blocking_pids({waiter})) AS \"Value\"").SingleAsync(deadline.Token))
            { Assert.False(second.IsCompleted); await Task.Delay(20, deadline.Token); }
        }
        finally { release.TrySetResult(); }
        Assert.Equal(refundFirst ? 201 : 200, (await first).Status);
        Assert.NotNull(second); Assert.Equal(409, (await second).Status);
        // Fresh versions must still fail for capacity, not merely the stale command version.
        var retry = refundFirst ? await Review(f, AliasAccountId, request, round)
            : await FieldAdvanceRefundTests.RefundCall(f, request, "Propose", input: FieldAdvanceRefundTests.RefundInputFor(100m, proof.Id));
        Assert.Equal(409, retry.Status);
        var totals = await FieldAdvanceRefundTests.Totals(f, request);
        Assert.Equal(refundFirst ? 0m : 100m, totals.ApprovedExpense);
        Assert.Equal(refundFirst ? 100m : 0m, totals.PendingRefund);
        Assert.Equal(0m, totals.ConfirmedRefund);
    }

    internal static async Task<(Guid Request, Guid Round)> FinalReviewReady(FieldAdvanceFixture f)
    {
        var s = await Draft(f);
        await FieldAdvanceRefundTests.SeedPaid(f, s.Request, 100m);
        await using (var db = f.CreateContext())
        {
            var account = await db.Set<AdvancePaymentAccount>().SingleAsync();
            account.Spendable = account.Paid = 100m; account.EverPaid = true;
            (await db.Set<AdvanceInstallment>().SingleAsync()).State = "Paid";
            (await db.Set<AdvancePaymentAttempt>().SingleAsync()).State = "Matched";
            await db.SaveChangesAsync();
        }
        Assert.Equal(200, (await Submit(f, AuthorId, s.Request, s.Round, await ConfirmCommand(f, s.Request, s.Round))).Status);
        foreach (var actor in new[] { ReviewerId, RecipientId }) Assert.Equal(200, (await Review(f, actor, s.Request, s.Round)).Status);
        return s;
    }
}
