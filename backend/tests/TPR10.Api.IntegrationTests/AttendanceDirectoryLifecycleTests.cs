using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Identity.Data;
using TPR10.Api.Organization.Data;
using static TPR10.Api.IntegrationTests.AuthorizationTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class AttendanceDirectoryLifecycleTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Audit_failure_rolls_back_directory_identity_and_sessions(bool organization)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        var f = await SeedAsync(d, actor);
        await using var db = d.Database.CreateContext();
        var token = await IdentityTestDriver.TokenAsync(d.Client);
        var auditCount = await db.AuditEvents.CountAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION reject_directory_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.event_type='attendance.directory.ended' THEN RAISE EXCEPTION 'audit fault'; END IF; RETURN NEW; END $$; CREATE TRIGGER reject_directory_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION reject_directory_audit();");
        using var request = IdentityTestDriver.Mutation(token, organization ? $"/api/v1/organization/workspaces/{f.Workspace}" : $"/api/v1/users/{f.Employee}");
        request.Method = HttpMethod.Patch;
        request.Content = JsonContent.Create(organization ? (object)new { name = "ปิด", isActive = false, expectedVersion = 1, reason = "ทดสอบ" } : new { isActive = false });
        using var response = await d.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.All(await db.Set<EmployeeMembership>().ToArrayAsync(), x => { Assert.Null(x.ValidToUtc); Assert.Equal(1, x.Version); });
        Assert.Null((await db.Set<ReportingLine>().SingleAsync()).ValidToUtc);
        Assert.Null((await db.Set<HrAssignment>().SingleAsync()).ValidToUtc);
        Assert.True((await db.Set<Workspace>().SingleAsync()).IsActive);
        Assert.All(await db.Set<IdentityUser>().Where(x => x.Id == f.Employee || x.Id == f.Manager).ToArrayAsync(), x => { Assert.True(x.IsActive); Assert.Equal(0, x.SecurityVersion); });
        Assert.Equal(auditCount, await db.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task Same_database_instant_cannot_end_a_membership_and_rolls_back_disable()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        var f = await SeedAsync(d, actor);
        await using var db = d.Database.CreateContext();
        var now = d.Clock.GetUtcNow();
        await db.Set<EmployeeMembership>().Where(x => x.UserId == f.Employee).ExecuteUpdateAsync(x => x.SetProperty(m => m.ValidFromUtc, now));
        // PostgreSQL stores microseconds; sub-microsecond ticks must not create an empty interval.
        d.Advance(TimeSpan.FromTicks(1));
        using var response = await SendAsync(d, HttpMethod.Patch, $"/api/v1/users/{f.Employee}", new { isActive = false });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True((await db.Set<IdentityUser>().SingleAsync(x => x.Id == f.Employee)).IsActive);
        Assert.Null((await db.Set<ReportingLine>().SingleAsync()).ValidToUtc);
    }

    [Fact]
    public async Task Repeated_disable_is_a_noop_without_more_directory_audit_or_session_revocation()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        var f = await SeedAsync(d, actor);
        using var first = await SendAsync(d, HttpMethod.Patch, $"/api/v1/users/{f.Employee}", new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await using var db = d.Database.CreateContext();
        var count = await db.AuditEvents.CountAsync(x => x.EventType == "attendance.directory.ended");
        using var second = await SendAsync(d, HttpMethod.Patch, $"/api/v1/users/{f.Employee}", new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(count, await db.AuditEvents.CountAsync(x => x.EventType == "attendance.directory.ended"));
        Assert.All(await db.Set<IdentityUser>().Where(x => x.Id == f.Employee || x.Id == f.Manager).ToArrayAsync(), x => Assert.Equal(1, x.SecurityVersion));
    }

    [Theory]
    [InlineData("employee")]
    [InlineData("manager")]
    [InlineData("department")]
    [InlineData("workspace")]
    public async Task Disable_and_reenable_preserve_ended_history_and_do_not_restore_authority(string target)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        var f = await SeedAsync(d, actor);
        var path = target switch
        {
            "employee" => $"/api/v1/users/{f.Employee}",
            "manager" => $"/api/v1/users/{f.Manager}",
            "department" => $"/api/v1/organization/workspaces/{f.Workspace}/departments/{f.Department}",
            _ => $"/api/v1/organization/workspaces/{f.Workspace}"
        };
        object body = target is "employee" or "manager" ? new { isActive = false } : new { name = "ปิดหน่วยงาน", isActive = false, expectedVersion = 1, reason = "ทดสอบการปิด" };
        using var response = await SendAsync(d, HttpMethod.Patch, path, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = d.Database.CreateContext();
        var line = await db.Set<ReportingLine>().SingleAsync();
        Assert.Equal(d.Clock.GetUtcNow(), line.ValidToUtc);
        Assert.Equal(actor, line.EndedBy);
        Assert.Equal(2, line.Version);
        var employee = await db.Set<IdentityUser>().AsNoTracking().SingleAsync(x => x.Id == f.Employee);
        var manager = await db.Set<IdentityUser>().AsNoTracking().SingleAsync(x => x.Id == f.Manager);
        Assert.Equal(1, employee.SecurityVersion);
        Assert.Equal(1, manager.SecurityVersion);
        if (target != "manager") Assert.NotNull((await db.Set<EmployeeMembership>().SingleAsync(x => x.UserId == f.Employee)).ValidToUtc);
        if (target != "employee") Assert.NotNull((await db.Set<HrAssignment>().SingleAsync()).ValidToUtc);
        var before = await db.Set<ReportingLine>().AsNoTracking().SingleAsync();
        body = target is "employee" or "manager" ? new { isActive = true } : new { name = "เปิดหน่วยงาน", isActive = true, expectedVersion = 2, reason = "ทดสอบเปิดใหม่" };
        using var enabled = await SendAsync(d, HttpMethod.Patch, path, body);
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        Assert.Equal(before.ValidToUtc, (await db.Set<ReportingLine>().AsNoTracking().SingleAsync()).ValidToUtc);
        Assert.True(await db.AuditEvents.AnyAsync(x => x.EventType == "attendance.directory.ended" && x.ActorId == actor));
    }

    internal sealed record Fixture(Guid Workspace, Guid Department, Guid Employee, Guid Manager);
    internal static async Task<Fixture> SeedAsync(IdentityTestDriver d, Guid actor)
    {
        d.Advance(TimeSpan.FromTicks(-(d.Clock.GetUtcNow().UtcTicks % 10)));
        await using var db = d.Database.CreateContext();
        var w = Guid.NewGuid(); var dep = Guid.NewGuid(); var employee = Guid.NewGuid(); var manager = Guid.NewGuid();
        var m1 = Guid.NewGuid(); var m2 = Guid.NewGuid(); var from = d.Clock.GetUtcNow().AddDays(-1);
        db.AddRange(new IdentityUser { Id = employee, Username = "employee", NormalizedUsername = "EMPLOYEE", CreatedAtUtc = from },
            new IdentityUser { Id = manager, Username = "manager", NormalizedUsername = "MANAGER", CreatedAtUtc = from },
            new Workspace { Id = w, Code = "W", Name = "พื้นที่", CreatedBy = actor, CreatedAtUtc = from },
            new Department { Id = dep, WorkspaceId = w, Code = "D", Name = "หน่วยงาน", CreatedBy = actor, CreatedAtUtc = from },
            new EmployeeMembership { Id = m1, UserId = employee, WorkspaceId = w, DepartmentId = dep, ValidFromUtc = from, CreatedBy = actor, CreatedAtUtc = from, Reason = "เตรียมทดสอบ" },
            new EmployeeMembership { Id = m2, UserId = manager, WorkspaceId = w, DepartmentId = dep, ValidFromUtc = from, CreatedBy = actor, CreatedAtUtc = from, Reason = "เตรียมทดสอบ" },
            new ReportingLine { Id = Guid.NewGuid(), EmployeeMembershipId = m1, EmployeeUserId = employee, SupervisorUserId = manager, ValidFromUtc = from, CreatedBy = actor, CreatedAtUtc = from, Reason = "เตรียมทดสอบ" },
            new HrAssignment { Id = Guid.NewGuid(), UserId = manager, WorkspaceId = w, DepartmentId = dep, ValidFromUtc = from, CreatedBy = actor, CreatedAtUtc = from, Reason = "เตรียมทดสอบ" });
        await db.SaveChangesAsync();
        return new(w, dep, employee, manager);
    }
}
