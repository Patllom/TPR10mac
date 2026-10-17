using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using TPR10.Api.Data;
using TPR10.Api.Attendance.Recording;
using static TPR10.Api.IntegrationTests.AttendanceRecordingFixture;

#pragma warning disable EF1002 // SQL identifiers/expressions below are fixed synthetic test cases, never user input.

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class AttendanceRecordingSchemaTests(PostgresFixture postgres)
{
    private const string Previous = "20260925113646_PinEvidenceInputDigest";

    [Fact]
    public async Task DuplicateDayAcrossWorkspacesRejected()
    {
        await using var f = await CreateAsync(postgres.ConnectionString);
        var day = await f.CreateOpenDayAsync(Employee, Start);
        await using var db = f.Database.CreateContext();
        await f.CloseDayAsync(day, Start.AddHours(1));
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE employee_memberships SET valid_to_utc='2026-09-25T19:00:00Z',ended_by=created_by,version=2;
            INSERT INTO employee_memberships(id,user_id,workspace_id,department_id,valid_from_utc,created_by,reason)
            VALUES(gen_random_uuid(),'00000000-0000-0000-0000-000000000031','00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000012','2026-09-25T19:00:00Z','00000000-0000-0000-0000-000000000031','ย้าย');
            """);
        Assert.Equal("23505", (await Assert.ThrowsAsync<PostgresException>(() => f.CreateOpenDayAsync(Employee, Start.AddHours(3)))).SqlState);
    }

    [Fact]
    public async Task OnlyOneOpenDayPerEmployee()
    {
        await using var f = await CreateAsync(postgres.ConnectionString);
        var day = await f.CreateOpenDayAsync(Employee, Start);
        Assert.Equal("23505", (await Assert.ThrowsAsync<PostgresException>(() => f.CreateOpenDayAsync(Employee, Start.AddDays(1)))).SqlState);
        await using var db = f.Database.CreateContext();
        await f.CloseDayAsync(day, Start.AddHours(8));
        await f.CreateOpenDayAsync(Employee, Start.AddDays(1));
        Assert.Equal(2, await CountAsync(db, "attendance_days"));
    }

    [Fact]
    public async Task ImmutableEventCannotBeUpdated()
    {
        await using var f = await CreateAsync(postgres.ConnectionString);
        var day = await f.CreateOpenDayAsync(Employee, Start);
        await f.AddEventAsync(day);
        await using var db = f.Database.CreateContext();
        await RejectAsync(db, "UPDATE attendance_events SET occurred_at_utc=occurred_at_utc+interval '1 second'", "23514");
        await RejectAsync(db, "DELETE FROM attendance_events", "23514");
        await RejectAsync(db, "TRUNCATE attendance_events CASCADE", "23514");
        Assert.Equal(1, await CountAsync(db, "attendance_events"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task CheckoutMustFollowCheckin(int seconds)
    {
        await using var f = await CreateAsync(postgres.ConnectionString);
        var day = await f.CreateOpenDayAsync(Employee, Start);
        await f.AddEventAsync(day);
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => f.AddEventAsync(day, "CheckOut", Start.AddSeconds(seconds)))).SqlState);
        await f.AddEventAsync(day, "CheckOut", Start.AddHours(25));
        await using var db = f.Database.CreateContext();
        Assert.Equal(2, await CountAsync(db, "attendance_events"));
    }

    [Fact]
    public async Task CheckoutWithoutOriginalCheckinRejected()
    {
        await using var f = await CreateAsync(postgres.ConnectionString);
        var day = await f.CreateOpenDayAsync(Employee, Start);
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => f.AddEventAsync(day, "CheckOut", Start.AddHours(1)))).SqlState);
    }

    [Fact]
    public async Task DuplicateEventActionRejected()
    {
        await using var f = await CreateAsync(postgres.ConnectionString);
        var day = await f.CreateOpenDayAsync(Employee, Start);
        await f.AddEventAsync(day);
        Assert.Equal("23505", (await Assert.ThrowsAsync<PostgresException>(() => f.AddEventAsync(day))).SqlState);
    }

    [Fact]
    public async Task ConcurrentDuplicateDaysAllowOnlyOneCommit()
    {
        await using var f = await CreateAsync(postgres.ConnectionString);
        var results = await Task.WhenAll(Insert(), Insert());
        Assert.Single(results, x => x == "ok");
        Assert.Single(results, x => x == "23505");
        async Task<string> Insert()
        {
            try { await f.CreateOpenDayAsync(Employee, Start); return "ok"; }
            catch (PostgresException e) { return e.SqlState; }
        }
    }

    [Theory]
    [InlineData("work_date=work_date+1")]
    [InlineData("snapshot_at_utc=snapshot_at_utc+interval '1 hour'")]
    [InlineData("effective_out_utc=effective_in_utc")]
    [InlineData("effective_out_utc=effective_in_utc+interval '1 hour'")]
    public async Task DaySnapshotAndVersionProtected(string assignment)
    {
        await using var f = await CreateAsync(postgres.ConnectionString);
        await f.CreateOpenDayAsync(Employee, Start);
        await using var db = f.Database.CreateContext();
        await RejectAsync(db, $"UPDATE attendance_days SET {assignment}", "23514");
    }

    [Theory]
    [InlineData("latitude", "'NaN'::float8", "23514")]
    [InlineData("latitude", "91", "23514")]
    [InlineData("longitude", "-181", "23514")]
    [InlineData("accuracy_meters", "'Infinity'::float8", "23514")]
    [InlineData("accuracy_meters", "0", "23514")]
    [InlineData("occurred_at_utc", "'infinity'::timestamptz", "23514")]
    [InlineData("actor_id", "'00000000-0000-0000-0000-000000000032'::uuid", "23503")]
    [InlineData("project_id", "'00000000-0000-0000-0000-000000000022'::uuid", "23503")]
    [InlineData("source", "'approved-correction'", "23514")]
    public async Task InvalidEventPayloadRejected(string column, string value, string state)
    {
        await using var f = await CreateAsync(postgres.ConnectionString);
        var day = await f.CreateOpenDayAsync(Employee, Start);
        await f.AddEventAsync(day);
        await using var db = f.Database.CreateContext();
        // Use a second day's row to avoid a duplicate action masking the payload constraint.
        await f.CloseDayAsync(day, Start.AddHours(1));
        var secondDay = await f.CreateOpenDayAsync(Employee, Start.AddDays(1));
        var columns = new[] { "id", "day_id", "actor_id", "action", "occurred_at_utc", "device_captured_at_utc", "workspace_id", "project_id", "site_id", "latitude", "longitude", "accuracy_meters", "gps_sampled_at_utc", "evidence_id", "source" };
        // A new evidence identity is necessary so the unique-evidence guard cannot mask other validation.
        var evidence = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO evidence_objects(id,operation_id,owner_id,membership_id,workspace_id,department_id,snapshot_at_utc,occurred_at_utc,action,storage_id,storage_version,object_key)
            SELECT {evidence},gen_random_uuid(),owner_id,membership_id,workspace_id,department_id,snapshot_at_utc,occurred_at_utc,action,storage_id,storage_version,
            'objects/' || left(replace({evidence}::text,'-',''),2) || '/' || replace({evidence}::text,'-','') FROM evidence_objects WHERE action='CheckIn' LIMIT 1
            """);
        var values = columns.Select(c => c == column ? value : c switch
        {
            "id" => "gen_random_uuid()",
            "day_id" => $"'{secondDay}'::uuid",
            "evidence_id" => $"'{evidence}'::uuid",
            "occurred_at_utc" => "occurred_at_utc+interval '1 day'",
            _ => c
        });
        await RejectAsync(db, $"INSERT INTO attendance_events({string.Join(',', columns)}) SELECT {string.Join(',', values)} FROM attendance_events WHERE action='CheckIn' LIMIT 1", state);
    }

    [Fact]
    public async Task ConcurrentEmployeeLocksSerialize()
    {
        await using var f = await CreateAsync(postgres.ConnectionString);
        await using var db = f.Database.CreateContext();
        await using var first = f.Database.CreateContext();
        await using var second = f.Database.CreateContext();
        await using var tx1 = await first.Database.BeginTransactionAsync();
        await first.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        await AcquireAsync(first);
        var held = await first.Database.SqlQueryRaw<long>("SELECT objid::bigint AS \"Value\" FROM pg_locks WHERE pid=pg_backend_pid() AND locktype='advisory' AND classid=7241003 AND objsubid=2 AND granted").SingleAsync();
        var employeeKey = unchecked((int)held);
        Assert.False(await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(7241003,{employeeKey}) AS \"Value\"").SingleAsync());
        // Release identity lock with the first transaction, then the next caller can get both locks.
        var contender = SecondAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!contender.IsCompleted)
        {
            var waiting = await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_locks WHERE locktype='advisory' AND NOT granted AND database=(SELECT oid FROM pg_database WHERE datname=current_database())").SingleAsync(timeout.Token);
            if (waiting > 0) break;
            await Task.Delay(10, timeout.Token);
        }
        Assert.False(contender.IsCompleted);
        await tx1.CommitAsync();
        await contender.WaitAsync(timeout.Token);
        Assert.True(await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(7241003,{employeeKey}) AS \"Value\"").SingleAsync());
        async Task SecondAsync()
        {
            await using var tx2 = await second.Database.BeginTransactionAsync();
            await second.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
            await AcquireAsync(second);
            await tx2.CommitAsync();
        }
    }

    [Fact]
    public async Task EmployeeLockRequiresIdentityTransaction()
    {
        await using var f = await CreateAsync(postgres.ConnectionString);
        await using var db = f.Database.CreateContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() => AcquireAsync(db));
        await using var tx = await db.Database.BeginTransactionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => AcquireAsync(db));
    }

    [Fact]
    public async Task UpgradeFrom6BPreservesDataAndRollbackRefusesAttendance()
    {
        await using var database = new IdentityDatabase(postgres.ConnectionString);
        await database.InitializeAsync();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(Previous);
        await SeedAsync(db);
        await migrator.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        await migrator.MigrateAsync(Previous); // Empty recording tables can roll back; legacy data retained.
        Assert.Equal(1, await CountAsync(db, "employee_memberships"));
        await migrator.MigrateAsync();
        var tables = await db.Database.SqlQueryRaw<string>("SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname='public'").ToArrayAsync();
        Assert.Contains("attendance_days", tables);
        var f = new AttendanceRecordingFixture(database);
        await f.CreateOpenDayAsync(Employee, Start);
        var error = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync(Previous));
        Assert.Equal("P0001", error.SqlState);
        Assert.Equal(1, await CountAsync(db, "attendance_days"));
    }

    private static Task AcquireAsync(Tpr10DbContext db) => AttendanceEmployeeLock.AcquireAsync(db, Employee, CancellationToken.None);
    private static Task<int> CountAsync(Tpr10DbContext db, string table) => db.Database.SqlQueryRaw<int>($"SELECT count(*)::int AS \"Value\" FROM {table}").SingleAsync();
    private static async Task RejectAsync(Tpr10DbContext db, string sql, string state) => Assert.Equal(state, (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql))).SqlState);

    [Theory]
    [InlineData("capture_challenges", "expires_at_utc=expires_at_utc+interval '1 second'")]
    [InlineData("capture_challenges", "idempotency_key=gen_random_uuid()")]
    [InlineData("attendance_operations", "payload_sha256=repeat('b',64),version=2")]
    [InlineData("attendance_operations", "occurred_at_utc=occurred_at_utc+interval '1 second',version=2")]
    [InlineData("attendance_operations", "lease_until_utc=lease_until_utc+interval '1 minute'")]
    public async Task CaptureIdentityAndOperationPinCannotChange(string table, string assignment)
    {
        await using var f = await CreateAsync(postgres.ConnectionString);
        await using var db = f.Database.CreateContext();
        await SeedOperationAsync(db);
        await RejectAsync(db, $"UPDATE {table} SET {assignment}", "23514");
    }

    [Theory]
    [InlineData("actor_id", "'00000000-0000-0000-0000-000000000032'::uuid")]
    [InlineData("action", "'CheckOut'")]
    [InlineData("site_id", "gen_random_uuid()")]
    [InlineData("idempotency_key", "gen_random_uuid()")]
    public async Task ChallengeOperationMustMatchActorActionSiteAndKey(string column, string value)
    {
        await using var f = await CreateAsync(postgres.ConnectionString);
        await using var db = f.Database.CreateContext();
        await SeedOperationAsync(db);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO capture_challenges(id,actor_id,session_id,action,workspace_id,project_id,site_id,idempotency_key,issued_at_utc,expires_at_utc)
            SELECT '00000000-0000-0000-0000-000000000082',actor_id,session_id,action,workspace_id,project_id,site_id,'00000000-0000-0000-0000-000000000092',issued_at_utc,expires_at_utc FROM capture_challenges
            """);
        var columns = new[] { "id", "challenge_id", "actor_id", "session_id", "action", "workspace_id", "project_id", "site_id", "idempotency_key", "payload_sha256", "occurred_at_utc", "membership_id", "membership_workspace_id", "department_id", "target_day_id", "expected_day_version", "state", "lease_until_utc", "fencing_version", "version", "result_event_id", "result_json" };
        var values = columns.Select(c => c == column ? value : c switch
        {
            "id" => "gen_random_uuid()",
            "challenge_id" => "'00000000-0000-0000-0000-000000000082'::uuid",
            "idempotency_key" => "'00000000-0000-0000-0000-000000000092'::uuid",
            _ => c
        });
        await RejectAsync(db, $"INSERT INTO attendance_operations({string.Join(',', columns)}) SELECT {string.Join(',', values)} FROM attendance_operations", "23503");
    }

    private static Task SeedOperationAsync(Tpr10DbContext db) => db.Database.ExecuteSqlRawAsync("""
        INSERT INTO capture_challenges(id,actor_id,session_id,action,workspace_id,project_id,site_id,idempotency_key,issued_at_utc,expires_at_utc)
        VALUES('00000000-0000-0000-0000-000000000081','00000000-0000-0000-0000-000000000031','00000000-0000-0000-0000-000000000071','CheckIn','00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000021','00000000-0000-0000-0000-000000000051','00000000-0000-0000-0000-000000000091','2026-09-25T17:00:00Z','2026-09-25T17:02:00Z');
        INSERT INTO attendance_operations(id,challenge_id,actor_id,session_id,action,workspace_id,project_id,site_id,idempotency_key,payload_sha256,occurred_at_utc,membership_id,membership_workspace_id,department_id,target_day_id,lease_until_utc)
        VALUES('00000000-0000-0000-0000-000000000101','00000000-0000-0000-0000-000000000081','00000000-0000-0000-0000-000000000031','00000000-0000-0000-0000-000000000071','CheckIn','00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000021','00000000-0000-0000-0000-000000000051','00000000-0000-0000-0000-000000000091',repeat('a',64),'2026-09-25T17:00:01Z','00000000-0000-0000-0000-000000000041','00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000011',gen_random_uuid(),'2026-09-25T17:01:01Z');
        """);
}
