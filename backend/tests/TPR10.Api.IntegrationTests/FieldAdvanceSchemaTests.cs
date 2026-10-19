using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;

namespace TPR10.Api.IntegrationTests;

// SQL identifiers and expressions come only from fixed test literals.
#pragma warning disable EF1002

[Collection("database")]
public sealed class FieldAdvanceSchemaTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Migration_creates_foundation_without_automatic_financial_authority()
    {
        await using var f = await FieldAdvanceFixture.CreateAsync(postgres);
        await using var db = f.CreateContext();
        await FieldAdvanceFixture.AssertFoundationAsync(db);
        foreach (var table in FieldAdvanceFixture.Tables)
            Assert.Equal(0, await CountAsync(db, table));
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData("UPDATE field_advance_policy_revisions SET workspace_id=gen_random_uuid() WHERE project_id IS NULL")]
    [InlineData("UPDATE field_advance_policy_revisions SET project_id='71000000-0000-0000-0000-000000000013' WHERE project_id IS NOT NULL")]
    [InlineData("UPDATE field_advance_policy_revisions SET base_company_version_id=gen_random_uuid() WHERE project_id IS NOT NULL")]
    [InlineData("UPDATE field_advance_policy_revisions SET base_company_version_id=id WHERE project_id IS NOT NULL")]
    public async Task Policy_scope_and_base_references_reject_missing_or_cross_company_targets(string sql)
    {
        await using var f = await FieldAdvanceFixture.CreateAsync(postgres);
        await f.SeedPoliciesAsync();
        await using var db = f.CreateContext();
        await RejectAsync(db, sql, "23503");
    }

    [Theory]
    [InlineData("DELETE FROM field_advance_policy_revisions WHERE project_id IS NULL")]
    [InlineData("DELETE FROM finance_persons WHERE id='72000000-0000-0000-0000-000000000031'")]
    public async Task Referenced_revision_and_person_cannot_be_deleted(string sql)
    {
        await using var f = await FieldAdvanceFixture.CreateAsync(postgres);
        await f.SeedPoliciesAsync();
        await using var db = f.CreateContext();
        await RejectAsync(db, sql, "23503");
        Assert.Equal(2, await CountAsync(db, "field_advance_policy_revisions"));
        Assert.Equal(1, await CountAsync(db, "field_advance_policy_contributors"));
    }

    [Fact]
    public async Task Later_contribution_keeps_original_person_and_account_evidence()
    {
        await using var f = await FieldAdvanceFixture.CreateAsync(postgres);
        await f.SeedPoliciesAsync();
        await using var db = f.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO field_advance_policy_contributors(id,workspace_id,policy_revision_id,person_id,user_id,reason) VALUES
            (gen_random_uuid(),'71000000-0000-0000-0000-000000000001','73000000-0000-0000-0000-000000000001','72000000-0000-0000-0000-000000000032','71000000-0000-0000-0000-000000000032','ผู้ร่วมแก้คนถัดไป');
            UPDATE field_advance_policy_revisions SET version=version+1 WHERE project_id IS NULL;
            """);
        var people = await db.Database.SqlQueryRaw<Guid>(
            "SELECT person_id AS \"Value\" FROM field_advance_policy_contributors ORDER BY person_id").ToArrayAsync();
        Assert.Equal(new[] { FieldAdvanceFixture.AuthorPersonId, FieldAdvanceFixture.ReviewerPersonId }, people);
        Assert.Equal(FieldAdvanceFixture.AuthorId, await db.Database.SqlQueryRaw<Guid>(
            "SELECT user_id AS \"Value\" FROM field_advance_policy_contributors WHERE person_id='72000000-0000-0000-0000-000000000031'").SingleAsync());
    }

    [Fact]
    public async Task Review_keeps_an_otherwise_unreferenced_policy_revision_from_being_deleted()
    {
        await using var f = await FieldAdvanceFixture.CreateAsync(postgres);
        await f.SeedPoliciesAsync();
        await using var db = f.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO field_advance_policy_reviews(id,workspace_id,policy_revision_id,reviewer_id,reviewer_person_id,outcome,reason) VALUES
            (gen_random_uuid(),'71000000-0000-0000-0000-000000000001','73000000-0000-0000-0000-000000000011','71000000-0000-0000-0000-000000000032',
             '72000000-0000-0000-0000-000000000032','Approved','ผลตรวจฉบับโครงการจำลอง');
            """);
        await RejectAsync(db, "DELETE FROM field_advance_policy_revisions WHERE project_id IS NOT NULL", "23503");
        Assert.Equal(1, await CountAsync(db, "field_advance_policy_reviews"));
        await RejectAsync(db, "UPDATE field_advance_policy_reviews SET policy_revision_id=gen_random_uuid()", "23503");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_one_active_pointer_is_allowed_per_exact_scope(bool projectScope)
    {
        await using var f = await FieldAdvanceFixture.CreateAsync(postgres);
        await f.SeedPoliciesAsync();
        await using var db = f.CreateContext();
        await SeedActivePointersAsync(db);
        Assert.Equal(2, await CountAsync(db, "field_advance_active_policies"));
        var predicate = projectScope ? "project_id IS NOT NULL" : "project_id IS NULL";
        await RejectAsync(db, $"""
            INSERT INTO field_advance_active_policies(id,workspace_id,project_id,policy_revision_id,created_by)
            SELECT gen_random_uuid(),workspace_id,project_id,policy_revision_id,created_by
            FROM field_advance_active_policies WHERE {predicate}
            """, "23505");
    }

    [Theory]
    [InlineData("UPDATE field_advance_active_policies SET policy_revision_id=gen_random_uuid()", "23503")]
    [InlineData("UPDATE field_advance_active_policies SET project_id='71000000-0000-0000-0000-000000000013' WHERE project_id IS NOT NULL", "23503")]
    public async Task Active_pointer_rejects_invalid_scope_or_revision(string sql, string sqlState)
    {
        await using var f = await FieldAdvanceFixture.CreateAsync(postgres);
        await f.SeedPoliciesAsync();
        await using var db = f.CreateContext();
        await SeedActivePointersAsync(db);
        await RejectAsync(db, sql, sqlState);
    }

    [Fact]
    public async Task Active_pointer_cannot_reference_a_revision_from_a_different_project_in_same_company()
    {
        await using var f = await FieldAdvanceFixture.CreateAsync(postgres);
        await f.SeedPoliciesAsync();
        await using var db = f.CreateContext();
        await SeedActivePointersAsync(db);
        // The project exists and belongs to the same company: only exact scope binding can reject this.
        await RejectAsync(db, "UPDATE field_advance_active_policies SET project_id='71000000-0000-0000-0000-000000000012' WHERE project_id IS NOT NULL", "23503");
    }

    [Fact]
    public async Task Company_pointer_cannot_reference_a_project_revision_through_null_scope()
    {
        await using var f = await FieldAdvanceFixture.CreateAsync(postgres);
        await f.SeedPoliciesAsync();
        await using var db = f.CreateContext();
        await SeedActivePointersAsync(db);
        await RejectAsync(db, """
            UPDATE field_advance_active_policies
            SET policy_revision_id='73000000-0000-0000-0000-000000000011' WHERE project_id IS NULL
            """, "23503");
    }

    [Fact]
    public async Task Two_explicit_accounts_can_share_one_person_without_creating_a_second_person()
    {
        await using var f = await FieldAdvanceFixture.CreateAsync(postgres);
        await f.SeedPeopleAsync();
        await using var db = f.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO finance_account_links(id,workspace_id,user_id,person_id,created_by,reason) VALUES
            (gen_random_uuid(),'71000000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000034',
             '72000000-0000-0000-0000-000000000031','71000000-0000-0000-0000-000000000031','ยืนยันบัญชีที่สองในข้อมูลจำลอง');
            """);
        var accounts = await db.Database.SqlQueryRaw<Guid>("""
            SELECT user_id AS "Value" FROM finance_account_links
            WHERE person_id='72000000-0000-0000-0000-000000000031' ORDER BY user_id
            """).ToArrayAsync();
        Assert.Equal(new[] { FieldAdvanceFixture.AuthorId, FieldAdvanceFixture.AliasAccountId }, accounts);
        Assert.Equal(4, await CountAsync(db, "finance_persons"));
    }

    [Theory]
    [InlineData("'72000000-0000-0000-0000-000000000032'", "'71000000-0000-0000-0000-000000000031'", "23505")]
    [InlineData("'72000000-0000-0000-0000-000000000041'", "'71000000-0000-0000-0000-000000000034'", "23503")]
    [InlineData("gen_random_uuid()", "'71000000-0000-0000-0000-000000000034'", "23503")]
    [InlineData("'72000000-0000-0000-0000-000000000031'", "gen_random_uuid()", "23503")]
    public async Task Account_link_rejects_ambiguous_person_or_dangling_identity(string person, string account, string sqlState)
    {
        await using var f = await FieldAdvanceFixture.CreateAsync(postgres);
        await f.SeedPeopleAsync();
        await using var db = f.CreateContext();
        await RejectAsync(db, $"""
            INSERT INTO finance_account_links(id,workspace_id,user_id,person_id,created_by,reason) VALUES
            (gen_random_uuid(),'71000000-0000-0000-0000-000000000001',{account},{person},
             '71000000-0000-0000-0000-000000000031','ทดสอบการปฏิเสธ mapping')
            """, sqlState);
    }

    [Theory]
    [InlineData("UPDATE field_advance_grant_proposals SET workspace_id=gen_random_uuid()")]
    [InlineData("UPDATE field_advance_grant_proposals SET project_id='71000000-0000-0000-0000-000000000013'")]
    [InlineData("UPDATE field_advance_grant_reviews SET proposal_id=gen_random_uuid()")]
    [InlineData("DELETE FROM field_advance_grant_proposals")]
    public async Task Grant_history_rejects_invalid_scope_dangling_review_or_proposal_deletion(string sql)
    {
        await using var f = await FieldAdvanceFixture.CreateAsync(postgres);
        await f.SeedGrantAsync();
        await using var db = f.CreateContext();
        await RejectAsync(db, sql, "23503");
        Assert.Equal(1, await CountAsync(db, "field_advance_grant_reviews"));
    }

    [Fact]
    public async Task Grant_storage_preserves_satang_utc_and_bigint_version()
    {
        await using var f = await FieldAdvanceFixture.CreateAsync(postgres);
        await f.SeedGrantAsync();
        await using var db = f.CreateContext();
        await db.Database.ExecuteSqlRawAsync("UPDATE field_advance_grant_proposals SET version=2147483648");
        Assert.Equal(1234.56m, await db.Database.SqlQueryRaw<decimal>(
            "SELECT approval_limit AS \"Value\" FROM field_advance_grant_proposals").SingleAsync());
        Assert.Equal(2147483648L, await db.Database.SqlQueryRaw<long>(
            "SELECT version AS \"Value\" FROM field_advance_grant_proposals").SingleAsync());
        Assert.Equal(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), await db.Database.SqlQueryRaw<DateTimeOffset>(
            "SELECT starts_at_utc AS \"Value\" FROM field_advance_grant_proposals").SingleAsync());
        await RejectAsync(db, "UPDATE field_advance_grant_proposals SET approval_limit=10000000000000000.00", "22003");
    }

    [Fact]
    public async Task Duplicate_command_id_cannot_record_a_second_result()
    {
        await using var f = await FieldAdvanceFixture.CreateAsync(postgres);
        await using var db = f.CreateContext();
        await FieldAdvanceFixture.AssertFoundationAsync(db);
        const string insert = """
            INSERT INTO field_advance_command_receipts
                (command_id,workspace_id,actor_id,action,payload_sha256,status,result_json) VALUES
            ('76000000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000001',
             '71000000-0000-0000-0000-000000000031','test-command',repeat('c',64),200,jsonb_build_object('code','ok'))
            """;
        await db.Database.ExecuteSqlRawAsync(insert);
        await RejectAsync(db, insert, "23505");
        Assert.Equal(1, await CountAsync(db, "field_advance_command_receipts"));
    }

    [Fact]
    public async Task Migration_roundtrip_preserves_existing_directory_and_does_not_grant_admin_financial_access()
    {
        await using var database = new IdentityDatabase(postgres.ConnectionString);
        await database.InitializeAsync();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(FieldAdvanceFixture.PreviousMigration);
        await FieldAdvanceFixture.SeedDirectoryAsync(db);
        for (var round = 0; round < 2; round++)
        {
            await migrator.MigrateAsync();
            await FieldAdvanceFixture.AssertFoundationAsync(db);
            foreach (var table in FieldAdvanceFixture.Tables)
                Assert.Equal(0, await CountAsync(db, table));
            Assert.Equal(4, await CountAsync(db, "employee_memberships"));
            Assert.Equal(1, await CountAsync(db, "user_roles"));
            await migrator.MigrateAsync(FieldAdvanceFixture.PreviousMigration);
            Assert.Equal(4, await CountAsync(db, "users"));
            Assert.Equal(4, await CountAsync(db, "employee_memberships"));
        }
        await migrator.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Backup_restore_retains_policy_contributors_and_explicit_person_links()
    {
        await using var f = await FieldAdvanceFixture.CreateAsync(postgres);
        await f.SeedPoliciesAsync();
        await using var restored = new IdentityDatabase(postgres.ConnectionString);
        await restored.InitializeAsync();
        await postgres.BackupRestoreAsync(f.Database.ConnectionString, restored.ConnectionString);
        await using var db = restored.CreateContext();
        await FieldAdvanceFixture.AssertFoundationAsync(db);
        Assert.Equal(2, await CountAsync(db, "field_advance_policy_revisions"));
        Assert.Equal(3, await CountAsync(db, "finance_account_links"));
        Assert.Equal(FieldAdvanceFixture.AuthorPersonId, await db.Database.SqlQueryRaw<Guid>(
            "SELECT person_id AS \"Value\" FROM field_advance_policy_contributors").SingleAsync());
        Assert.Equal(FieldAdvanceFixture.CompanyRevisionId, await db.Database.SqlQueryRaw<Guid>(
            "SELECT base_company_version_id AS \"Value\" FROM field_advance_policy_revisions WHERE project_id IS NOT NULL").SingleAsync());
        await RejectAsync(db, "DELETE FROM field_advance_policy_revisions WHERE project_id IS NULL", "23503");
        await RejectAsync(db, """
            INSERT INTO finance_account_links(id,workspace_id,user_id,person_id,created_by,reason)
            SELECT gen_random_uuid(),workspace_id,user_id,person_id,created_by,reason FROM finance_account_links
            """, "23505");
    }

    private static Task SeedActivePointersAsync(Tpr10DbContext db) => db.Database.ExecuteSqlRawAsync("""
        UPDATE field_advance_policy_revisions SET state='Active';
        INSERT INTO field_advance_active_policies(id,workspace_id,project_id,policy_revision_id,created_by)
        SELECT gen_random_uuid(),workspace_id,project_id,id,created_by FROM field_advance_policy_revisions;
        """);

    [Fact]
    public async Task Policy_money_columns_preserve_satang_and_reject_out_of_range_amounts()
    {
        await using var f = await FieldAdvanceFixture.CreateAsync(postgres);
        await f.SeedPoliciesAsync();
        await using var db = f.CreateContext();
        Assert.Equal(1234.56m, await db.Database.SqlQueryRaw<decimal>(
            "SELECT upper_inclusive AS \"Value\" FROM field_advance_approval_bands").SingleAsync());
        Assert.Equal(42.75m, await db.Database.SqlQueryRaw<decimal>(
            "SELECT standard_rate AS \"Value\" FROM field_advance_category_rules").SingleAsync());
        await RejectAsync(db, "UPDATE field_advance_approval_bands SET upper_inclusive=10000000000000000.00", "22003");
        await RejectAsync(db, "UPDATE field_advance_category_rules SET standard_rate=10000000000000000.00", "22003");
    }

    [Theory]
    [InlineData("DELETE FROM field_advance_grant_assignments")]
    [InlineData("DELETE FROM field_advance_grant_reviews")]
    [InlineData("DELETE FROM field_advance_command_receipts")]
    [InlineData("UPDATE field_advance_grant_assignments SET workspace_id=gen_random_uuid()")]
    [InlineData("UPDATE field_advance_grant_assignments SET project_id='71000000-0000-0000-0000-000000000011'")]
    [InlineData("UPDATE field_advance_bootstrap_receipts SET grant_assignment_id=gen_random_uuid()")]
    public async Task Assignment_and_bootstrap_references_preserve_history_and_scope(string sql)
    {
        await using var f = await FieldAdvanceFixture.CreateAsync(postgres);
        await f.SeedAssignmentAsync();
        await using var db = f.CreateContext();
        await RejectAsync(db, sql, "23503");
    }

    [Fact]
    public async Task Contributor_history_cannot_be_rewritten_or_removed()
    {
        await using var f = await FieldAdvanceFixture.CreateAsync(postgres);
        await f.SeedPoliciesAsync();
        await using var db = f.CreateContext();
        await RejectAsync(db, "UPDATE field_advance_policy_contributors SET person_id='72000000-0000-0000-0000-000000000032'", "23514");
        await RejectAsync(db, "DELETE FROM field_advance_policy_contributors", "23514");
        await RejectAsync(db, "TRUNCATE field_advance_policy_contributors", "23514");
        Assert.Equal(1, await CountAsync(db, "field_advance_policy_contributors"));
    }

    [Fact]
    public async Task Ended_account_link_retains_person_evidence_when_a_new_explicit_link_is_created()
    {
        await using var f = await FieldAdvanceFixture.CreateAsync(postgres);
        await f.SeedPeopleAsync();
        await using var db = f.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE finance_account_links SET ended_at_utc='2026-02-01T00:00:00Z',
             ended_by='71000000-0000-0000-0000-000000000031',end_reason='สิ้นสุด mapping จำลอง',version=version+1
            WHERE user_id='71000000-0000-0000-0000-000000000031';
            INSERT INTO finance_account_links(id,workspace_id,user_id,person_id,created_by,created_at_utc,reason)
            VALUES (gen_random_uuid(),'71000000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000031',
             '72000000-0000-0000-0000-000000000032','71000000-0000-0000-0000-000000000031','2026-02-01T00:00:00Z','แก้ mapping โดยชัดแจ้ง');
            """);
        Assert.Equal(FieldAdvanceFixture.AuthorPersonId, await db.Database.SqlQueryRaw<Guid>(
            "SELECT person_id AS \"Value\" FROM finance_account_links WHERE ended_at_utc IS NOT NULL").SingleAsync());
        await RejectAsync(db, "UPDATE finance_account_links SET person_id='72000000-0000-0000-0000-000000000032' WHERE ended_at_utc IS NOT NULL", "23514");
        await RejectAsync(db, "DELETE FROM finance_account_links WHERE ended_at_utc IS NOT NULL", "23514");
    }

    private static Task<int> CountAsync(Tpr10DbContext db, string table) =>
        db.Database.SqlQueryRaw<int>($"SELECT count(*)::int AS \"Value\" FROM {table}").SingleAsync();

    [Fact]
    public async Task Ef_can_create_revision_with_generated_scope_and_detect_a_stale_update()
    {
        await using var f = await FieldAdvanceFixture.CreateAsync(postgres);
        await f.SeedPeopleAsync();
        await using var db = f.CreateContext();
        var row = new PolicyRevision
        {
            Id = Guid.NewGuid(),
            WorkspaceId = FieldAdvanceFixture.WorkspaceId,
            State = "Draft",
            ClearingDays = 7,
            ContentHash = new string('d', 64),
            CreatedBy = FieldAdvanceFixture.AuthorId,
            CreatedByPersonId = FieldAdvanceFixture.AuthorPersonId,
            Reason = "ฉบับจำลองจาก EF"
        };
        db.Add(row);
        await db.SaveChangesAsync();
        await using var stale = f.CreateContext();
        var staleRow = await stale.Set<PolicyRevision>().SingleAsync(x => x.Id == row.Id);
        row.Version++;
        row.ClearingDays = 8;
        await db.SaveChangesAsync();
        staleRow.Version++;
        staleRow.ClearingDays = 9;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
        await using var current = f.CreateContext();
        Assert.Equal(8, (await current.Set<PolicyRevision>().SingleAsync(x => x.Id == row.Id)).ClearingDays);
    }

    [Fact]
    public async Task Downgrade_refuses_to_discard_retained_financial_history()
    {
        await using var f = await FieldAdvanceFixture.CreateAsync(postgres);
        await f.SeedPoliciesAsync();
        await using var db = f.CreateContext();
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            db.GetService<IMigrator>().MigrateAsync(FieldAdvanceFixture.PreviousMigration));
        Assert.Equal("23514", error.SqlState);
        Assert.Equal(2, await CountAsync(db, "field_advance_policy_revisions"));
        Assert.Equal(3, await CountAsync(db, "finance_account_links"));
    }

    [Fact]
    public async Task Account_link_rejects_backdated_overlap_with_closed_person_mapping()
    {
        await using var f = await FieldAdvanceFixture.CreateAsync(postgres);
        await f.SeedPeopleAsync();
        await using var db = f.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE finance_account_links SET ended_at_utc='2026-02-01T00:00:00Z',
             ended_by='71000000-0000-0000-0000-000000000031',end_reason='สิ้นสุด mapping จำลอง',version=version+1
            WHERE user_id='71000000-0000-0000-0000-000000000031';
            """);
        await RejectAsync(db, """
            INSERT INTO finance_account_links(id,workspace_id,user_id,person_id,created_by,created_at_utc,reason)
            VALUES (gen_random_uuid(),'71000000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000031',
             '72000000-0000-0000-0000-000000000032','71000000-0000-0000-0000-000000000031','2026-01-15T00:00:00Z','ช่วงเวลาซ้อนทับจำลอง')
            """, "23505");
    }

    [Theory]
    [InlineData(IsolationLevel.ReadCommitted, "23505")]
    [InlineData(IsolationLevel.RepeatableRead, "40001")]
    [InlineData(IsolationLevel.Serializable, "40001")]
    public async Task Closed_account_links_conflict_across_concurrent_snapshots(IsolationLevel isolation, string sqlState)
    {
        await using var f = await FieldAdvanceFixture.CreateAsync(postgres);
        await f.SeedPeopleAsync();
        await using var observer = f.CreateContext();
        await using var first = new NpgsqlConnection(f.Database.ConnectionString);
        await using var second = new NpgsqlConnection(f.Database.ConnectionString);
        await first.OpenAsync();
        await second.OpenAsync();
        await using var tx1 = await first.BeginTransactionAsync(isolation);
        await using var tx2 = await second.BeginTransactionAsync(isolation);
        // Both transactions establish a data snapshot before either inserts a link.
        const string snapshotSql = "SELECT count(*) FROM finance_account_links WHERE user_id='71000000-0000-0000-0000-000000000034'";
        await using var snapshot1 = new NpgsqlCommand(snapshotSql, first, tx1);
        await using var snapshot2 = new NpgsqlCommand(snapshotSql, second, tx2);
        Assert.Equal(0L, await snapshot1.ExecuteScalarAsync());
        Assert.Equal(0L, await snapshot2.ExecuteScalarAsync());
        const string insertSql = """
            INSERT INTO finance_account_links
                (id,workspace_id,user_id,person_id,created_by,created_at_utc,reason,ended_at_utc,ended_by,end_reason)
            VALUES (gen_random_uuid(),'71000000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000034',
                @person,'71000000-0000-0000-0000-000000000031','2026-03-01T00:00:00Z','ประวัติเชื่อมบัญชีจำลอง',
                '2026-04-01T00:00:00Z','71000000-0000-0000-0000-000000000031','สิ้นสุดช่วงเวลาจำลอง')
            """;
        await using var insert1 = new NpgsqlCommand(insertSql, first, tx1);
        insert1.Parameters.AddWithValue("person", FieldAdvanceFixture.AuthorPersonId);
        await insert1.ExecuteNonQueryAsync();
        await using var insert2 = new NpgsqlCommand(insertSql, second, tx2);
        insert2.Parameters.AddWithValue("person", FieldAdvanceFixture.ReviewerPersonId);
        var contender = insert2.ExecuteNonQueryAsync();
        var firstCommitted = false;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var observedBlock = false;
            while (!contender.IsCompleted)
            {
                observedBlock = await observer.Database.SqlQuery<int>(
                    $"SELECT cardinality(pg_blocking_pids({second.ProcessID})) AS \"Value\"").SingleAsync(deadline.Token) > 0;
                if (observedBlock) break;
                await Task.Delay(10, deadline.Token);
            }
            Assert.True(observedBlock, "คู่แข่งต้องรอ lock จริงก่อน commit แรก");
            Assert.False(contender.IsCompleted);
            await tx1.CommitAsync();
            firstCommitted = true;
            var error = await Record.ExceptionAsync(async () =>
            {
                await contender;
                await tx2.CommitAsync();
            });
            Assert.Equal(sqlState, Assert.IsType<PostgresException>(error).SqlState);
            await tx2.RollbackAsync();
            Assert.Equal(1, await observer.Database.SqlQueryRaw<int>(
                "SELECT count(*)::int AS \"Value\" FROM finance_account_links WHERE user_id='71000000-0000-0000-0000-000000000034'").SingleAsync());
            Assert.Equal(0L, await observer.Database.SqlQueryRaw<long>(
                "SELECT security_version AS \"Value\" FROM users WHERE id='71000000-0000-0000-0000-000000000034'").SingleAsync());
        }
        finally
        {
            if (!firstCommitted) await tx1.RollbackAsync();
            if (!contender.IsCompleted)
            {
                try { await contender; }
                catch (PostgresException) { }
            }
        }
    }

    private static async Task RejectAsync(Tpr10DbContext db, string sql, string sqlState)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Equal(sqlState, error.SqlState);
    }
}
