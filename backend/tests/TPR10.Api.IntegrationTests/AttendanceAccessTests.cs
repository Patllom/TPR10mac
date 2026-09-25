using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Attendance.Access;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Data;
using TPR10.Api.Identity;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Organization.Data;
using TPR10.Api.Scopes.Data;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class AttendanceAccessTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Access_endpoint_is_actor_only_hint_and_requires_authentication()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        using var anonymous = await d.Client.GetAsync("/api/v1/attendance/access");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var actor = await AttendanceDirectoryApiTests.OperatorAsync(d);
        var f = await AttendanceDirectoryLifecycleTests.SeedAsync(d, actor);
        var role = await AttendanceIdentityTests.RoleAsync(d, "approval", 14, 15, 16, 17);
        await using var setup = d.Database.CreateContext();
        AddMembership(setup, actor, f, d.Clock.GetUtcNow().AddDays(-1));
        (await setup.Set<ReportingLine>().SingleAsync()).SupervisorUserId = actor;
        (await setup.Set<HrAssignment>().SingleAsync()).UserId = actor;
        setup.Add(new UserRole { UserId = actor, RoleId = role, CreatedAtUtc = d.Clock.GetUtcNow() });
        await setup.SaveChangesAsync();
        var hint = await d.Client.GetFromJsonAsync<AttendanceAccessView>("/api/v1/attendance/access");
        Assert.Equal(new AttendanceAccessView(false, true, true, true, true, true), hint);
        var json = await d.Client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/attendance/access");
        Assert.Equal(6, json.EnumerateObject().Count());
        Assert.All(json.EnumerateObject(), x => Assert.Contains(x.Value.ValueKind, new[] { System.Text.Json.JsonValueKind.True, System.Text.Json.JsonValueKind.False }));
    }

    [Theory]
    [InlineData("returned", 404)]
    [InlineData("transitive", 404)]
    [InlineData("expired-mfa", 403)]
    [InlineData("revoked-session", 401)]
    public async Task Current_relationship_does_not_expose_prior_membership_or_transitive_records(string scenario, int status)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        var f = await AttendanceDirectoryLifecycleTests.SeedAsync(d, actor);
        var role = await AttendanceIdentityTests.RoleAsync(d, "approval", 14);
        await using var setup = d.Database.CreateContext();
        AddMembership(setup, actor, f, d.Clock.GetUtcNow().AddDays(-1));
        setup.Add(new UserRole { UserId = actor, RoleId = role, CreatedAtUtc = d.Clock.GetUtcNow() });
        var original = await setup.Set<EmployeeMembership>().SingleAsync(x => x.UserId == f.Employee);
        var subject = Snapshot(original, d.Clock.GetUtcNow().AddHours(-1));
        var line = await setup.Set<ReportingLine>().SingleAsync();
        if (scenario != "transitive") line.SupervisorUserId = actor;
        else
        {
            var manager = await setup.Set<EmployeeMembership>().SingleAsync(x => x.UserId == f.Manager);
            setup.Add(new ReportingLine { Id = Guid.NewGuid(), EmployeeMembershipId = manager.Id, EmployeeUserId = f.Manager, SupervisorUserId = actor, ValidFromUtc = manager.ValidFromUtc, CreatedBy = actor, CreatedAtUtc = manager.ValidFromUtc, Reason = "ระดับถัดไป" });
        }
        if (scenario == "returned")
        {
            var end = d.Clock.GetUtcNow().AddMinutes(-1);
            original.ValidToUtc = end; original.EndedBy = actor; original.Version++;
            line.ValidToUtc = end; line.EndedBy = actor; line.Version++;
            await setup.SaveChangesAsync();
            var next = AddMembership(setup, f.Employee, f, end); next.Version = 3;
            setup.Add(new ReportingLine { Id = Guid.NewGuid(), EmployeeMembershipId = next.Id, EmployeeUserId = f.Employee, SupervisorUserId = actor, ValidFromUtc = end, CreatedBy = actor, CreatedAtUtc = end, Reason = "กลับหน่วยเดิม", Version = 3 });
        }
        await setup.SaveChangesAsync();
        await using var scope = d.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>(); await SetSessionAsync(scope.ServiceProvider, actor);
        if (scenario == "expired-mfa") d.Advance(TimeSpan.FromMinutes(15));
        if (scenario == "revoked-session") await setup.Set<IdentitySession>().Where(x => x.UserId == actor).ExecuteUpdateAsync(x => x.SetProperty(s => s.RevokedAtUtc, d.Clock.GetUtcNow()));
        await using var tx = await db.Database.BeginTransactionAsync(); await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        var access = scope.ServiceProvider.GetRequiredService<IAttendanceAccess>();
        Assert.Equal(new AttendanceReadDecision(false, false, false, AttendanceReadBasis.None, status), await access.ReadAsync(subject, default));
        if (scenario == "returned")
        {
            var current = await access.CurrentEmploymentAsync(f.Employee, d.Clock.GetUtcNow(), default);
            Assert.NotNull(current); Assert.NotEqual(subject.MembershipId, current.MembershipId);
            Assert.Equal(new AttendanceReadDecision(true, true, false, AttendanceReadBasis.Supervisor, null), await access.ReadAsync(current, default));
        }
    }

    [Fact]
    public async Task Ordinary_staff_can_read_own_data_and_record_hint_needs_exact_site_assignment()
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var f = await ScopeFixture.CreateAsync(d);
        await d.LoginAsync("scope-user", MfaTests.Password);
        var employee = await AttendanceDirectoryLifecycleTests.SeedAsync(d, f.UserId);
        await using var setup = d.Database.CreateContext();
        AddMembership(setup, f.UserId, employee, d.Clock.GetUtcNow().AddDays(-1)); await setup.SaveChangesAsync();
        await using var scope = d.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>(); await SetSessionAsync(scope.ServiceProvider, f.UserId);
        var access = scope.ServiceProvider.GetRequiredService<IAttendanceAccess>();
        await f.GrantAsync(f.Workspace, "staff", "attendance:record");
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
            Assert.False((await access.DescribeAsync(default)).CanRecord);
            var subject = await access.CurrentEmploymentAsync(f.UserId, d.Clock.GetUtcNow(), default);
            Assert.NotNull(subject);
            Assert.Equal(new AttendanceReadDecision(true, true, true, AttendanceReadBasis.Own, null), await access.ReadAsync(subject, default));
            await tx.CommitAsync();
        }
        await f.GrantAsync(f.Site, "staff", "attendance:record");
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
            Assert.True((await access.DescribeAsync(default)).CanRecord);
        }
    }

    [Theory]
    [InlineData("own", true, true, AttendanceReadBasis.Own)]
    [InlineData("supervisor", true, false, AttendanceReadBasis.Supervisor)]
    [InlineData("hr", true, true, AttendanceReadBasis.Hr)]
    [InlineData("both", true, true, AttendanceReadBasis.Hr)]
    [InlineData("admin", false, false, AttendanceReadBasis.None)]
    [InlineData("wrong-unit", false, false, AttendanceReadBasis.None)]
    [InlineData("scope-only", false, false, AttendanceReadBasis.None)]
    [InlineData("staff-class", false, false, AttendanceReadBasis.None)]
    [InlineData("system-class", false, false, AttendanceReadBasis.None)]
    [InlineData("expired-line", false, false, AttendanceReadBasis.None)]
    [InlineData("expired-hr", false, false, AttendanceReadBasis.None)]
    [InlineData("inactive-unit", false, false, AttendanceReadBasis.None)]
    [InlineData("revoked-grant", false, false, AttendanceReadBasis.None)]
    public async Task Read_uses_real_snapshot_current_relations_and_explicit_basis(string scenario, bool read, bool photo, AttendanceReadBasis basis)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        var f = await AttendanceDirectoryLifecycleTests.SeedAsync(d, actor);
        await using var setup = d.Database.CreateContext();
        var now = d.Clock.GetUtcNow();
        var actorMembership = AddMembership(setup, actor, f, now.AddDays(-1));
        if (scenario is "supervisor" or "both" or "scope-only" or "expired-line" or "staff-class" or "system-class" or "revoked-grant")
        {
            var line = await setup.Set<ReportingLine>().SingleAsync(); line.SupervisorUserId = actor;
            if (scenario == "expired-line") { line.ValidToUtc = now; line.EndedBy = actor; line.Version++; }
        }
        if (scenario is "hr" or "both" or "wrong-unit" or "expired-hr" or "inactive-unit")
        {
            var hr = await setup.Set<HrAssignment>().SingleAsync(); hr.UserId = actor;
            if (scenario == "expired-hr") { hr.ValidToUtc = now; hr.EndedBy = actor; hr.Version++; }
            if (scenario == "wrong-unit")
            {
                var dep = Guid.NewGuid(); setup.Add(new Department { Id = dep, WorkspaceId = f.Workspace, Code = "OTHER", Name = "อื่น", CreatedBy = actor, CreatedAtUtc = now }); hr.DepartmentId = dep;
            }
            if (scenario == "inactive-unit") (await setup.Set<Department>().SingleAsync()).IsActive = false;
        }
        await setup.SaveChangesAsync();
        if (scenario != "admin")
        {
            var role = await AttendanceIdentityTests.RoleAsync(d, scenario == "staff-class" ? "staff" : scenario == "system-class" ? "system-administration" : "approval", 14, 15);
            if (scenario == "scope-only") setup.Add(new ScopeAssignment { Id = Guid.NewGuid(), UserId = actor, RoleId = role, WorkspaceId = f.Workspace, CreatedBy = actor, CreatedAtUtc = now, Reason = "เฉพาะพื้นที่" });
            else if (scenario != "revoked-grant") setup.Add(new UserRole { UserId = actor, RoleId = role, CreatedAtUtc = now });
            await setup.SaveChangesAsync();
        }
        var membership = scenario == "own" ? actorMembership : await setup.Set<EmployeeMembership>().SingleAsync(x => x.UserId == f.Employee);
        var subject = Snapshot(membership, now.AddHours(-1));
        await using var scope = d.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>();
        await SetSessionAsync(scope.ServiceProvider, actor);
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        var decision = await scope.ServiceProvider.GetRequiredService<IAttendanceAccess>().ReadAsync(subject, default);
        Assert.Equal(new AttendanceReadDecision(read, read, photo, basis, read ? null : 404), decision);
        await tx.RollbackAsync();
        var view = await d.Client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/auth/session");
        var permissions = view.GetProperty("permissions").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.DoesNotContain("attendance:team-read", permissions);
        Assert.DoesNotContain("attendance:hr-read", permissions);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("membership")]
    [InlineData("unit")]
    [InlineData("time")]
    public async Task Forged_snapshot_cannot_use_own_record_shortcut(string change)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        var f = await AttendanceDirectoryLifecycleTests.SeedAsync(d, actor);
        await using var setup = d.Database.CreateContext();
        var row = await setup.Set<EmployeeMembership>().SingleAsync(x => x.UserId == f.Employee);
        var subject = Snapshot(row, d.Clock.GetUtcNow().AddHours(-1));
        subject = change switch { "owner" => subject with { EmployeeId = actor }, "membership" => subject with { MembershipId = Guid.NewGuid() }, "unit" => subject with { DepartmentId = Guid.NewGuid() }, _ => subject with { OccurredAtUtc = row.ValidFromUtc.AddTicks(-1) } };
        await using var scope = d.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>(); await SetSessionAsync(scope.ServiceProvider, actor);
        await using var tx = await db.Database.BeginTransactionAsync(); await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        Assert.Equal(new AttendanceReadDecision(false, false, false, AttendanceReadBasis.None, 404), await scope.ServiceProvider.GetRequiredService<IAttendanceAccess>().ReadAsync(subject, default));
    }

    [Fact]
    public async Task No_transaction_is_rejected_and_revoked_session_is_reloaded()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        await using var scope = d.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>(); await SetSessionAsync(scope.ServiceProvider, actor);
        var access = scope.ServiceProvider.GetRequiredService<IAttendanceAccess>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => access.DescribeAsync(default));
        await using var tx = await db.Database.BeginTransactionAsync(); await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        await db.Set<IdentitySession>().Where(x => x.UserId == actor).ExecuteUpdateAsync(x => x.SetProperty(s => s.RevokedAtUtc, d.Clock.GetUtcNow()));
        var decision = await access.ReadAsync(new(Guid.NewGuid(), actor, Guid.NewGuid(), Guid.NewGuid(), d.Clock.GetUtcNow()), default);
        Assert.Equal(401, decision.Status);
        Assert.Equal(new AttendanceAccessView(false, false, false, false, false, false), await access.DescribeAsync(default));
    }

    internal static EmployeeMembership AddMembership(Tpr10DbContext db, Guid user, AttendanceDirectoryLifecycleTests.Fixture f, DateTimeOffset from)
    {
        var row = new EmployeeMembership { Id = Guid.NewGuid(), UserId = user, WorkspaceId = f.Workspace, DepartmentId = f.Department, ValidFromUtc = from, CreatedBy = user, CreatedAtUtc = from, Reason = "ทดสอบ" };
        db.Add(row); return row;
    }
    internal static EmploymentSnapshot Snapshot(EmployeeMembership x, DateTimeOffset at) => new(x.Id, x.UserId, x.WorkspaceId, x.DepartmentId, at);
    internal static async Task SetSessionAsync(IServiceProvider services, Guid actor)
    {
        var db = services.GetRequiredService<Tpr10DbContext>();
        services.GetRequiredService<RequestSession>().Entity = await db.Set<IdentitySession>().AsNoTracking().SingleAsync(x => x.UserId == actor && x.RevokedAtUtc == null);
    }
}
