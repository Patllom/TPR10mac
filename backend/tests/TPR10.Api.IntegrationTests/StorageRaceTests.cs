using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TPR10.Api.Attendance.Storage;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Identity.Data;
using TPR10.Api.Scopes;
using static TPR10.Api.IntegrationTests.StorageTestFixture;

namespace TPR10.Api.IntegrationTests;

[Collection("database"), UnsupportedOSPlatform("windows")]
public sealed class StorageRaceTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Audit_failure_rolls_back_probe_and_employee_reservation()
    {
        using var f = new StorageTestFixture();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, f.Settings);
        var actor = await OperatorAsync(d); var row = await ProbeAsync(d, await RegisterAsync(d));
        await StoragePinTests.Switch(d, row.Id, 1);
        var subject = await StoragePinTests.Employee(d, actor);
        await using var db = d.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION reject_storage_audits() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.event_type IN ('attendance.storage.probe','attendance.evidence.pin') THEN RAISE EXCEPTION 'audit fault'; END IF; RETURN NEW; END $$; CREATE TRIGGER reject_storage_audits BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION reject_storage_audits();");
        using var probe = await d.PostAsync(ApiRoot + $"/locations/{row.Id}/probe", new { expectedVersion = row.Version, reason = "ตรวจ rollback" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, probe.StatusCode);
        Assert.Equal(row.Version, (await db.Set<StorageLocation>().AsNoTracking().SingleAsync()).Version);
        var error = await Assert.ThrowsAsync<StorageOperationException>(() => StoragePinTests.Pin(d, subject.EmployeeId, Guid.NewGuid(), subject, new(subject.OccurredAtUtc, EvidenceAction.CheckIn)));
        Assert.Equal(503, error.Status);
        Assert.Empty(await db.Set<EvidenceObject>().ToArrayAsync());
    }

    [Fact]
    public async Task Configuration_drift_under_same_alias_requires_new_registration_not_rebinding_old_storage()
    {
        using var f = new StorageTestFixture();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, f.Settings);
        await OperatorAsync(d); var row = await ProbeAsync(d, await RegisterAsync(d));
        var monitor = d.Factory.Services.GetRequiredService<IOptionsMonitor<StorageOptions>>();
        monitor.CurrentValue.Locations[0] = monitor.CurrentValue.Locations[0] with { RootPath = Path.Combine(f.Root, "volume-1") };
        using var response = await d.PostAsync(ApiRoot + "/write-target", new { storageId = row.Id, expectedVersion = 1, reason = "เปลี่ยน root ใต้ alias เดิม" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await using var db = d.Database.CreateContext();
        Assert.Empty(await db.Set<StorageWriteTarget>().ToArrayAsync());
        Assert.Equal(row.Version, (await db.Set<StorageLocation>().SingleAsync()).Version);
        var health = Assert.Single((await d.Client.GetFromJsonAsync<TPR10.Api.Attendance.Storage.Page<StorageHealthView>>(ApiRoot + "/health"))!.Items);
        Assert.Equal("storage-config-changed", health.ErrorCode);
    }

    [Theory]
    [InlineData("grant", 403)]
    [InlineData("session", 401)]
    [InlineData("config", 503)]
    public async Task Probe_does_not_hold_identity_lock_and_revalidates_after_native_io(string change, int expected)
    {
        using var f = new StorageTestFixture();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, f.Settings);
        var actor = await OperatorAsync(d); var row = await RegisterAsync(d);
        var monitor = d.Factory.Services.GetRequiredService<IOptionsMonitor<StorageOptions>>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new StorageRuntime(monitor, d.Clock, (id, def) => new PausingAdapter(new FolderStorageAdapter(id, def, d.Clock), entered, release));
        using var scope = d.Factory.Services.CreateScope();
        await AttendanceAccessTests.SetSessionAsync(scope.ServiceProvider, actor);
        var service = ActivatorUtilities.CreateInstance<StorageRegistry>(scope.ServiceProvider, runtime);
        var pending = service.ProbeAsync(row.Id, new(row.Version, "ทดสอบถอนสิทธิ์ระหว่างตรวจ"), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await using var db = d.Database.CreateContext();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await using var tx = await ScopeOperation.BeginAsync(db, deadline.Token);
            if (change == "grant") await db.Set<RolePermission>().Where(x => x.PermissionId == AttendanceIdentityTests.Permission(19)).ExecuteDeleteAsync(deadline.Token);
            if (change == "session") await db.Set<IdentitySession>().Where(x => x.UserId == actor).ExecuteUpdateAsync(x => x.SetProperty(s => s.RevokedAtUtc, d.Clock.GetUtcNow()), deadline.Token);
            if (change == "config") monitor.CurrentValue.Locations[0] = monitor.CurrentValue.Locations[0] with { RootPath = Path.Combine(f.Root, "volume-1") };
            await tx.CommitAsync(deadline.Token);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(expected, ((IStatusCodeHttpResult)await pending).StatusCode);
        await using var check = d.Database.CreateContext();
        var current = await check.Set<StorageLocation>().SingleAsync();
        Assert.Equal(change == "config" ? 2 : 1, current.Version);
        Assert.NotEqual("ready", current.Health);
    }

    [Fact]
    public async Task Switch_audit_failure_rolls_back_target_and_version()
    {
        using var f = new StorageTestFixture();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, f.Settings);
        await OperatorAsync(d); var row = await ProbeAsync(d, await RegisterAsync(d));
        await using var db = d.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION reject_storage_switch() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.event_type='attendance.storage.switch' THEN RAISE EXCEPTION 'audit fault'; END IF; RETURN NEW; END $$; CREATE TRIGGER reject_storage_switch BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION reject_storage_switch();");
        using var response = await d.PostAsync(ApiRoot + "/write-target", new { storageId = row.Id, expectedVersion = 1, reason = "ตรวจ atomic audit" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(await db.Set<StorageWriteTarget>().ToArrayAsync());
        Assert.Equal(row.Version, (await db.Set<StorageLocation>().SingleAsync()).Version);
    }

    private sealed class PausingAdapter(IStorageAdapter inner, TaskCompletionSource entered, TaskCompletionSource release) : IStorageAdapter
    {
        public Task<StoredCopy> WriteImmutableAsync(string key, ReadOnlyMemory<byte> bytes, CancellationToken ct) => inner.WriteImmutableAsync(key, bytes, ct);
        public Task<byte[]> ReadVerifiedAsync(string key, string sha256, long length, CancellationToken ct) => inner.ReadVerifiedAsync(key, sha256, length, ct);
        public async Task<StorageHealthView> ProbeAsync(CancellationToken ct)
        {
            var result = await inner.ProbeAsync(ct); entered.TrySetResult(); await release.Task.WaitAsync(ct); return result;
        }
    }
}
