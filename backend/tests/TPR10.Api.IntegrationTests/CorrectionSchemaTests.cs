using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using TPR10.Api.Data;
using static TPR10.Api.IntegrationTests.AttendanceRecordingFixture;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class CorrectionSchemaTests(PostgresFixture postgres)
{
    private const string Previous = "20260926084047_BindEvidenceToAttendanceEvents";

    [Fact]
    public async Task Submitted_revision_is_immutable()
    {
        await using var f = await CreateAsync(postgres.ConnectionString);
        await using var db = f.Database.CreateContext();
        var request = await DraftAsync(db);
        await RevisionAsync(db, request);
        await SubmitAsync(db);
        await RejectAsync(db, "UPDATE correction_revisions SET reason='เปลี่ยนย้อนหลัง'", "23514");
        await RejectAsync(db, "DELETE FROM correction_revisions", "23514");
        await RejectAsync(db, "TRUNCATE correction_revisions CASCADE", "23514");
        Assert.Equal("ลืมลงเวลา", await db.Database.SqlQueryRaw<string>("SELECT reason AS \"Value\" FROM correction_revisions").SingleAsync());
    }

    [Fact]
    public async Task Only_one_active_request_per_original_day()
    {
        await using var f = await CreateAsync(postgres.ConnectionString);
        await using var db = f.Database.CreateContext();
        await DraftAsync(db);
        Assert.Equal("23505", (await Assert.ThrowsAsync<PostgresException>(() => DraftAsync(db))).SqlState);
    }

    [Fact]
    public async Task Expired_history_cannot_be_bypassed()
    {
        await using var f = await CreateAsync(postgres.ConnectionString);
        await using var db = f.Database.CreateContext();
        await DraftAsync(db);
        await db.Database.ExecuteSqlRawAsync("UPDATE correction_requests SET state='Expired',version=2");
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => DraftAsync(db))).SqlState);
        await RejectAsync(db, "UPDATE correction_requests SET state='Draft',version=3", "23514");
        await RejectAsync(db, "UPDATE correction_requests SET original_work_date=original_work_date+1,version=3", "23514");
    }

    [Fact]
    public async Task Effective_change_requires_exact_approved_adjustment()
    {
        await using var f = await CreateAsync(postgres.ConnectionString);
        await f.CreateOpenDayAsync(Employee, Start);
        await using var db = f.Database.CreateContext();
        await RejectAsync(db, "UPDATE attendance_days SET effective_in_utc=effective_in_utc+interval '1 minute',version=2", "23514");
        await RejectAsync(db, "UPDATE attendance_days SET effective_out_utc=effective_in_utc+interval '1 hour',version=2", "23514");
    }

    [Fact]
    public async Task Day_version_cannot_be_advanced_without_a_business_change()
    {
        await using var f = await CreateAsync(postgres.ConnectionString);
        await f.CreateOpenDayAsync(Employee, Start);
        await using var db = f.Database.CreateContext();
        await RejectAsync(db, "UPDATE attendance_days SET version=version+1", "23514");
        await RejectAsync(db, "UPDATE attendance_days SET written_transaction_id=123", "23514");
    }

    [Fact]
    public async Task Live_event_still_requires_evidence()
    {
        await using var f = await CreateAsync(postgres.ConnectionString);
        var day = await f.CreateOpenDayAsync(Employee, Start);
        await f.AddEventAsync(day);
        await using var db = f.Database.CreateContext();
        await RejectAsync(db, """
            INSERT INTO attendance_events(id,day_id,actor_id,action,occurred_at_utc,device_captured_at_utc,
                workspace_id,project_id,site_id,latitude,longitude,accuracy_meters,gps_sampled_at_utc,evidence_id,source)
            SELECT gen_random_uuid(),day_id,actor_id,'CheckOut',occurred_at_utc+interval '1 hour',device_captured_at_utc,
                workspace_id,project_id,site_id,latitude,longitude,accuracy_meters,gps_sampled_at_utc,gen_random_uuid(),'live'
            FROM attendance_events
            """, "23503");
    }

    [Fact]
    public async Task Migration_up_down_up_preserves_6c_and_refuses_retained_corrections()
    {
        await using var f = await CreateAsync(postgres.ConnectionString);
        await using var db = f.Database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(Previous);
        var day = await f.CreateOpenDayAsync(Employee, Start);
        await migrator.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Contains("correction_requests", await db.Database.SqlQueryRaw<string>("SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname='public'").ToArrayAsync());
        await migrator.MigrateAsync(Previous);
        await migrator.MigrateAsync();
        Assert.Equal(day, await db.Database.SqlQueryRaw<Guid>("SELECT id AS \"Value\" FROM attendance_days").SingleAsync());
        await DraftAsync(db);
        Assert.Equal("P0001", (await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync(Previous))).SqlState);
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM correction_requests").SingleAsync());
    }

    [Fact]
    public async Task Approval_cannot_finish_without_atomic_adjustment()
    {
        await using var f = await CreateAsync(postgres.ConnectionString);
        await using var db = f.Database.CreateContext();
        var request = await DraftAsync(db);
        await RevisionAsync(db, request);
        await SubmitAsync(db);
        await RejectAsync(db, "UPDATE correction_requests SET state='Approved',version=4", "23514");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Route_cannot_include_owner_or_reuse_supervisor_as_hr(bool owner)
    {
        await using var f = await CreateAsync(postgres.ConnectionString);
        await using var db = f.Database.CreateContext();
        var request = await DraftAsync(db);
        await RevisionAsync(db, request);
        var candidate = owner ? Employee : Guid.Parse("00000000-0000-0000-0000-000000000032");
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO correction_route_snapshots(request_id,revision,supervisor_id,hr_candidate_ids,membership_version,reporting_version,created_at_utc)
            VALUES({request},1,'00000000-0000-0000-0000-000000000032',ARRAY[{candidate}],1,1,{Start.AddDays(1)})
            """))).SqlState);
    }

    private static Task SubmitAsync(Tpr10DbContext db) => db.Database.ExecuteSqlRawAsync("""
        INSERT INTO users(id,username,normalized_username) VALUES ('00000000-0000-0000-0000-000000000033','hr','HR');
        UPDATE correction_requests SET current_revision=1,version=2;
        INSERT INTO correction_route_snapshots(request_id,revision,supervisor_id,hr_candidate_ids,membership_version,reporting_version,created_at_utc)
        SELECT id,1,'00000000-0000-0000-0000-000000000032',ARRAY['00000000-0000-0000-0000-000000000033'::uuid],1,1,'2026-09-27T00:00:00Z' FROM correction_requests;
        UPDATE correction_requests SET state='PendingSupervisor',version=3;
        """);

    [Fact]
    public async Task Revision_numbers_and_command_receipts_preserve_history()
    {
        await using var f = await CreateAsync(postgres.ConnectionString);
        await using var db = f.Database.CreateContext();
        var request = await DraftAsync(db);
        await RevisionAsync(db, request);
        await RejectAsync(db, """
            INSERT INTO correction_revisions SELECT request_id,3,baseline_day_version,work_date,in_utc,out_utc,
                in_workspace_id,in_project_id,in_site_id,out_workspace_id,out_project_id,out_site_id,reason,created_at_utc
            FROM correction_revisions
            """, "23514");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO correction_command_receipts(actor_id,command_id,request_id,action,payload_sha256,status,result_json,created_at_utc)
            SELECT employee_id,gen_random_uuid(),id,'create',repeat('a',64),201,jsonb_build_object(),'2026-09-27T00:00:00Z' FROM correction_requests
            """);
        await RejectAsync(db, "INSERT INTO correction_command_receipts SELECT * FROM correction_command_receipts", "23505");
        await RejectAsync(db, "UPDATE correction_command_receipts SET result_json=jsonb_build_object('changed',true)", "23514");
        await RejectAsync(db, "DELETE FROM correction_command_receipts", "23514");
        await RejectAsync(db, "UPDATE correction_requests SET deadline_utc=deadline_utc+interval '1 day',version=2", "23514");
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false, true)]
    public async Task Adjustment_requires_two_distinct_approvers_and_exact_proposal(bool approved, bool exact, bool missingDay, bool moveDate, bool forgedBefore = false)
    {
        await using var f = await CreateAsync(postgres.ConnectionString);
        var day = missingDay ? Guid.NewGuid() : await f.CreateOpenDayAsync(Employee, Start);
        if (!missingDay) await f.AddEventAsync(day);
        await using var db = f.Database.CreateContext();
        var proposedIn = moveDate ? Start.AddDays(1) : Start;
        var proposedDate = DateOnly.FromDateTime(proposedIn.UtcDateTime.AddHours(7));
        var request = await DraftAsync(db, missingDay ? null : day);
        await RevisionAsync(db, request, missingDay ? null : 1, proposedIn);
        await SubmitAsync(db);
        if (approved)
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO correction_approval_actions(request_id,revision,step,actor_id,outcome,occurred_at_utc)
                SELECT id,1,'Supervisor','00000000-0000-0000-0000-000000000032','Approved','2026-09-27T00:01:00Z' FROM correction_requests;
                UPDATE correction_requests SET state='PendingHr',version=4;
                INSERT INTO correction_approval_actions(request_id,revision,step,actor_id,outcome,occurred_at_utc)
                SELECT id,1,'Hr','00000000-0000-0000-0000-000000000033','Approved','2026-09-27T00:02:00Z' FROM correction_requests;
                """);
        async Task ApplyAsync()
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            if (approved) await db.Database.ExecuteSqlRawAsync("UPDATE correction_requests SET state='Approved',version=5");
            if (missingDay)
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO attendance_days(id,employee_id,work_date,membership_id,workspace_id,department_id,snapshot_at_utc,effective_in_utc,effective_out_utc)
                    SELECT {day},employee_id,{proposedDate},membership_id,workspace_id,department_id,snapshot_at_utc,{proposedIn},{proposedIn.AddHours(8)}
                    FROM correction_requests WHERE id={request}
                    """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO attendance_adjustments(id,request_id,revision,day_id,before_version,before_work_date,before_in_utc,before_out_utc,
                    after_version,after_work_date,after_in_utc,after_out_utc,applied_at_utc)
                VALUES(gen_random_uuid(),{request},1,{day},{(missingDay ? (long?)null : 1)},{(missingDay && !forgedBefore ? (DateOnly?)null : new DateOnly(2026, 9, 26))},
                    {(missingDay && !forgedBefore ? (DateTimeOffset?)null : Start)},NULL,{(missingDay ? 1 : 2)},{proposedDate},{proposedIn},{proposedIn.AddHours(exact ? 8 : 9)},'2026-09-28T00:03:00Z');
                """);
            if (!missingDay)
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE attendance_days SET work_date={proposedDate},effective_in_utc={proposedIn},effective_out_utc={proposedIn.AddHours(exact ? 8 : 9)},version=2 WHERE id={day}
                    """);
            await tx.CommitAsync();
        }
        if (approved && exact && !forgedBefore)
        {
            await ApplyAsync();
            Assert.Equal(proposedIn.AddHours(8), await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT effective_out_utc AS \"Value\" FROM attendance_days").SingleAsync());
            Assert.Equal(proposedDate, await db.Database.SqlQueryRaw<DateOnly>("SELECT work_date AS \"Value\" FROM attendance_days").SingleAsync());
            Assert.Equal(Start, await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT snapshot_at_utc AS \"Value\" FROM attendance_days").SingleAsync());
            Assert.Equal(missingDay ? 0 : 1, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM attendance_events").SingleAsync());
            if (!missingDay) Assert.Equal(Start, await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT occurred_at_utc AS \"Value\" FROM attendance_events").SingleAsync());
            await RejectAsync(db, "UPDATE attendance_adjustments SET after_out_utc=after_out_utc+interval '1 minute'", "23514");
        }
        else Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(ApplyAsync)).SqlState);
    }

    private static async Task<Guid> DraftAsync(Tpr10DbContext db, Guid? day = null)
    {
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO correction_requests(id,employee_id,original_work_date,day_id,membership_id,workspace_id,department_id,snapshot_at_utc,deadline_anchor_utc,deadline_utc)
            VALUES({id},{Employee},'2026-09-26',{day},'00000000-0000-0000-0000-000000000041','00000000-0000-0000-0000-000000000001',
                '00000000-0000-0000-0000-000000000011',{Start},'2026-10-03T17:00:00Z','2026-10-03T17:00:00Z')
            """);
        return id;
    }

    private static Task RevisionAsync(Tpr10DbContext db, Guid request, long? baseline = null, DateTimeOffset? proposedIn = null) => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO correction_revisions(request_id,revision,baseline_day_version,work_date,in_utc,out_utc,in_workspace_id,in_project_id,in_site_id,
            out_workspace_id,out_project_id,out_site_id,reason,created_at_utc)
        VALUES({request},1,{baseline},{DateOnly.FromDateTime((proposedIn ?? Start).UtcDateTime.AddHours(7))},{proposedIn ?? Start},{(proposedIn ?? Start).AddHours(8)},'00000000-0000-0000-0000-000000000001',
            '00000000-0000-0000-0000-000000000021','00000000-0000-0000-0000-000000000051',
            '00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000021','00000000-0000-0000-0000-000000000051','ลืมลงเวลา',{Start.AddDays(2)})
        """);

    private static async Task RejectAsync(Tpr10DbContext db, string sql, string state) =>
        Assert.Equal(state, (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql))).SqlState);
}
