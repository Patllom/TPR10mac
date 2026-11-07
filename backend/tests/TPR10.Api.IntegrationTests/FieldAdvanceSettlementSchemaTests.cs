using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceSettlementSchemaTests(PostgresFixture postgres)
{
    private const string PreviousMigration = "20261024075718_AddFieldAdvancePayments";
    private static readonly string[] Tables = ["settlement_rounds", "settlement_revisions", "settlement_lines", "settlement_approval_steps",
        "expense_documents", "expense_allocations", "expense_duplicate_cases", "work_completions", "submission_declarations",
        "refunds", "refund_corrections", "settlement_expense_adjustments", "settlement_adjustment_cases", "payment_incident_resolutions",
        "payment_incident_recoveries", "closure_records"];

    [Fact]
    public async Task Settlement_tables_exist_and_match_the_model()
    {
        await using var f = await FieldAdvancePaymentFixture.CreateAsync(postgres);
        await using var db = f.CreateContext();
        var tables = await db.Database.SqlQueryRaw<string>("""SELECT tablename AS "Value" FROM pg_tables WHERE schemaname='public'""").ToArrayAsync();
        foreach (var table in Tables) Assert.Contains("field_advance_" + table, tables);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("0.001")]
    [InlineData("10000000000000000")]
    [InlineData("'NaN'")]
    [InlineData("'Infinity'")]
    public async Task Stored_money_rejects_invalid_values_without_rounding(string value)
    {
        await using var f = await FieldAdvancePaymentFixture.CreateAsync(postgres);
        var request = await f.SeedApprovedAsync(10000m, default);
        await using var db = f.CreateContext();
        await SeedRefund(db, request);
        // Fixed theory literals only; no client input is interpolated into SQL.
        await Reject(db, "UPDATE field_advance_refunds SET amount=" + value, "23514");
    }

    [Fact]
    public async Task Accepted_refund_and_signed_adjustment_are_immutable_and_applied_once()
    {
        await using var f = await FieldAdvancePaymentFixture.CreateAsync(postgres);
        var request = await f.SeedApprovedAsync(10000m, default);
        await using var db = f.CreateContext();
        var refund = await SeedRefund(db, request);
        Accept(refund); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        foreach (var sql in new[] { "UPDATE field_advance_refunds SET amount=1500", "UPDATE field_advance_refunds SET accepted_at_utc=NULL,state='Draft'", "DELETE FROM field_advance_refunds", "TRUNCATE field_advance_refunds CASCADE" })
            await Reject(db, sql, "23514");
        var correction = await Row<RefundCorrection>(db, request);
        correction.RefundId = refund.Id; correction.EvidenceId = refund.EvidenceId;
        correction.BaseVersion = 1; correction.BeforeAmount = 2000m; correction.AfterAmount = 1500m; correction.Delta = -500m;
        Accept(correction); db.Add(correction); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        Assert.Equal(2000m, (await db.Set<Refund>().SingleAsync()).Amount);
        Assert.Equal(-500m, (await db.Set<RefundCorrection>().SingleAsync()).Delta);
        foreach (var sql in new[] { "UPDATE field_advance_refund_corrections SET delta=-400,after_amount=1600", "DELETE FROM field_advance_refund_corrections", "TRUNCATE field_advance_refund_corrections CASCADE" })
            await Reject(db, sql, "23514");
        var duplicate = await Row<RefundCorrection>(db, request);
        duplicate.RefundId = refund.Id; duplicate.EvidenceId = refund.EvidenceId; duplicate.BaseVersion = 1;
        duplicate.BeforeAmount = 2000m; duplicate.AfterAmount = 1500m; duplicate.Delta = -500m; Accept(duplicate);
        db.Add(duplicate); await RejectSave(db, "23505");
        duplicate.BaseVersion = 2; duplicate.BeforeAmount = 1500m; duplicate.AfterAmount = -1m; duplicate.Delta = -1501m;
        db.Add(duplicate); await RejectSave(db, "23514");
    }

    [Fact]
    public async Task Scoped_references_and_command_uniqueness_prevent_cross_request_reuse()
    {
        await using var f = await FieldAdvancePaymentFixture.CreateAsync(postgres);
        var first = await f.SeedApprovedAsync(10000m, default); var second = await f.SeedApprovedAsync(10000m, default);
        await using var db = f.CreateContext();
        var refund = await SeedRefund(db, first);
        var other = await Row<Refund>(db, second);
        other.Amount = 100m; other.Channel = "Cash"; other.RefundedOn = new(2026, 10, 31); other.EvidenceId = refund.EvidenceId;
        db.Add(other); await RejectSave(db, "23503");
        var copy = await Row<Refund>(db, first);
        copy.CommandId = refund.CommandId; copy.Amount = 100m; copy.Channel = "Cash"; copy.RefundedOn = new(2026, 10, 31); copy.EvidenceId = refund.EvidenceId;
        db.Add(copy); await RejectSave(db, "23505");
    }

    [Fact]
    public async Task Accepted_revision_freezes_lines_and_allocations_and_cannot_accept_twice()
    {
        await using var f = await FieldAdvancePaymentFixture.CreateAsync(postgres);
        var request = await f.SeedApprovedAsync(10000m, default);
        await using var db = f.CreateContext();
        var refund = await SeedRefund(db, request);
        var round = await Row<SettlementRound>(db, request); db.Add(round); await db.SaveChangesAsync();
        var revision = await Row<SettlementRevision>(db, request); revision.RoundId = round.Id; revision.RevisionNumber = 1; revision.Total = 1.01m; db.Add(revision); await db.SaveChangesAsync();
        var line = await Row<SettlementLine>(db, request); line.RevisionId = revision.Id; line.CategoryId = Guid.NewGuid(); line.Description = "test"; line.IncurredOn = new(2026, 10, 31); line.Quantity = 0.5m; line.Rate = 2.01m; line.Amount = 1.01m; db.Add(line); await db.SaveChangesAsync();
        await Reject(db, "UPDATE field_advance_settlement_lines SET quantity=0.00001", "23514");
        await Reject(db, "UPDATE field_advance_settlement_lines SET rate=2.011", "23514");
        await Reject(db, "UPDATE field_advance_settlement_lines SET amount=1.00", "23514");
        var document = await Row<ExpenseDocument>(db, request); document.EvidenceId = refund.EvidenceId; document.Issuer = "test"; document.HasNoNumber = true; document.IssuedOn = new(2026, 10, 31); document.Amount = 100m; db.Add(document); await db.SaveChangesAsync();
        var allocation = await Row<ExpenseAllocation>(db, request); allocation.DocumentId = document.Id; allocation.LineId = line.Id; allocation.RevisionId = revision.Id; allocation.Amount = 1.01m; db.Add(allocation); await db.SaveChangesAsync();
        Accept(revision); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        foreach (var sql in new[] { "UPDATE field_advance_settlement_revisions SET total=2", "DELETE FROM field_advance_settlement_revisions", "UPDATE field_advance_settlement_lines SET amount=2,lump_sum=2,quantity=NULL,rate=NULL", "DELETE FROM field_advance_settlement_lines", "UPDATE field_advance_expense_allocations SET amount=0", "DELETE FROM field_advance_expense_allocations", "UPDATE field_advance_expense_documents SET amount=50" })
            await Reject(db, sql, "23514");
        var next = await Row<SettlementRevision>(db, request); next.RoundId = round.Id; next.RevisionNumber = 2; Accept(next); db.Add(next); await RejectSave(db, "23505");
        var extra = await Row<SettlementLine>(db, request); extra.RevisionId = revision.Id; extra.CategoryId = Guid.NewGuid(); extra.IncurredOn = new(2026, 10, 31); extra.LumpSum = 1m; extra.Amount = 1m; db.Add(extra); await RejectSave(db, "23514");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Document_mutation_waits_for_acceptance_and_rechecks_committed_history(bool acceptAllocation)
    {
        await using var f = await FieldAdvancePaymentFixture.CreateAsync(postgres);
        var request = await f.SeedApprovedAsync(10000m, default);
        await using var db = f.CreateContext();
        var refund = await SeedRefund(db, request);
        var round = await Row<SettlementRound>(db, request); db.Add(round); await db.SaveChangesAsync();
        var revision = await Row<SettlementRevision>(db, request);
        revision.RoundId = round.Id; revision.RevisionNumber = 1; revision.Total = 1m;
        db.Add(revision); await db.SaveChangesAsync();
        var line = await Row<SettlementLine>(db, request);
        line.RevisionId = revision.Id; line.CategoryId = Guid.NewGuid(); line.IncurredOn = new(2026, 10, 31);
        line.LumpSum = 1m; line.Amount = 1m; db.Add(line); await db.SaveChangesAsync();
        var document = await Row<ExpenseDocument>(db, request);
        document.EvidenceId = refund.EvidenceId; document.Issuer = "race fixture"; document.HasNoNumber = true;
        document.IssuedOn = new(2026, 10, 31); document.Amount = 100m; db.Add(document); await db.SaveChangesAsync();
        var allocation = await Row<ExpenseAllocation>(db, request);
        allocation.DocumentId = document.Id; allocation.LineId = line.Id; allocation.RevisionId = revision.Id;
        allocation.Amount = 1m; db.Add(allocation); await db.SaveChangesAsync();

        await using var accepting = new NpgsqlConnection(db.Database.GetConnectionString());
        await using var mutating = new NpgsqlConnection(db.Database.GetConnectionString());
        await accepting.OpenAsync(); await mutating.OpenAsync();
        await using var transaction = await accepting.BeginTransactionAsync();
        // A has accepted using the original evidence, but has NOT committed. Independent
        // allocation acceptance is supported by the schema and locks its parent revision.
        var table = acceptAllocation ? "field_advance_expense_allocations" : "field_advance_settlement_revisions";
        await using (var accept = new NpgsqlCommand($"UPDATE {table} SET state='Accepted', accepted_at_utc=now(), reviewed_at_utc=now(), reviewed_by=@account, reviewed_by_person_id=@person WHERE id=@id", accepting, transaction))
        {
            accept.Parameters.AddWithValue("account", ReviewerId);
            accept.Parameters.AddWithValue("person", ReviewerPersonId);
            accept.Parameters.AddWithValue("id", acceptAllocation ? allocation.Id : revision.Id);
            Assert.Equal(1, await accept.ExecuteNonQueryAsync());
        }
        await using var mutate = new NpgsqlCommand("UPDATE field_advance_expense_documents SET amount=50 WHERE id=@id", mutating);
        mutate.Parameters.AddWithValue("id", document.Id);
        mutate.CommandTimeout = 20;
        var mutation = mutate.ExecuteNonQueryAsync();
        var committed = false;
        try
        {
            var blocked = false;
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(10))
            {
                await using var barrier = new NpgsqlCommand("SELECT @holder = ANY(pg_blocking_pids(@waiter))", accepting, transaction);
                barrier.Parameters.AddWithValue("holder", accepting.ProcessID);
                barrier.Parameters.AddWithValue("waiter", mutating.ProcessID);
                blocked = (bool)(await barrier.ExecuteScalarAsync())!;
                if (blocked || mutation.IsCompleted) break;
                await Task.Delay(10);
            }
            Assert.True(blocked, "Document mutation must reach a PostgreSQL lock wait on the still-uncommitted acceptance transaction.");
            await transaction.CommitAsync();
            committed = true;
            var rejected = await Assert.ThrowsAsync<PostgresException>(async () => await mutation);
            Assert.Equal("23514", rejected.SqlState);
        }
        finally
        {
            // Release A even on RED, then observe B; never leave a blocked backend behind.
            if (!committed) await transaction.RollbackAsync();
            try { await mutation; } catch (PostgresException) { }
        }
        db.ChangeTracker.Clear();
        Assert.Equal(100m, (await db.Set<ExpenseDocument>().SingleAsync(x => x.Id == document.Id)).Amount);
        if (acceptAllocation)
        {
            Assert.NotNull((await db.Set<ExpenseAllocation>().SingleAsync(x => x.Id == allocation.Id)).AcceptedAtUtc);
            Assert.Null((await db.Set<SettlementRevision>().SingleAsync(x => x.Id == revision.Id)).AcceptedAtUtc);
        }
        else
        {
            Assert.NotNull((await db.Set<SettlementRevision>().SingleAsync(x => x.Id == revision.Id)).AcceptedAtUtc);
            Assert.Null((await db.Set<ExpenseAllocation>().SingleAsync(x => x.Id == allocation.Id)).AcceptedAtUtc);
        }
    }

    [Fact]
    public async Task Closure_is_unique_and_immutable()
    {
        await using var f = await FieldAdvancePaymentFixture.CreateAsync(postgres);
        var request = await f.SeedApprovedAsync(10000m, default);
        await using var db = f.CreateContext();
        var completion = await Row<WorkCompletion>(db, request); completion.CompletedOn = new(2026, 10, 31); completion.ClearingDays = 7; completion.ClearingDueOn = new(2026, 11, 7); completion.RefundDueOn = completion.ClearingDueOn; Accept(completion); db.Add(completion); await db.SaveChangesAsync();
        var declaration = await Row<SubmissionDeclaration>(db, request); declaration.WorkCompletionId = completion.Id; Accept(declaration); db.Add(declaration); await db.SaveChangesAsync();
        var closure = await Row<ClosureRecord>(db, request); closure.WorkCompletionId = completion.Id; closure.DeclarationId = declaration.Id; closure.RequestVersion = 1; closure.FamilyVersion = 1; Accept(closure); db.Add(closure); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        foreach (var sql in new[] { "UPDATE field_advance_closure_records SET version=version+1", "DELETE FROM field_advance_closure_records", "TRUNCATE field_advance_closure_records CASCADE" }) await Reject(db, sql, "23514");
        var duplicate = await Row<ClosureRecord>(db, request); duplicate.WorkCompletionId = completion.Id; duplicate.DeclarationId = declaration.Id; duplicate.RequestVersion = 1; duplicate.FamilyVersion = 1; Accept(duplicate); db.Add(duplicate); await RejectSave(db, "23505");
    }

    [Fact]
    public async Task Migration_from_7C_is_repeatable_without_money_or_grants()
    {
        await using var f = await FieldAdvancePaymentFixture.CreateAsync(postgres);
        await using var db = f.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        var request = await f.SeedApprovedAsync(10000m, default, legacyPaymentAccount: true);
        var before = await db.Set<AdvancePaymentAccount>().AsNoTracking().Where(x => x.RequestId == request)
            .Select(x => new { x.Approved, x.Spendable, x.Paid, x.Reserved, x.Version }).SingleAsync();
        var grants = await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM field_advance_grant_assignments""").SingleAsync();
        var script = db.GetService<IMigrator>().GenerateScript(PreviousMigration, options: MigrationsSqlGenerationOptions.Idempotent);
        await using (var connection = new NpgsqlConnection(db.Database.GetConnectionString()))
        {
            await connection.OpenAsync();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await using var command = new NpgsqlCommand(script, connection);
                await command.ExecuteNonQueryAsync();
            }
        }
        await db.Database.MigrateAsync();
        foreach (var table in Tables)
        {
            // Identifiers come exclusively from the fixed table allowlist above.
            var sql = "SELECT count(*)::int AS \"Value\" FROM field_advance_" + table;
            Assert.Equal(0, await db.Database.SqlQueryRaw<int>(sql).SingleAsync());
        }
        var after = await db.Set<AdvancePaymentAccount>().AsNoTracking().SingleAsync(x => x.RequestId == request);
        Assert.Equal(before.Approved, after.Approved); Assert.Equal(before.Spendable, after.Spendable); Assert.Equal(before.Paid, after.Paid); Assert.Equal(before.Reserved, after.Reserved); Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.Paid, after.EffectiveUsage); Assert.Equal(0m, after.ActivatedShortfall); Assert.Equal(0m, after.IncidentReservationUsage);
        Assert.Empty(await db.Set<AdvancePaymentResult>().ToArrayAsync());
        Assert.Equal(grants, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM field_advance_grant_assignments""").SingleAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Expense_adjustments_keep_signed_deltas_and_unique_accepted_applications()
    {
        await using var f = await FieldAdvancePaymentFixture.CreateAsync(postgres);
        var request = await f.SeedApprovedAsync(10000m, default);
        await using var db = f.CreateContext();
        var originalRound = await Row<SettlementRound>(db, request); db.Add(originalRound);
        var adjustmentRound = await Row<SettlementRound>(db, request); adjustmentRound.SourceRoundId = originalRound.Id; adjustmentRound.RoundKind = "Adjustment"; db.Add(adjustmentRound); await db.SaveChangesAsync();
        var original = await Row<SettlementRevision>(db, request); original.RoundId = originalRound.Id; original.RevisionNumber = 1; original.Total = 8000m; Accept(original); db.Add(original);
        var adjusted = await Row<SettlementRevision>(db, request); adjusted.RoundId = adjustmentRound.Id; adjusted.SourceRevisionId = original.Id; adjusted.RevisionNumber = 1; adjusted.Total = 7500m; db.Add(adjusted); await db.SaveChangesAsync();
        var delta = await Row<SettlementExpenseAdjustment>(db, request); delta.SourceRevisionId = original.Id; delta.RevisionId = adjusted.Id; delta.BaseVersion = 1; delta.BeforeAmount = 8000m; delta.AfterAmount = 7500m; delta.Delta = -500m; db.Add(delta); await db.SaveChangesAsync();
        await Reject(db, "UPDATE field_advance_settlement_expense_adjustments SET delta=-501", "23514");
        await Reject(db, "UPDATE field_advance_settlement_expense_adjustments SET after_amount=-1,delta=-8001", "23514");
        Accept(delta); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        foreach (var sql in new[] { "UPDATE field_advance_settlement_expense_adjustments SET after_amount=7600,delta=-400", "DELETE FROM field_advance_settlement_expense_adjustments" }) await Reject(db, sql, "23514");
        var duplicate = await Row<SettlementExpenseAdjustment>(db, request); duplicate.SourceRevisionId = original.Id; duplicate.RevisionId = adjusted.Id; duplicate.BaseVersion = 1; duplicate.BeforeAmount = 8000m; duplicate.AfterAmount = 7500m; duplicate.Delta = -500m; Accept(duplicate); db.Add(duplicate); await RejectSave(db, "23505");
        Assert.Equal(8000m, (await db.Set<SettlementRevision>().SingleAsync(x => x.Id == original.Id)).Total);
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(PreviousMigration))).SqlState);
    }

    [Fact]
    public async Task Incident_facts_are_attempt_scoped_unique_and_do_not_change_payment_capacity()
    {
        await using var f = await FieldAdvancePaymentFixture.CreateAsync(postgres);
        var request = await f.SeedApprovedAsync(10000m, default);
        var (installment, revision) = await f.SeedInstallmentAsync(request);
        await using var db = f.CreateContext();
        var refund = await SeedRefund(db, request);
        var receipt = FieldAdvancePaymentFixture.Receipt(); db.Add(receipt);
        var attempt = new AdvancePaymentAttempt { Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, ProjectId = ProjectId, FamilyId = installment.FamilyId, RequestId = request, InstallmentId = installment.Id, RevisionId = revision.Id, CommandId = receipt.CommandId, PayerId = AliasAccountId, PayerPersonId = FieldAdvancePaymentFixture.PayerPersonId, State = "Mismatch" };
        var hold = new AdvancePaymentHold { Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, ProjectId = ProjectId, FamilyId = installment.FamilyId, Code = "PaymentMismatch", Reason = "test", CreatedBy = AliasAccountId, CreatedByPersonId = FieldAdvancePaymentFixture.PayerPersonId };
        db.AddRange(attempt, hold); await db.SaveChangesAsync();
        var fact = await Row<PaymentIncidentResolution>(db, request); fact.InstallmentId = installment.Id; fact.AttemptId = attempt.Id; fact.HoldId = hold.Id; fact.EvidenceId = refund.EvidenceId; fact.OwnerReceived = 8000m; fact.WrongRecipientReceived = 2000m; fact.WrongRecipientPersonId = RecipientPersonId; Accept(fact); db.Add(fact); await db.SaveChangesAsync();
        var recovery = await Row<PaymentIncidentRecovery>(db, request); recovery.ResolutionId = fact.Id; recovery.ReturnedByPersonId = RecipientPersonId; recovery.ReturnKind = "WrongRecipient"; recovery.Amount = 500m; recovery.ReturnedOn = new(2026, 10, 31); recovery.EvidenceId = refund.EvidenceId; Accept(recovery); db.Add(recovery); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var account = await db.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == request);
        Assert.Equal(10000m, account.Spendable); Assert.Equal(0m, account.Paid); Assert.Equal(0m, account.Reserved);
        Assert.Null((await db.Set<AdvancePaymentHold>().SingleAsync(x => x.Id == hold.Id)).ReleasedAtUtc);
        await Reject(db, "UPDATE field_advance_payment_incident_resolutions SET owner_received=10000", "23514");
        await Reject(db, "DELETE FROM field_advance_payment_incident_recoveries", "23514");
        var duplicate = await Row<PaymentIncidentResolution>(db, request); duplicate.InstallmentId = installment.Id; duplicate.AttemptId = attempt.Id; duplicate.HoldId = hold.Id; duplicate.EvidenceId = refund.EvidenceId; duplicate.OwnerReceived = 8000m; Accept(duplicate); db.Add(duplicate); await RejectSave(db, "23505");
    }

    private static async Task<Refund> SeedRefund(Tpr10DbContext db, Guid request)
    {
        var r = await Row<Refund>(db, request);
        var evidence = new AdvanceEvidence
        {
            Id = Guid.NewGuid(),
            WorkspaceId = r.WorkspaceId,
            ProjectId = r.ProjectId,
            FamilyId = r.FamilyId,
            RequestId = r.RequestId,
            Sha256 = new('a', 64),
            Length = 1,
            ContentType = "image/png",
            CreatedBy = AuthorId,
            CreatedByPersonId = AuthorPersonId
        };
        db.Add(evidence); r.EvidenceId = evidence.Id; r.Amount = 2000m; r.RefundedOn = new(2026, 10, 31); r.Channel = "Cash";
        db.Add(r); await db.SaveChangesAsync(); return r;
    }

    // Test-only population of the common provenance contract, not a production mutation API.
    private static async Task<T> Row<T>(Tpr10DbContext db, Guid request) where T : class, new()
    {
        var source = await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == request);
        var receipt = FieldAdvancePaymentFixture.Receipt(); db.Add(receipt); await db.SaveChangesAsync();
        var row = new T();
        foreach (var (name, value) in new (string, object)[] { ("Id", Guid.NewGuid()), ("WorkspaceId", source.WorkspaceId), ("ProjectId", source.ProjectId), ("FamilyId", source.FamilyId), ("RequestId", request), ("CommandId", receipt.CommandId), ("ProposedBy", AuthorId), ("ProposedByPersonId", AuthorPersonId) })
            typeof(T).GetProperty(name)!.SetValue(row, value);
        return row;
    }
    private static void Accept<T>(T row)
    {
        foreach (var (name, value) in new (string, object)[] { ("ReviewedBy", ReviewerId), ("ReviewedByPersonId", ReviewerPersonId), ("ReviewedAtUtc", DateTimeOffset.UtcNow), ("AcceptedAtUtc", DateTimeOffset.UtcNow), ("State", "Accepted") })
            typeof(T).GetProperty(name)!.SetValue(row, value);
    }
    private static async Task Reject(Tpr10DbContext db, string sql, string state) =>
        Assert.Equal(state, (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql))).SqlState);
    private static async Task RejectSave(Tpr10DbContext db, string state)
    {
        Assert.Equal(state, Assert.IsType<PostgresException>((await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException).SqlState);
        db.ChangeTracker.Clear();
    }
}
