using System.Net;
using System.Runtime.Versioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Attendance.Access;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Attendance.Storage;
using static TPR10.Api.IntegrationTests.StorageTestFixture;

namespace TPR10.Api.IntegrationTests;

[Collection("database"), UnsupportedOSPlatform("windows")]
public sealed class StoragePinTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Employee_reservation_is_durable_idempotent_and_keeps_original_target_after_switch()
    {
        using var f = new StorageTestFixture();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, f.Settings);
        var actor = await OperatorAsync(d);
        var a = await ProbeAsync(d, await RegisterAsync(d));
        var b = await ProbeAsync(d, await RegisterAsync(d, "local-1"));
        await Switch(d, a.Id, 1);
        var subject = await Employee(d, actor);
        var op = Guid.NewGuid(); var stamp = new StampRequest(subject.OccurredAtUtc, EvidenceAction.CheckIn);
        var first = await Pin(d, subject.EmployeeId, op, subject, stamp);
        Assert.Equal(a.Id, first.StorageId);
        Assert.Equal(a.Version, first.StorageVersion);
        await using (var db = d.Database.CreateContext())
        {
            var saved = await db.Set<EvidenceObject>().SingleAsync();
            Assert.Equal(subject.MembershipId, saved.MembershipId);
            Assert.Equal(subject.EmployeeId, saved.OwnerId);
            Assert.Equal(EvidenceState.Reserved, saved.State);
            Assert.Equal(stamp.OccurredAtUtc, saved.OccurredAtUtc);
            Assert.Equal(first.ObjectKey, saved.ObjectKey);
            Assert.Empty(System.IO.Directory.GetFiles(Path.Combine(f.Root, "volume-0"), "*.jpg", SearchOption.AllDirectories));
        }
        await Switch(d, b.Id, 2);
        Assert.Equal(first, await Pin(d, subject.EmployeeId, op, subject, stamp));
        var conflict = await Assert.ThrowsAsync<StorageOperationException>(() => Pin(d, subject.EmployeeId, op, subject, stamp with { Action = EvidenceAction.CheckOut }));
        Assert.Equal(409, conflict.Status);
        Assert.Equal(b.Id, (await Pin(d, subject.EmployeeId, Guid.NewGuid(), subject, stamp)).StorageId);
        await using var check = d.Database.CreateContext();
        Assert.Equal(2, await check.Set<EvidenceObject>().CountAsync());
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("membership")]
    [InlineData("stamp")]
    [InlineData("future")]
    [InlineData("action")]
    [InlineData("empty-operation")]
    [InlineData("expired-readiness")]
    [InlineData("sealed")]
    [InlineData("no-target")]
    public async Task Invalid_pin_has_no_reservation_or_fallback(string fault)
    {
        using var f = new StorageTestFixture();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, f.Settings);
        var actor = await OperatorAsync(d);
        var a = await ProbeAsync(d, await RegisterAsync(d));
        if (fault != "no-target") await Switch(d, a.Id, 1);
        var subject = await Employee(d, actor); var caller = subject.EmployeeId;
        var stamp = new StampRequest(subject.OccurredAtUtc, EvidenceAction.CheckIn); var op = Guid.NewGuid();
        if (fault == "owner") subject = subject with { EmployeeId = actor };
        if (fault == "membership") subject = subject with { MembershipId = Guid.NewGuid() };
        if (fault == "stamp") stamp = stamp with { OccurredAtUtc = stamp.OccurredAtUtc.AddSeconds(-1) };
        if (fault == "future") { subject = subject with { OccurredAtUtc = subject.OccurredAtUtc.AddHours(1) }; stamp = stamp with { OccurredAtUtc = subject.OccurredAtUtc }; }
        if (fault == "action") stamp = stamp with { Action = (EvidenceAction)99 };
        if (fault == "empty-operation") op = Guid.Empty;
        if (fault == "expired-readiness") d.Advance(TimeSpan.FromSeconds(60));
        if (fault == "sealed")
        {
            await using var setup = d.Database.CreateContext();
            var storage = await setup.Set<StorageLocation>().SingleAsync(); storage.AcceptWrites = false; storage.Version++; await setup.SaveChangesAsync();
        }
        var error = await Record.ExceptionAsync(() => Pin(d, caller, op, subject, stamp));
        Assert.NotNull(error);
        Assert.Equal("StorageOperationException", error.GetType().Name);
        await using var db = d.Database.CreateContext();
        Assert.Empty(await db.Set<EvidenceObject>().ToArrayAsync());
    }

    internal static async Task<EmploymentSnapshot> Employee(IdentityTestDriver d, Guid actor)
    {
        var org = await AttendanceDirectoryLifecycleTests.SeedAsync(d, actor);
        const string password = "Employee-Test-Only-837!";
        var employee = await d.SeedUserAsync("pin-employee", password, []);
        await using var db = d.Database.CreateContext();
        var member = AttendanceAccessTests.AddMembership(db, employee, org, d.Clock.GetUtcNow().AddDays(-1));
        await db.SaveChangesAsync();
        using var client = d.NewClient();
        using var login = IdentityTestDriver.Mutation(await IdentityTestDriver.TokenAsync(client), "/api/v1/auth/login");
        login.Content = System.Net.Http.Json.JsonContent.Create(new { username = "pin-employee", password });
        using var response = await client.SendAsync(login);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return AttendanceAccessTests.Snapshot(member, d.Clock.GetUtcNow());
    }

    internal static async Task Switch(IdentityTestDriver d, Guid id, long version)
    {
        using var response = await d.PostAsync(ApiRoot + "/write-target", new { storageId = id, expectedVersion = version, reason = "ทดสอบการตรึงปลายทาง" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    internal static async Task<EvidenceReservation> Pin(IdentityTestDriver d, Guid actor, Guid op, EmploymentSnapshot subject, StampRequest stamp)
    {
        using var scope = d.Factory.Services.CreateScope();
        await AttendanceAccessTests.SetSessionAsync(scope.ServiceProvider, actor);
        return await scope.ServiceProvider.GetRequiredService<StorageRegistry>().PinAsync(op, subject, stamp, CancellationToken.None);
    }
}
