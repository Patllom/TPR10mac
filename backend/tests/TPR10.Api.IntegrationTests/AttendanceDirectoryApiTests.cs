using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Accounts;
using TPR10.Api.Organization.Data;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class AttendanceDirectoryApiTests(PostgresFixture postgres)
{
    internal const string Root = "/api/v1/attendance/directory";
    public static IEnumerable<object[]> Routes => new[]
    {
        ("GET", "/memberships"), ("POST", "/memberships"), ("POST", "/memberships/00000000-0000-0000-0000-000000000099/end"),
        ("GET", "/reporting-lines"), ("POST", "/reporting-lines"), ("POST", "/reporting-lines/00000000-0000-0000-0000-000000000099/end"),
        ("GET", "/hr-assignments"), ("POST", "/hr-assignments"), ("POST", "/hr-assignments/00000000-0000-0000-0000-000000000099/end"),
        ("GET", "/options/users"), ("GET", "/options/workspaces"), ("GET", "/options/departments?workspaceId=00000000-0000-0000-0000-000000000099")
    }.SelectMany(x => new[] { new object[] { x.Item1, x.Item2, "anonymous", 401 }, new object[] { x.Item1, x.Item2, "staff", 403 }, new object[] { x.Item1, x.Item2, "expired", 403 } });

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Every_route_requires_session_permission_and_recent_mfa(string method, string path, string actor, int status)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        if (actor == "staff") { await d.SeedUserAsync("staff", MfaTests.Password, []); await d.LoginAsync("staff", MfaTests.Password); }
        if (actor == "expired") { await OperatorAsync(d); d.Advance(TimeSpan.FromMinutes(15)); }
        using var response = method == "GET" ? await d.Client.GetAsync(Root + path) : await d.PostAsync(Root + path, new { });
        Assert.Equal(status, (int)response.StatusCode);
        await using var db = d.Database.CreateContext();
        Assert.Empty(await db.Set<EmployeeMembership>().ToArrayAsync());
    }

    [Fact]
    public async Task Membership_replace_noop_and_end_preserve_history_and_revoke_once()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await OperatorAsync(d);
        var f = await AttendanceDirectoryLifecycleTests.SeedAsync(d, actor);
        await using var db = d.Database.CreateContext();
        var old = await db.Set<EmployeeMembership>().AsNoTracking().SingleAsync(x => x.UserId == f.Employee);
        using var noop = await d.PostAsync(Root + "/memberships", new { userId = f.Employee, workspaceId = f.Workspace, departmentId = f.Department, expectedVersion = 1, reason = "ไม่เปลี่ยน" });
        Assert.Equal(HttpStatusCode.OK, noop.StatusCode);
        Assert.Equal(0, (await db.Set<IdentityUser>().AsNoTracking().SingleAsync(x => x.Id == f.Employee)).SecurityVersion);
        var department = Guid.NewGuid();
        db.Add(new Department { Id = department, WorkspaceId = f.Workspace, Code = "D2", Name = "ใหม่", CreatedBy = actor, CreatedAtUtc = d.Clock.GetUtcNow() });
        await db.SaveChangesAsync();
        using var replaced = await d.PostAsync(Root + "/memberships", new { userId = f.Employee, workspaceId = f.Workspace, departmentId = department, expectedVersion = 1, reason = "ย้ายหน่วย" });
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var view = await replaced.Content.ReadFromJsonAsync<JsonElement>();
        var id = view.GetProperty("id").GetGuid();
        Assert.NotEqual(old.Id, id);
        Assert.Equal(3, view.GetProperty("version").GetInt64());
        Assert.Equal(2, await db.Set<EmployeeMembership>().CountAsync(x => x.UserId == f.Employee));
        Assert.NotNull((await db.Set<ReportingLine>().SingleAsync()).ValidToUtc);
        Assert.Equal(1, (await db.Set<IdentityUser>().AsNoTracking().SingleAsync(x => x.Id == f.Manager)).SecurityVersion);
        using var conflict = await d.PostAsync(Root + "/memberships", new { userId = f.Employee, workspaceId = f.Workspace, departmentId = department, expectedVersion = (long?)null, reason = "เก่า" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var sameInstant = await d.PostAsync(Root + $"/memberships/{id}/end", new { expectedVersion = 3, reason = "สิ้นสุดทันที" });
        Assert.Equal(HttpStatusCode.Conflict, sameInstant.StatusCode);
        d.Advance(TimeSpan.FromSeconds(1));
        using var ended = await d.PostAsync(Root + $"/memberships/{id}/end", new { expectedVersion = 3, reason = "สิ้นสุด" });
        Assert.Equal(HttpStatusCode.NoContent, ended.StatusCode);
        Assert.Empty(await db.Set<EmployeeMembership>().Where(x => x.UserId == f.Employee && x.ValidToUtc == null).ToArrayAsync());
    }

    [Theory]
    [InlineData("memberships")]
    [InlineData("reporting-lines")]
    [InlineData("hr-assignments")]
    public async Task Unknown_fields_and_missing_fields_are_audited_without_body(string resource)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await OperatorAsync(d);
        using var response = await d.PostAsync(Root + "/" + resource, new { actorId = actor, reason = "RAW-BODY-MARKER" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = d.Database.CreateContext();
        Assert.True(await db.AuditEvents.AnyAsync(x => x.EventType == "attendance.directory.denied" && x.ActorId == actor));
        Assert.DoesNotContain("RAW-BODY-MARKER", string.Join(" ", await db.AuditMetadata.Select(x => x.Value).ToArrayAsync()));
    }

    [Fact]
    public async Task Reporting_and_hr_mutations_enforce_graph_and_no_self_elevation()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await OperatorAsync(d);
        var f = await AttendanceDirectoryLifecycleTests.SeedAsync(d, actor);
        await using var db = d.Database.CreateContext();
        db.Add(new EmployeeMembership { Id = Guid.NewGuid(), UserId = actor, WorkspaceId = f.Workspace, DepartmentId = f.Department, ValidFromUtc = d.Clock.GetUtcNow().AddDays(-1), CreatedBy = actor, CreatedAtUtc = d.Clock.GetUtcNow(), Reason = "เตรียมผู้ดูแล" });
        await db.SaveChangesAsync();
        using var self = await d.PostAsync(Root + "/reporting-lines", new { employeeUserId = f.Employee, supervisorUserId = actor, expectedVersion = 1, reason = "ให้ตนดูทีม" });
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        using var cycle = await d.PostAsync(Root + "/reporting-lines", new { employeeUserId = f.Manager, supervisorUserId = f.Employee, expectedVersion = (long?)null, reason = "วงวน" });
        Assert.Equal(HttpStatusCode.BadRequest, cycle.StatusCode);
        using var hrSelf = await d.PostAsync(Root + "/hr-assignments", new { userId = actor, workspaceId = f.Workspace, departmentId = f.Department, reason = "ตนเอง" });
        Assert.Equal(HttpStatusCode.Forbidden, hrSelf.StatusCode);
        using var hr = await d.PostAsync(Root + "/hr-assignments", new { userId = f.Employee, workspaceId = f.Workspace, departmentId = f.Department, reason = "มอบหมาย" });
        Assert.Equal(HttpStatusCode.Created, hr.StatusCode);
        var id = (await hr.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var duplicate = await d.PostAsync(Root + "/hr-assignments", new { userId = f.Employee, workspaceId = f.Workspace, departmentId = f.Department, reason = "ซ้ำ" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        d.Advance(TimeSpan.FromSeconds(1));
        using var ended = await d.PostAsync(Root + $"/hr-assignments/{id}/end", new { expectedVersion = 1, reason = "ถอน" });
        Assert.Equal(HttpStatusCode.NoContent, ended.StatusCode);
        Assert.Equal(f.Manager, (await db.Set<ReportingLine>().SingleAsync()).SupervisorUserId);
    }

    internal static async Task<Guid> OperatorAsync(IdentityTestDriver d)
    {
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        await using var db = d.Database.CreateContext();
        db.Add(new RolePermission { RoleId = IdentityCatalog.AdministratorRoleId, PermissionId = AttendanceIdentityTests.Permission(18) });
        await db.SaveChangesAsync();
        return actor;
    }
}
