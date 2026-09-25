using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Storage;
using TPR10.Api.Identity.Accounts;
using TPR10.Api.Identity.Data;

namespace TPR10.Api.IntegrationTests;

[UnsupportedOSPlatform("windows")]
internal sealed class StorageTestFixture : IDisposable
{
    public const string ApiRoot = "/api/v1/attendance/storage";
    public string Root { get; } = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : "/tmp", "tpr10-registry-" + Guid.NewGuid().ToString("N"));
    public Dictionary<string, string?> Settings { get; }
    private readonly TestKeyMaterial keys = new();

    public StorageTestFixture()
    {
        Settings = new(keys.Settings) { ["AttendanceStorage:HealthWorkerEnabled"] = "false" };
        System.IO.Directory.CreateDirectory(Root);
        for (var i = 0; i < 2; i++)
        {
            var root = Path.Combine(Root, "volume-" + i);
            System.IO.Directory.CreateDirectory(root);
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var marker = Guid.NewGuid();
            File.WriteAllText(Path.Combine(root, ".tpr10-storage-id"), marker.ToString("D"));
            File.SetUnixFileMode(Path.Combine(root, ".tpr10-storage-id"), UnixFileMode.UserRead);
            var prefix = $"AttendanceStorage:Locations:{i}:";
            Settings[prefix + "Alias"] = "local-" + i;
            Settings[prefix + "Kind"] = "local-folder";
            Settings[prefix + "RootPath"] = root;
            Settings[prefix + "ExpectedVolumeId"] = MountIdentity.GetVolumeId(root);
            Settings[prefix + "MarkerId"] = marker.ToString("D");
        }
    }

    public static async Task<Guid> OperatorAsync(IdentityTestDriver d)
    {
        var actor = await RoleAuthorizationTests.AdminAsync(d);
        await using var db = d.Database.CreateContext();
        db.Add(new RolePermission { RoleId = IdentityCatalog.AdministratorRoleId, PermissionId = AttendanceIdentityTests.Permission(19) });
        await db.SaveChangesAsync();
        return actor;
    }

    public static async Task<StorageView> RegisterAsync(IdentityTestDriver d, string alias = "local-0")
    {
        using var response = await d.PostAsync(ApiRoot + "/locations", new { alias, reason = "เตรียมที่เก็บทดสอบ" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null(response.Headers.Location);
        return (await response.Content.ReadFromJsonAsync<StorageView>())!;
    }

    public static async Task<StorageView> ProbeAsync(IdentityTestDriver d, StorageView row)
    {
        using var response = await d.PostAsync(ApiRoot + $"/locations/{row.Id}/probe", new { expectedVersion = row.Version, reason = "ตรวจที่เก็บ" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = d.Database.CreateContext();
        var current = await db.Set<StorageLocation>().AsNoTracking().SingleAsync(x => x.Id == row.Id);
        return new(current.Id, current.Alias, current.Kind, current.Version, current.AcceptWrites, current.Health, current.CheckedAtUtc);
    }

    public void Dispose() { System.IO.Directory.Delete(Root, recursive: true); keys.Dispose(); }
}
