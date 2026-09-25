using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Attendance.Access;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Data;
using TPR10.Api.Identity.Data;
using static TPR10.Api.IntegrationTests.AttendanceAccessTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class AttendanceRouteTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Head_request_goes_to_next_manager_and_another_hr_not_the_requester()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        var f = await AttendanceDirectoryLifecycleTests.SeedAsync(d, actor);
        var supervisorRole = await AttendanceIdentityTests.RoleAsync(d, "approval", 16);
        var hrRole = await AttendanceIdentityTests.RoleAsync(d, "accounting", 17);
        await using var setup = d.Database.CreateContext();
        var otherHr = Guid.NewGuid(); var now = d.Clock.GetUtcNow();
        setup.Add(new IdentityUser { Id = otherHr, Username = "other-hr", NormalizedUsername = "OTHER-HR", CreatedAtUtc = now });
        AddMembership(setup, actor, f, now.AddDays(-1));
        var head = await setup.Set<EmployeeMembership>().SingleAsync(x => x.UserId == f.Manager);
        setup.Add(new ReportingLine { Id = Guid.NewGuid(), EmployeeMembershipId = head.Id, EmployeeUserId = f.Manager, SupervisorUserId = actor, ValidFromUtc = head.ValidFromUtc, CreatedBy = actor, CreatedAtUtc = head.ValidFromUtc, Reason = "ผู้บังคับบัญชาถัดไป" });
        setup.Add(new UserRole { UserId = actor, RoleId = supervisorRole, CreatedAtUtc = now });
        setup.Add(new UserRole { UserId = f.Manager, RoleId = hrRole, CreatedAtUtc = now });
        setup.Add(new UserRole { UserId = otherHr, RoleId = hrRole, CreatedAtUtc = now });
        setup.Add(new HrAssignment { Id = Guid.NewGuid(), UserId = otherHr, WorkspaceId = f.Workspace, DepartmentId = f.Department, ValidFromUtc = now.AddDays(-1), CreatedBy = actor, CreatedAtUtc = now, Reason = "HRคนอื่น" });
        await setup.SaveChangesAsync();
        await using var scope = d.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>();
        await SetSessionAsync(scope.ServiceProvider, actor);
        var resolver = scope.ServiceProvider.GetRequiredService<IAttendanceRouteResolver>();
        var subject = Snapshot(head, now.AddHours(-1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(subject, default));
        await using var tx = await db.Database.BeginTransactionAsync(); await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        var route = await resolver.ResolveAsync(subject, default);
        Assert.Null(route.Status); Assert.Equal(actor, route.SupervisorId); Assert.Equal(new[] { otherHr }, route.HrCandidateIds);
    }

    [Fact]
    public async Task A_moved_employee_cannot_silently_route_the_old_unit_request_to_the_new_unit()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        var f = await AttendanceDirectoryLifecycleTests.SeedAsync(d, actor);
        await using var setup = d.Database.CreateContext(); var now = d.Clock.GetUtcNow();
        var old = await setup.Set<EmployeeMembership>().SingleAsync(x => x.UserId == f.Employee);
        var subject = Snapshot(old, now.AddHours(-1)); old.ValidToUtc = now.AddMinutes(-1); old.EndedBy = actor; old.Version++;
        var dep = Guid.NewGuid(); setup.Add(new TPR10.Api.Organization.Data.Department { Id = dep, WorkspaceId = f.Workspace, Code = "NEW", Name = "ใหม่", CreatedBy = actor, CreatedAtUtc = now });
        await setup.SaveChangesAsync();
        var next = AddMembership(setup, f.Employee, f with { Department = dep }, now.AddMinutes(-1)); next.Version = 3;
        await setup.SaveChangesAsync();
        await using var scope = d.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>(); await SetSessionAsync(scope.ServiceProvider, actor);
        await using var tx = await db.Database.BeginTransactionAsync(); await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        var route = await scope.ServiceProvider.GetRequiredService<IAttendanceRouteResolver>().ResolveAsync(subject, default);
        Assert.Equal(409, route.Status); Assert.Empty(route.HrCandidateIds); Assert.Null(route.SupervisorId);
    }

    [Theory]
    [InlineData("complete", true)]
    [InlineData("no-supervisor-cap", false)]
    [InlineData("no-hr-cap", false)]
    [InlineData("hr-is-supervisor", false)]
    [InlineData("hr-is-requester", false)]
    [InlineData("expired-line", false)]
    [InlineData("inactive-manager", false)]
    public async Task Route_requires_two_distinct_current_eligible_approvers(string scenario, bool allowed)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        var f = await AttendanceDirectoryLifecycleTests.SeedAsync(d, actor);
        await using var setup = d.Database.CreateContext();
        var hr = await setup.Set<HrAssignment>().SingleAsync();
        hr.UserId = scenario == "hr-is-supervisor" ? f.Manager : scenario == "hr-is-requester" ? f.Employee : actor;
        var managerRole = await AttendanceIdentityTests.RoleAsync(d, "approval", scenario == "no-supervisor-cap" ? [] : [16]);
        var hrRole = await AttendanceIdentityTests.RoleAsync(d, "accounting", scenario == "no-hr-cap" ? [] : [17]);
        setup.Add(new UserRole { UserId = f.Manager, RoleId = managerRole, CreatedAtUtc = d.Clock.GetUtcNow() });
        setup.Add(new UserRole { UserId = hr.UserId, RoleId = hrRole, CreatedAtUtc = d.Clock.GetUtcNow() });
        if (scenario == "expired-line") { var line = await setup.Set<ReportingLine>().SingleAsync(); line.ValidToUtc = d.Clock.GetUtcNow(); line.EndedBy = actor; line.Version++; }
        if (scenario == "inactive-manager") (await setup.Set<IdentityUser>().SingleAsync(x => x.Id == f.Manager)).IsActive = false;
        await setup.SaveChangesAsync();
        var employee = await setup.Set<EmployeeMembership>().SingleAsync(x => x.UserId == f.Employee);
        await using var scope = d.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>(); await SetSessionAsync(scope.ServiceProvider, actor);
        await using var tx = await db.Database.BeginTransactionAsync(); await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        var route = await scope.ServiceProvider.GetRequiredService<IAttendanceRouteResolver>().ResolveAsync(Snapshot(employee, d.Clock.GetUtcNow().AddHours(-1)), default);
        Assert.Equal(allowed ? null : 409, route.Status);
        if (allowed)
        {
            Assert.Equal(f.Manager, route.SupervisorId); Assert.Equal(new[] { actor }, route.HrCandidateIds);
            Assert.Equal(f.Workspace, route.WorkspaceId); Assert.Equal(f.Department, route.DepartmentId);
            Assert.Equal(1, route.MembershipVersion); Assert.Equal(1, route.ReportingVersion);
            // The supervisor has no live session/MFA: routing is not an authorization to approve.
            Assert.False(await db.Set<IdentitySession>().AnyAsync(x => x.UserId == f.Manager));
        }
        else { Assert.Null(route.SupervisorId); Assert.Empty(route.HrCandidateIds); }
    }
}
