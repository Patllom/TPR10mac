using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;

namespace TPR10.Api.IntegrationTests;

internal sealed class AttendanceRecordingFixture(IdentityDatabase database) : IAsyncDisposable
{
    public IdentityDatabase Database { get; } = database;
    public static readonly Guid Employee = Guid.Parse("00000000-0000-0000-0000-000000000031");
    public static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-25T17:00:00Z");
    public static async Task<AttendanceRecordingFixture> CreateAsync(string connectionString)
    {
        var database = new IdentityDatabase(connectionString);
        await database.InitializeAsync();
        try
        {
            await using var db = database.CreateContext();
            await db.Database.MigrateAsync();
            var tables = await db.Database.SqlQueryRaw<string>("SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname='public'").ToArrayAsync();
            foreach (var table in new[] { "attendance_days", "attendance_events", "capture_challenges", "attendance_operations" })
                Assert.Contains(table, tables);
            await SeedAsync(db);
            return new(database);
        }
        catch { await database.DisposeAsync(); throw; }
    }

    public async Task<Guid> CreateOpenDayAsync(Guid employeeId, DateTimeOffset occurredAtUtc)
    {
        var id = Guid.NewGuid();
        await using var db = Database.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO attendance_days(id,employee_id,work_date,membership_id,workspace_id,department_id,snapshot_at_utc,effective_in_utc)
            SELECT {id},{employeeId},({occurredAtUtc} AT TIME ZONE 'Asia/Bangkok')::date,id,workspace_id,department_id,{occurredAtUtc},{occurredAtUtc}
            FROM employee_memberships WHERE user_id={employeeId} AND valid_from_utc<={occurredAtUtc} AND (valid_to_utc IS NULL OR valid_to_utc>{occurredAtUtc})
            """);
        return id;
    }

    public async Task<Guid> AddEventAsync(Guid dayId, string action = "CheckIn", DateTimeOffset? at = null)
    {
        await using var db = Database.CreateContext();
        return await AddEventAsync(db, dayId, action, at);
    }

    public async Task CloseDayAsync(Guid dayId, DateTimeOffset at)
    {
        await using var db = Database.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        if (!await db.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM attendance_events WHERE day_id={dayId} AND action='CheckIn') AS \"Value\"").SingleAsync())
            await AddEventAsync(db, dayId, "CheckIn", Start);
        await AddEventAsync(db, dayId, "CheckOut", at);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE attendance_days SET effective_out_utc={at},version=version+1 WHERE id={dayId}");
        await tx.CommitAsync();
    }

    private static async Task<Guid> AddEventAsync(Tpr10DbContext db, Guid dayId, string action, DateTimeOffset? at)
    {
        var id = Guid.NewGuid();
        var evidence = Guid.NewGuid();
        var occurred = at ?? Start;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO evidence_objects(id,operation_id,owner_id,membership_id,workspace_id,department_id,snapshot_at_utc,occurred_at_utc,action,storage_id,storage_version,object_key)
            SELECT {evidence},gen_random_uuid(),employee_id,membership_id,workspace_id,department_id,{occurred},{occurred},{action},'00000000-0000-0000-0000-000000000061',1,
              'objects/' || left(replace({evidence}::text,'-',''),2) || '/' || replace({evidence}::text,'-','') FROM attendance_days WHERE id={dayId}
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO attendance_events(id,day_id,actor_id,action,occurred_at_utc,device_captured_at_utc,workspace_id,project_id,site_id,latitude,longitude,accuracy_meters,gps_sampled_at_utc,evidence_id,source)
            VALUES({id},{dayId},{Employee},{action},{occurred},{occurred},'00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000021','00000000-0000-0000-0000-000000000051',13.7,100.5,10,{occurred},{evidence},'live')
            """);
        return id;
    }

    internal static Task SeedAsync(Tpr10DbContext db) => db.Database.ExecuteSqlRawAsync("""
        INSERT INTO users(id,username,normalized_username) VALUES ('00000000-0000-0000-0000-000000000031','employee','EMPLOYEE'),('00000000-0000-0000-0000-000000000032','other','OTHER');
        INSERT INTO workspaces(id,code,name,created_by) VALUES ('00000000-0000-0000-0000-000000000001','W1','หนึ่ง','00000000-0000-0000-0000-000000000031'),('00000000-0000-0000-0000-000000000002','W2','สอง','00000000-0000-0000-0000-000000000031');
        INSERT INTO departments(id,workspace_id,code,name,created_by) VALUES ('00000000-0000-0000-0000-000000000011','00000000-0000-0000-0000-000000000001','D1','หนึ่ง','00000000-0000-0000-0000-000000000031'),('00000000-0000-0000-0000-000000000012','00000000-0000-0000-0000-000000000002','D2','สอง','00000000-0000-0000-0000-000000000031');
        INSERT INTO projects(id,workspace_id,code,name,created_by) VALUES ('00000000-0000-0000-0000-000000000021','00000000-0000-0000-0000-000000000001','P1','หนึ่ง','00000000-0000-0000-0000-000000000031'),('00000000-0000-0000-0000-000000000022','00000000-0000-0000-0000-000000000002','P2','สอง','00000000-0000-0000-0000-000000000031');
        INSERT INTO sites(id,workspace_id,project_id,code,name,created_by) VALUES ('00000000-0000-0000-0000-000000000051','00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000021','S1','หนึ่ง','00000000-0000-0000-0000-000000000031');
        INSERT INTO employee_memberships(id,user_id,workspace_id,department_id,valid_from_utc,created_by,reason) VALUES ('00000000-0000-0000-0000-000000000041','00000000-0000-0000-0000-000000000031','00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000011','2026-09-01T00:00:00Z','00000000-0000-0000-0000-000000000031','ทดสอบ');
        INSERT INTO attendance_storage_locations(id,alias,kind,config_fingerprint,accept_writes) VALUES ('00000000-0000-0000-0000-000000000061','synthetic','local-folder',repeat('a',64),false);
        INSERT INTO sessions(id,user_id,token_hash,stage,created_at_utc,last_seen_at_utc,expires_at_utc,security_version) VALUES ('00000000-0000-0000-0000-000000000071','00000000-0000-0000-0000-000000000031',decode(repeat('ab',32),'hex'),'Active','2026-09-25T16:00:00Z','2026-09-25T17:00:00Z','2026-09-26T17:00:00Z',1);
        """);

    public ValueTask DisposeAsync() => Database.DisposeAsync();
}
