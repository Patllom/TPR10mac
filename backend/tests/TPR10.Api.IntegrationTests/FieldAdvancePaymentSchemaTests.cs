using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Payments;
using Npgsql;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvancePaymentSchemaTests(PostgresFixture postgres)
{
    internal const string PreviousMigration = "20261022124725_AddFieldAdvanceRequests";

    [Fact]
    public void BalanceNeverNegative()
    {
        Assert.Equal(8000m, BalanceMath.Available(new(10000m, 10000m, 0m, 2000m)));
        Assert.Equal(6000m, BalanceMath.Available(new(10000m, 10000m, 2000m, 2000m)));
        Assert.Equal(0m, BalanceMath.Available(new(10000m, 2000m, 0m, 2000m)));
        Assert.Throws<InvalidOperationException>(() => BalanceMath.Available(new(10000m, 10000m, 8000.01m, 2000m)));
        Assert.Throws<InvalidOperationException>(() => BalanceMath.Available(new(10000m, 10000.01m, 0m, 0m)));
        Assert.Throws<OverflowException>(() => BalanceMath.Available(new(decimal.MaxValue, decimal.MaxValue, decimal.MaxValue, 1m)));
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("0.001")]
    [InlineData("10000000000000000")]
    public void Invalid_money_is_not_rounded_or_truncated(string amount)
    {
        var value = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Throws<ArgumentOutOfRangeException>(() => BalanceMath.Available(new(value, value, 0m, 0m)));
    }

    [Fact]
    public async Task Existing_7B_writes_create_independent_families_and_exact_basis_without_spendable_money()
    {
        await using var f = await FieldAdvanceRequestFixture.CreateAsync(postgres);
        await f.SeedRequestAsync();
        await using var db = f.CreateContext();
        Assert.Contains("field_advance_payment_accounts", await Tables(db));
        var family = await db.Database.SqlQueryRaw<Guid>("SELECT family_id AS \"Value\" FROM field_advance_requests").SingleAsync();
        Assert.NotEqual(Guid.Empty, family);
        Assert.Equal(12.35m, await db.Database.SqlQueryRaw<decimal>("SELECT approval_basis_amount AS \"Value\" FROM field_advance_request_revisions").SingleAsync());
        var old = await db.Set<AdvanceRequestRevision>().SingleAsync();
        old.IsCurrent = false;
        await db.SaveChangesAsync();
        var next = f.NewRevision(2); next.Total = 123.45m; db.Add(next);
        await db.SaveChangesAsync();
        Assert.Equal(123.45m, next.ApprovalBasisAmount);
        next.Total = 234.56m; await db.SaveChangesAsync();
        Assert.Equal(234.56m, next.ApprovalBasisAmount);
        Assert.Equal(234.56m, await db.Database.SqlQueryRaw<decimal>("SELECT approval_basis_amount AS \"Value\" FROM field_advance_request_revisions WHERE is_current").SingleAsync());
        Assert.Equal(0m, await db.Database.SqlQueryRaw<decimal>("SELECT spendable AS \"Value\" FROM field_advance_payment_accounts").SingleAsync());
        var copy = new AdvanceRequest { Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, ProjectId = ProjectId, OwnerPersonId = AuthorPersonId, CreatedBy = AuthorId, SourceRequestId = f.Request.Id };
        db.Add(copy); await db.SaveChangesAsync();
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>("SELECT count(DISTINCT family_id)::int AS \"Value\" FROM field_advance_requests").SingleAsync());
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("0.001")]
    [InlineData("10000000000000000")]
    [InlineData("'NaN'")]
    [InlineData("'Infinity'")]
    public async Task Stored_account_rejects_invalid_money_without_silent_rounding(string amount)
    {
        await using var f = await FieldAdvanceRequestFixture.CreateAsync(postgres);
        await f.SeedRequestAsync();
        await using var db = f.CreateContext();
        Assert.Contains("field_advance_payment_accounts", await Tables(db));
        // Only the finite test literals above enter this SQL, never user input.
        var sql = "UPDATE field_advance_payment_accounts SET reserved=" + amount;
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Equal("23514", error.SqlState);
    }

    private static Task<string[]> Tables(Tpr10DbContext db) => db.Database.SqlQueryRaw<string>(
        "SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname='public'").ToArrayAsync();

    [Theory]
    [InlineData("current", "23505")]
    [InlineData("number", "23505")]
    [InlineData("family", "23503")]
    [InlineData("request", "23503")]
    [InlineData("recipient", "23503")]
    [InlineData("precision", "23514")]
    [InlineData("zero", "23514")]
    public async Task Installment_revisions_enforce_scope_money_and_uniqueness(string mutation, string code)
    {
        await using var f = await FieldAdvancePaymentFixture.CreateAsync(postgres);
        var request = await f.SeedApprovedAsync(10000m, default);
        var (_, revision) = await f.SeedInstallmentAsync(request);
        await using var db = f.CreateContext();
        revision.Id = Guid.NewGuid(); revision.RevisionNumber = 2; revision.IsCurrent = false;
        switch (mutation)
        {
            case "current": revision.IsCurrent = true; break;
            case "number": revision.RevisionNumber = 1; break;
            case "family":
            case "request":
                var other = await f.SeedApprovedAsync(10000m, default);
                if (mutation == "request") revision.RequestId = other;
                else revision.FamilyId = (await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == other)).FamilyId;
                break;
            case "recipient": revision.RecipientPersonId = ReviewerPersonId; break;
            case "precision": revision.Amount = 0.001m; break;
            case "zero": revision.Amount = 0m; break;
        }
        db.Add(revision);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(code, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [Fact]
    public async Task Attempt_and_success_result_are_unique_and_ledger_is_command_scoped()
    {
        await using var f = await FieldAdvancePaymentFixture.CreateAsync(postgres);
        var request = await f.SeedApprovedAsync(10000m, default);
        var (installment, revision) = await f.SeedInstallmentAsync(request);
        await using var db = f.CreateContext();
        var receipt = FieldAdvancePaymentFixture.Receipt(); db.Add(receipt);
        var attempt = new AdvancePaymentAttempt
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            FamilyId = installment.FamilyId,
            RequestId = request,
            InstallmentId = installment.Id,
            RevisionId = revision.Id,
            CommandId = receipt.CommandId,
            PayerId = AliasAccountId,
            PayerPersonId = FieldAdvancePaymentFixture.PayerPersonId
        };
        db.Add(attempt); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var originalAttempt = attempt.Id;
        var nextReceipt = FieldAdvancePaymentFixture.Receipt(); db.Add(nextReceipt);
        attempt.Id = Guid.NewGuid(); attempt.CommandId = nextReceipt.CommandId; db.Add(attempt);
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23505", Assert.IsType<PostgresException>(duplicate.InnerException).SqlState);
        db.ChangeTracker.Clear();
        var result = new AdvancePaymentResult
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            FamilyId = installment.FamilyId,
            RequestId = request,
            InstallmentId = installment.Id,
            AttemptId = originalAttempt,
            CommandId = receipt.CommandId,
            Amount = 2000m,
            RecipientPersonId = AuthorPersonId,
            PaidOn = new(2026, 10, 24),
            RecordedBy = AliasAccountId,
            RecordedByPersonId = FieldAdvancePaymentFixture.PayerPersonId
        };
        db.Add(result); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        // A different command and a different closed attempt must still not pay this installment twice.
        attempt.State = "Unpaid";
        db.AddRange(nextReceipt, attempt); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        result.Id = Guid.NewGuid(); result.CommandId = nextReceipt.CommandId; result.AttemptId = attempt.Id; db.Add(result);
        duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23505", Assert.IsType<PostgresException>(duplicate.InnerException).SqlState); db.ChangeTracker.Clear();
        var entry = new AdvanceReservationEntry
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            FamilyId = installment.FamilyId,
            RequestId = request,
            InstallmentId = installment.Id,
            CommandId = receipt.CommandId,
            Action = "Reserve",
            Amount = 2000m
        };
        db.Add(entry); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        entry.Id = Guid.NewGuid(); db.Add(entry);
        duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23505", Assert.IsType<PostgresException>(duplicate.InnerException).SqlState);
        foreach (var sql in new[] { "UPDATE field_advance_reservation_entries SET amount=1", "DELETE FROM field_advance_reservation_entries", "TRUNCATE field_advance_reservation_entries" })
            Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql))).SqlState);
    }

    [Fact]
    public async Task Paid_history_never_reopens_and_balance_cannot_be_overcommitted()
    {
        await using var f = await FieldAdvancePaymentFixture.CreateAsync(postgres);
        var request = await f.SeedApprovedAsync(10000m, default);
        await using var db = f.CreateContext();
        var account = await db.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == request);
        account.Paid = 2000m; account.EverPaid = true; account.Reserved = 8000m; await db.SaveChangesAsync();
        account.Reserved = 8000.01m;
        Assert.Equal("23514", Assert.IsType<PostgresException>((await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException).SqlState);
        db.ChangeTracker.Clear();
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE field_advance_payment_accounts SET paid=0,ever_paid=false"))).SqlState);
    }

    [Theory]
    [InlineData("approved-revision")]
    [InlineData("pending-supplement")]
    [InlineData("supplement-family")]
    [InlineData("family-owner")]
    public async Task References_cannot_cross_request_or_owner_within_the_same_project(string reference)
    {
        await using var f = await FieldAdvancePaymentFixture.CreateAsync(postgres);
        var first = await f.SeedApprovedAsync(10000m, default);
        var second = await f.SeedApprovedAsync(1000m, default);
        await using var db = f.CreateContext();
        var family = (await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == first)).FamilyId;
        if (reference == "approved-revision")
        {
            var account = await db.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == first);
            account.ApprovedRevisionId = (await db.Set<AdvanceRequestRevision>().SingleAsync(x => x.RequestId == second)).Id;
        }
        else if (reference == "pending-supplement")
            (await db.Set<AdvanceFamily>().SingleAsync(x => x.Id == family)).PendingSupplementId = second;
        else
            db.Add(new AdvanceRequest
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                ProjectId = ProjectId,
                FamilyId = family,
                RelationKind = "Supplement",
                SupplementOfRequestId = reference == "supplement-family" ? second : first,
                OwnerPersonId = reference == "family-owner" ? ReviewerPersonId : AuthorPersonId,
                CreatedBy = AuthorId
            });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains(Assert.IsType<PostgresException>(error.InnerException).SqlState, new[] { "23503", "23514" });
    }

    [Fact]
    public async Task Backfill_preserves_original_approved_revision_while_new_draft_has_no_extra_money()
    {
        await using var f = await FieldAdvanceRequestFixture.CreateAsync(postgres);
        await using var db = f.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        var request = Guid.NewGuid(); var approved = Guid.NewGuid(); var draft = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($$"""
            INSERT INTO field_advance_requests(id,workspace_id,project_id,owner_person_id,created_by,state)
            VALUES ({{request}},{{WorkspaceId}},{{ProjectId}},{{AuthorPersonId}},{{AuthorId}},'Draft');
            INSERT INTO field_advance_request_revisions(id,workspace_id,project_id,request_id,revision_number,is_current,content_json,content_hash,total,created_by,created_by_person_id)
            VALUES ({{approved}},{{WorkspaceId}},{{ProjectId}},{{request}},1,false,'{}',repeat('a',64),10000,{{AuthorId}},{{AuthorPersonId}}),
                   ({{draft}},{{WorkspaceId}},{{ProjectId}},{{request}},2,true,'{}',repeat('b',64),15000,{{AuthorId}},{{AuthorPersonId}});
            INSERT INTO field_advance_approval_steps(id,workspace_id,project_id,request_id,revision_id,step_order,level_code)
            VALUES (gen_random_uuid(),{{WorkspaceId}},{{ProjectId}},{{request}},{{approved}},1,'Supervisor');
            UPDATE field_advance_request_revisions SET submitted_at_utc=now(),confirmed_by_person_id={{AuthorPersonId}},
                company_policy_revision_id={{CompanyRevisionId}},policy_snapshot_json='{}',policy_hash=repeat('c',64) WHERE id={{approved}};
            UPDATE field_advance_approval_steps SET outcome='Approved',decided_by_person_id={{ReviewerPersonId}},decided_by={{ReviewerId}},
                decided_at_utc=now(),decision_reason='Test approved';
            """);
        await db.Database.MigrateAsync();
        var account = await db.Set<AdvancePaymentAccount>().SingleAsync();
        Assert.Equal(approved, account.ApprovedRevisionId);
        Assert.Equal(10000m, account.Approved); Assert.Equal(10000m, account.Spendable);
        Assert.False(account.EverPaid); Assert.Equal(0m, account.Paid); Assert.Equal(0m, account.Reserved);
        Assert.Equal(15000m, (await db.Set<AdvanceRequestRevision>().SingleAsync(x => x.IsCurrent)).ApprovalBasisAmount);
        Assert.Empty(await db.Set<AdvancePaymentResult>().ToArrayAsync());
        Assert.Empty(await db.Set<AdvanceReservationEntry>().ToArrayAsync());
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(PreviousMigration));
        Assert.Equal("23514", error.SqlState);
        Assert.Equal(10000m, (await db.Set<AdvancePaymentAccount>().AsNoTracking().SingleAsync()).Spendable);
    }

    [Fact]
    public async Task Payment_tables_are_mapped_and_no_pending_model_changes()
    {
        await using var f = await FieldAdvanceRequestFixture.CreateAsync(postgres);
        await using var db = f.CreateContext();
        foreach (var table in new[] { "families", "payment_accounts", "installments", "installment_revisions", "reservation_entries",
            "payment_attempts", "payment_results", "payment_proposals", "payment_participations", "payment_holds", "payment_events", "evidence", "evidence_locations" })
            Assert.Contains("field_advance_" + table, await Tables(db));
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task LegacyCopyRemainsIndependent()
    {
        await using var f = await FieldAdvanceRequestFixture.CreateAsync(postgres);
        await using var db = f.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        await db.Database.ExecuteSqlAsync($$"""
            INSERT INTO field_advance_requests(id,workspace_id,project_id,owner_person_id,created_by,state)
            VALUES ('77000000-0000-0000-0000-000000000001',{{WorkspaceId}},{{ProjectId}},{{AuthorPersonId}},{{AuthorId}},'Approved');
            INSERT INTO field_advance_requests(id,workspace_id,project_id,owner_person_id,created_by,source_request_id,state)
            VALUES ('77000000-0000-0000-0000-000000000002',{{WorkspaceId}},{{ProjectId}},{{AuthorPersonId}},{{AuthorId}},'77000000-0000-0000-0000-000000000001','Draft');
            INSERT INTO field_advance_request_revisions(id,workspace_id,project_id,request_id,revision_number,is_current,content_json,content_hash,total,created_by,created_by_person_id)
            SELECT gen_random_uuid(),workspace_id,project_id,id,1,true,'{}',repeat('a',64),10000,created_by,owner_person_id FROM field_advance_requests;
            """);
        await db.Database.MigrateAsync();
        var tables = await db.Database.SqlQueryRaw<string>("SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname='public'").ToArrayAsync();
        Assert.Contains("field_advance_families", tables);
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>("SELECT count(DISTINCT family_id)::int AS \"Value\" FROM field_advance_requests").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM field_advance_payment_results").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM field_advance_reservation_entries").SingleAsync());
        Assert.Equal(10000m, await db.Database.SqlQueryRaw<decimal>("SELECT approval_basis_amount AS \"Value\" FROM field_advance_request_revisions LIMIT 1").SingleAsync());
        Assert.Equal(10000m, await db.Database.SqlQueryRaw<decimal>("SELECT spendable AS \"Value\" FROM field_advance_payment_accounts WHERE request_id='77000000-0000-0000-0000-000000000001'").SingleAsync());
        Assert.Equal(0m, await db.Database.SqlQueryRaw<decimal>("SELECT spendable AS \"Value\" FROM field_advance_payment_accounts WHERE request_id='77000000-0000-0000-0000-000000000002'").SingleAsync());
    }
}
