using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Attendance.Recording;
using TPR10.Api.Identity;
using TPR10.Api.Identity.Data;

namespace TPR10.Api.IntegrationTests;

internal sealed class AttendanceHistoryFixture(CaptureFixture capture) : IAsyncDisposable
{
    internal CaptureFixture Capture { get; } = capture;
    internal static readonly Guid Manager = Guid.Parse("00000000-0000-0000-0000-000000000032");
    internal static readonly Guid Stranger = Guid.Parse("00000000-0000-0000-0000-000000000033");
    internal static readonly Guid ManagerSession = Guid.Parse("00000000-0000-0000-0000-000000000072");
    internal static readonly DateOnly From = new(2026, 9, 1), To = new(2026, 9, 26);
    internal static async Task<AttendanceHistoryFixture> CreateAsync(string connection, string mode = "both", Action<IServiceCollection>? configure = null)
    {
        var capture = await CaptureFixture.CreateAsync(connection, s =>
        {
            s.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider()); configure?.Invoke(s);
        });
        var f = new AttendanceHistoryFixture(capture);
        await using var db = capture.Data.Database.CreateContext(); var now = capture.Clock.GetUtcNow();
        db.Add(new IdentityUser { Id = Stranger, Username = "stranger", NormalizedUsername = "STRANGER", SecurityVersion = 1 });
        var old = await db.Set<EmployeeMembership>().SingleAsync();
        db.Add(new EmployeeMembership
        {
            Id = Guid.NewGuid(),
            UserId = Manager,
            WorkspaceId = old.WorkspaceId,
            DepartmentId = old.DepartmentId,
            ValidFromUtc = old.ValidFromUtc,
            CreatedBy = Manager,
            Reason = "ทดสอบ"
        });
        db.Add(new EmployeeMembership
        {
            Id = Guid.NewGuid(),
            UserId = Stranger,
            WorkspaceId = Guid.Parse("00000000-0000-0000-0000-000000000002"),
            DepartmentId = Guid.Parse("00000000-0000-0000-0000-000000000012"),
            ValidFromUtc = old.ValidFromUtc,
            CreatedBy = Manager,
            Reason = "ทดสอบ"
        });
        db.Add(new IdentitySession
        {
            Id = ManagerSession,
            UserId = Manager,
            TokenHash = Enumerable.Repeat((byte)19, 32).ToArray(),
            Stage = SessionStage.Active,
            SecurityVersion = 1,
            CreatedAtUtc = now.AddHours(-1),
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddDays(1),
            MfaVerifiedAtUtc = now
        });
        db.Add(new MfaFactor { Id = Guid.NewGuid(), UserId = Manager, ProtectedSecret = "synthetic-test-factor", CreatedAtUtc = now.AddDays(-1), ConfirmedAtUtc = now.AddDays(-1) });
        var role = Guid.NewGuid();
        db.Add(new IdentityRole { Id = role, Name = "History test " + mode, RoleClass = mode == "admin" ? "system-administration" : "approval" });
        db.Add(new UserRole { UserId = Manager, RoleId = role });
        foreach (var number in mode switch { "hr" => new[] { 15 }, "team" => new[] { 14 }, "admin" => Array.Empty<int>(), _ => new[] { 14, 15 } })
            db.Add(new RolePermission { RoleId = role, PermissionId = AttendanceIdentityTests.Permission(number) });
        if (mode is "team" or "both") db.Add(new ReportingLine
        {
            Id = Guid.NewGuid(),
            EmployeeMembershipId = old.Id,
            EmployeeUserId = old.UserId,
            SupervisorUserId = Manager,
            ValidFromUtc = old.ValidFromUtc,
            CreatedBy = Manager,
            Reason = "ทดสอบ"
        });
        if (mode is "hr" or "both") db.Add(new HrAssignment
        {
            Id = Guid.NewGuid(),
            UserId = Manager,
            WorkspaceId = old.WorkspaceId,
            DepartmentId = old.DepartmentId,
            ValidFromUtc = old.ValidFromUtc,
            CreatedBy = Manager,
            Reason = "ทดสอบ"
        });
        await db.SaveChangesAsync(); return f;
    }
    internal async Task<Guid> Day(Guid actor, int day, bool closed = true)
    {
        var at = DateTimeOffset.Parse($"2026-09-{day:00}T12:00:00Z");
        await using var db = Capture.Data.Database.CreateContext();
        var m = await db.Set<EmployeeMembership>().SingleAsync(x => x.UserId == actor && x.ValidFromUtc <= at && (x.ValidToUtc == null || x.ValidToUtc > at));
        var row = new AttendanceDay
        {
            Id = Guid.NewGuid(),
            EmployeeId = actor,
            WorkDate = new(2026, 9, day),
            MembershipId = m.Id,
            WorkspaceId = m.WorkspaceId,
            DepartmentId = m.DepartmentId,
            SnapshotAtUtc = at,
            EffectiveInUtc = at,
            EffectiveOutUtc = closed ? at.AddHours(1) : null,
            Version = closed ? 2 : 1
        };
        db.Add(row);
        foreach (var action in closed ? new[] { EvidenceAction.CheckIn, EvidenceAction.CheckOut } : new[] { EvidenceAction.CheckIn })
        {
            var time = action == EvidenceAction.CheckIn ? at : at.AddHours(1); var id = Guid.NewGuid();
            db.Add(new EvidenceObject
            {
                Id = id,
                OperationId = Guid.NewGuid(),
                OwnerId = actor,
                MembershipId = m.Id,
                WorkspaceId = m.WorkspaceId,
                DepartmentId = m.DepartmentId,
                SnapshotAtUtc = time,
                OccurredAtUtc = time,
                Action = action,
                StorageId = Guid.Parse("00000000-0000-0000-0000-000000000061"),
                StorageVersion = 1,
                ObjectKey = "objects/" + id.ToString("N")[..2] + "/" + id.ToString("N")
            });
            db.Add(new AttendanceEvent
            {
                Id = Guid.NewGuid(),
                DayId = row.Id,
                ActorId = actor,
                Action = action,
                OccurredAtUtc = time,
                DeviceCapturedAtUtc = time,
                WorkspaceId = CaptureFixture.Site.WorkspaceId,
                ProjectId = CaptureFixture.Site.ProjectId!.Value,
                SiteId = CaptureFixture.Site.SiteId!.Value,
                Latitude = 13,
                Longitude = 100,
                AccuracyMeters = 10,
                GpsSampledAtUtc = time,
                EvidenceId = id
            });
            // Preserve insertion ordering required by the existing checkout trigger.
            await db.SaveChangesAsync();
        }
        return row.Id;
    }
    internal static Type Type(string name)
    {
        var type = typeof(AttendanceDay).Assembly.GetType("TPR10.Api.Attendance.History." + name);
        Assert.NotNull(type); return type;
    }
    internal async Task<(int Status, JsonElement Value)> Call(string method, Guid? session = null, params object?[] args)
    {
        var type = Type("AttendanceHistory");
        await using var scope = await Capture.ScopeAsync(session ?? ManagerSession);
        var task = (Task)type.GetMethod(method)!.Invoke(scope.ServiceProvider.GetRequiredService(type), [.. args, CancellationToken.None])!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var status = (int)result.GetType().GetProperty("Status")!.GetValue(result)!;
        var value = result.GetType().GetProperty("Value")!.GetValue(result);
        return (status, JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
    internal Task<(int Status, JsonElement Value)> List(string audience, int size = 20, string? cursor = null, Guid? session = null,
        DateOnly? from = null, DateOnly? to = null) => Call("ListAsync", session, Enum.Parse(Type("AttendanceAudience"), audience),
            Activator.CreateInstance(Type("AttendanceListQuery"), from ?? From, to ?? To, size, cursor));
    public ValueTask DisposeAsync() => Capture.DisposeAsync();
}
