using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Data;
using TPR10.Api.Identity.Authorization;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Organization;
using static TPR10.Api.IntegrationTests.AttendanceDirectoryApiTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class AttendanceDirectoryAtomicityTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Stale_reporting_version_cannot_overwrite_a_replacement()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await OperatorAsync(d);
        var f = await AttendanceDirectoryLifecycleTests.SeedAsync(d, actor);
        await using var db = d.Database.CreateContext();
        var leader = Guid.NewGuid();
        db.Add(new IdentityUser { Id = leader, Username = "leader", NormalizedUsername = "LEADER", CreatedAtUtc = d.Clock.GetUtcNow() });
        db.Add(new EmployeeMembership { Id = Guid.NewGuid(), UserId = leader, WorkspaceId = f.Workspace, DepartmentId = f.Department, ValidFromUtc = d.Clock.GetUtcNow().AddDays(-1), CreatedBy = actor, CreatedAtUtc = d.Clock.GetUtcNow(), Reason = "ทดสอบ" });
        await db.SaveChangesAsync();
        using var first = await d.PostAsync(Root + "/reporting-lines", new { employeeUserId = f.Employee, supervisorUserId = leader, expectedVersion = 1, reason = "เปลี่ยน" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        d.Advance(TimeSpan.FromSeconds(1));
        using var stale = await d.PostAsync(Root + "/reporting-lines", new { employeeUserId = f.Employee, supervisorUserId = f.Manager, expectedVersion = 1, reason = "หน้าจอเก่า" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(leader, (await db.Set<ReportingLine>().SingleAsync(x => x.EmployeeUserId == f.Employee && x.ValidToUtc == null)).SupervisorUserId);
    }

    [Fact]
    public async Task Stale_membership_version_cannot_overwrite_a_new_history_row()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await OperatorAsync(d);
        var f = await AttendanceDirectoryLifecycleTests.SeedAsync(d, actor);
        await using var db = d.Database.CreateContext();
        var second = Guid.NewGuid();
        db.Add(new TPR10.Api.Organization.Data.Department { Id = second, WorkspaceId = f.Workspace, Code = "D2", Name = "ใหม่", CreatedBy = actor, CreatedAtUtc = d.Clock.GetUtcNow() });
        await db.SaveChangesAsync();
        using var first = await d.PostAsync(Root + "/memberships", new { userId = f.Employee, workspaceId = f.Workspace, departmentId = second, expectedVersion = 1, reason = "เปลี่ยน" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        d.Advance(TimeSpan.FromSeconds(1));
        using var stale = await d.PostAsync(Root + "/memberships", new { userId = f.Employee, workspaceId = f.Workspace, departmentId = f.Department, expectedVersion = 1, reason = "หน้าจอเก่า" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(second, (await db.Set<EmployeeMembership>().SingleAsync(x => x.UserId == f.Employee && x.ValidToUtc == null)).DepartmentId);
    }

    [Fact]
    public async Task Literal_prefix_accepts_valid_supplementary_unicode()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await OperatorAsync(d);
        using var response = await d.Client.GetAsync(Root + "/options/users?prefix=" + Uri.EscapeDataString("😀"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Create_replace_and_end_reporting_returns_versions_and_keeps_history()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await OperatorAsync(d);
        var f = await AttendanceDirectoryLifecycleTests.SeedAsync(d, actor);
        var user = Guid.NewGuid();
        await using var db = d.Database.CreateContext();
        db.Add(new IdentityUser { Id = user, Username = "newhire", NormalizedUsername = "NEWHIRE", CreatedAtUtc = d.Clock.GetUtcNow() });
        await db.SaveChangesAsync();
        using var membership = await d.PostAsync(Root + "/memberships", new { userId = user, workspaceId = f.Workspace, departmentId = f.Department, expectedVersion = (long?)null, reason = "รับเข้า" });
        Assert.Equal(HttpStatusCode.Created, membership.StatusCode);
        using var line = await d.PostAsync(Root + "/reporting-lines", new { employeeUserId = user, supervisorUserId = f.Manager, expectedVersion = (long?)null, reason = "จัดหัวหน้า" });
        Assert.Equal(HttpStatusCode.Created, line.StatusCode);
        var oldId = (await line.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        d.Advance(TimeSpan.FromSeconds(1));
        using var replacement = await d.PostAsync(Root + "/reporting-lines", new { employeeUserId = user, supervisorUserId = f.Employee, expectedVersion = 1, reason = "เปลี่ยนหัวหน้า" });
        Assert.Equal(HttpStatusCode.OK, replacement.StatusCode);
        var nextId = (await replacement.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.NotEqual(oldId, nextId);
        Assert.Equal(2, (await db.Set<ReportingLine>().SingleAsync(x => x.Id == oldId)).Version);
        d.Advance(TimeSpan.FromSeconds(1));
        using var ended = await d.PostAsync(Root + $"/reporting-lines/{nextId}/end", new { expectedVersion = 3, reason = "สิ้นสุด" });
        Assert.Equal(HttpStatusCode.NoContent, ended.StatusCode);
        Assert.Equal(2, await db.Set<ReportingLine>().CountAsync(x => x.EmployeeUserId == user && x.ValidToUtc != null));
        using var history = await d.Client.GetAsync(Root + $"/reporting-lines?employeeUserId={user}&includeEnded=true");
        Assert.Equal(2, (await history.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Denial_audit_failure_returns_unavailable_not_an_unaudited_success()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await OperatorAsync(d);
        await using var db = d.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION reject_directory_denied() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.event_type='attendance.directory.denied' THEN RAISE EXCEPTION 'audit fault'; END IF; RETURN NEW; END $$; CREATE TRIGGER reject_directory_denied BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION reject_directory_denied();");
        using var response = await d.Client.GetAsync(Root + "/memberships?page=0");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(await db.Set<EmployeeMembership>().ToArrayAsync());
    }

    [Fact]
    public async Task Audit_failure_after_saved_replacement_rolls_back_rows_versions_and_security_versions()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await OperatorAsync(d);
        var f = await AttendanceDirectoryLifecycleTests.SeedAsync(d, actor);
        await using var db = d.Database.CreateContext();
        var second = Guid.NewGuid();
        db.Add(new TPR10.Api.Organization.Data.Department { Id = second, WorkspaceId = f.Workspace, Code = "D2", Name = "สอง", CreatedBy = actor, CreatedAtUtc = d.Clock.GetUtcNow() });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION reject_directory_completed() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.event_type='attendance.directory.completed' THEN RAISE EXCEPTION 'audit fault'; END IF; RETURN NEW; END $$; CREATE TRIGGER reject_directory_completed BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION reject_directory_completed();");
        using var response = await d.PostAsync(Root + "/memberships", new { userId = f.Employee, workspaceId = f.Workspace, departmentId = second, expectedVersion = 1, reason = "ย้าย" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var membership = await db.Set<EmployeeMembership>().SingleAsync(x => x.UserId == f.Employee);
        Assert.Equal(f.Department, membership.DepartmentId); Assert.Equal(1, membership.Version); Assert.Null(membership.ValidToUtc);
        Assert.Null((await db.Set<ReportingLine>().SingleAsync()).ValidToUtc);
        Assert.All(await db.Set<IdentityUser>().Where(x => x.Id == f.Employee || x.Id == f.Manager).ToArrayAsync(), x => Assert.Equal(0, x.SecurityVersion));
        Assert.False(await db.AuditEvents.AnyAsync(x => x.EventType == "attendance.directory.completed"));
    }

    [Fact]
    public async Task Concurrent_duplicate_grants_have_one_winner_and_one_revocation()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await OperatorAsync(d);
        var f = await AttendanceDirectoryLifecycleTests.SeedAsync(d, actor);
        await using var observer = d.Database.CreateContext();
        var snapshot = await observer.Set<IdentitySession>().AsNoTracking().SingleAsync(x => x.UserId == actor && x.RevokedAtUtc == null);
        var gate = new LockGate();
        async Task<int?> Attempt()
        {
            await using var db = new Tpr10DbContext(new DbContextOptionsBuilder<Tpr10DbContext>().UseNpgsql(d.Database.ConnectionString).AddInterceptors(gate).Options);
            var current = new RequestSession { Entity = snapshot }; var permission = new PermissionContext();
            var service = new DirectoryService(db, current, new PermissionMutationGuard(db, current, permission, d.Clock), permission,
                new SessionService(db, d.Clock, current, new EffectiveRolePolicy(db)), AccountProvisioningTests.Audit(d, db), d.Clock);
            return AccountProvisioningTests.Status(await service.GrantHrAsync(new(f.Employee, f.Workspace, f.Department, "พร้อมกัน"), default));
        }
        var one = Attempt(); var two = Attempt();
        try { await gate.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { gate.Resume.TrySetResult(); }
        Assert.Equal(new int?[] { 201, 409 }, (await Task.WhenAll(one, two)).OrderBy(x => x).ToArray());
        Assert.Equal(1, await observer.Set<HrAssignment>().CountAsync(x => x.UserId == f.Employee));
        Assert.Equal(1, (await observer.Set<IdentityUser>().SingleAsync(x => x.Id == f.Employee)).SecurityVersion);
        Assert.Equal(1, await observer.AuditEvents.CountAsync(x => x.EventType == "attendance.directory.completed"));
        Assert.Equal(1, await observer.AuditEvents.CountAsync(x => x.EventType == "attendance.directory.denied"));
    }

    [Theory]
    [InlineData("workspace", 404)]
    [InlineData("department", 404)]
    [InlineData("employee", 404)]
    [InlineData("actor", 403)]
    public async Task Lifecycle_committed_before_waiting_directory_grant_is_rechecked_without_partial_effects(string target, int expectedStatus)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await OperatorAsync(d);
        var f = await AttendanceDirectoryLifecycleTests.SeedAsync(d, actor);
        await using var observer = d.Database.CreateContext();
        var snapshot = await observer.Set<IdentitySession>().AsNoTracking().SingleAsync(x => x.UserId == actor && x.RevokedAtUtc == null);
        var gate = new LockGate(1);
        await using var db = new Tpr10DbContext(new DbContextOptionsBuilder<Tpr10DbContext>().UseNpgsql(d.Database.ConnectionString).AddInterceptors(gate).Options);
        var current = new RequestSession { Entity = snapshot }; var permission = new PermissionContext();
        var service = new DirectoryService(db, current, new PermissionMutationGuard(db, current, permission, d.Clock), permission,
            new SessionService(db, d.Clock, current, new EffectiveRolePolicy(db)), AccountProvisioningTests.Audit(d, db), d.Clock);
        var pending = service.GrantHrAsync(new(f.Employee, f.Workspace, f.Department, "คำขอรอ lock"), default);
        string baseline = "";
        async Task<string> StateAsync() => JsonSerializer.Serialize(new
        {
            memberships = await observer.Set<EmployeeMembership>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            reporting = await observer.Set<ReportingLine>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            hr = await observer.Set<HrAssignment>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            users = await observer.Set<IdentityUser>().AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.IsActive, x.SecurityVersion }).ToArrayAsync(),
            sessions = await observer.Set<IdentitySession>().AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.RevokedAtUtc, x.SecurityVersion }).ToArrayAsync(),
            completed = await observer.AuditEvents.CountAsync(x => x.EventType == "attendance.directory.completed")
        });
        try
        {
            await gate.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (target == "actor")
            {
                using var logout = await d.PostAsync("/api/v1/auth/logout", new { });
                Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
                Assert.NotNull((await observer.Set<IdentitySession>().AsNoTracking().SingleAsync(x => x.Id == snapshot.Id)).RevokedAtUtc);
            }
            else
            {
                var path = target switch
                {
                    "employee" => $"/api/v1/users/{f.Employee}",
                    "department" => $"/api/v1/organization/workspaces/{f.Workspace}/departments/{f.Department}",
                    _ => $"/api/v1/organization/workspaces/{f.Workspace}"
                };
                object body = target == "employee" ? new { isActive = false } : new { name = "ปิดหน่วยงาน", isActive = false, expectedVersion = 1, reason = "ปิดก่อน grant ได้ lock" };
                using var closed = await AuthorizationTests.SendAsync(d, HttpMethod.Patch, path, body);
                Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
            }
            baseline = await StateAsync();
        }
        finally { gate.Resume.TrySetResult(); }
        Assert.Equal(expectedStatus, AccountProvisioningTests.Status(await pending));
        Assert.Equal(baseline, await StateAsync());
        Assert.False(await observer.Set<HrAssignment>().AnyAsync(x => x.UserId == f.Employee));
        Assert.Equal(1, await observer.AuditEvents.CountAsync(x => x.EventType == "attendance.directory.denied"));
    }

    private sealed class LockGate(int expectedArrivals = 2) : DbCommandInterceptor
    {
        private int arrivals;
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("pg_advisory_xact_lock(7241002)", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref arrivals) == expectedArrivals) Ready.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    [Fact]
    public async Task Options_are_bounded_literal_and_filtered_before_count()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await OperatorAsync(d);
        await using var db = d.Database.CreateContext();
        for (var i = 0; i < 105; i++) db.Add(new IdentityUser { Id = Guid.NewGuid(), Username = $"%literal{i:D3}", NormalizedUsername = $"%LITERAL{i:D3}", CreatedAtUtc = d.Clock.GetUtcNow() });
        db.Add(new IdentityUser { Id = Guid.NewGuid(), Username = "other", NormalizedUsername = "OTHER", CreatedAtUtc = d.Clock.GetUtcNow() });
        await db.SaveChangesAsync();
        using var response = await d.Client.GetAsync(Root + "/options/users?prefix=%25&pageSize=999");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(105, json.GetProperty("total").GetInt32());
        Assert.Equal(100, json.GetProperty("pageSize").GetInt32());
        Assert.Equal(100, json.GetProperty("items").GetArrayLength());
        using var next = await d.Client.GetAsync(Root + "/options/users?prefix=%25&pageSize=100&page=2");
        var second = await next.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(5, second.GetProperty("items").GetArrayLength());
        Assert.Equal("%literal100", second.GetProperty("items")[0].GetProperty("label").GetString());
        Assert.Equal(new[] { "id", "label" }, second.GetProperty("items")[0].EnumerateObject().Select(x => x.Name).OrderBy(x => x).ToArray());
    }

    [Theory]
    [InlineData("/memberships?userId=00000000-0000-0000-0000-000000000000", 400)]
    [InlineData("/memberships?page=0", 400)]
    [InlineData("/memberships?pageSize=0", 400)]
    [InlineData("/memberships?page=2147483647&pageSize=100", 400)]
    [InlineData("/options/departments", 400)]
    [InlineData("/options/departments?workspaceId=00000000-0000-0000-0000-000000000099", 404)]
    [InlineData("/options/users?prefix=%0A", 400)]
    public async Task Invalid_filters_and_missing_parent_fail_closed(string query, int status)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await OperatorAsync(d);
        using var response = await d.Client.GetAsync(Root + query);
        Assert.Equal(status, (int)response.StatusCode);
    }

    [Fact]
    public async Task Missing_csrf_and_inactive_parent_cannot_grant_hr()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await OperatorAsync(d);
        var f = await AttendanceDirectoryLifecycleTests.SeedAsync(d, actor);
        var body = new { userId = f.Employee, workspaceId = f.Workspace, departmentId = f.Department, reason = "มอบหมาย" };
        using var missing = await d.Client.PostAsJsonAsync(Root + "/hr-assignments", body);
        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        await using var db = d.Database.CreateContext();
        await db.Set<TPR10.Api.Organization.Data.Department>().Where(x => x.Id == f.Department).ExecuteUpdateAsync(x => x.SetProperty(u => u.IsActive, false));
        using var response = await d.PostAsync(Root + "/hr-assignments", body);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(await db.Set<HrAssignment>().AnyAsync(x => x.UserId == f.Employee));
    }
}
