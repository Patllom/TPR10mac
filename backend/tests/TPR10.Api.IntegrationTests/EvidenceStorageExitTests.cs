using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Attendance.Storage;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class EvidenceStorageExitTests(PostgresFixture postgres)
{
    // Catches accidentally mapping a test publisher or exposing evidence as static content.
    [Fact]
    public async Task Production_does_not_expose_experimental_publisher_or_public_files()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await using var factory = new ApiFactory(d.Database.ConnectionString,
            "Production", settings: keys.Settings);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost:4443"), AllowAutoRedirect = false });
        using var request = IdentityTestDriver.Mutation(await IdentityTestDriver.TokenAsync(client),
            "/api/v1/attendance/evidence/publish");
        using var upload = await client.SendAsync(request);
        // ASP.NET's method matcher can return 405 before the {id:guid} constraint.
        Assert.Contains(upload.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        using var direct = await client.GetAsync("/evidence-files/example.jpg");
        Assert.Equal(HttpStatusCode.NotFound, direct.StatusCode);
    }

    // A restore that loses bytes, bindings, protected keys, or access policy must fail this drill.
    [Fact, UnsupportedOSPlatform("windows")]
    public async Task Backup_restore_preserves_bindings_bytes_and_owner_only_access_on_a_new_host()
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        await using var restored = new IdentityDatabase(postgres.ConnectionString);
        await restored.InitializeAsync();
        await using var originalDb = f.D.Database.CreateContext();
        var binding = await originalDb.Set<EvidenceBinding>().AsNoTracking().SingleAsync();
        var copies = await originalDb.Set<EvidenceLocation>().AsNoTracking().ToArrayAsync();
        var expected = new Dictionary<string, byte[]>();
        foreach (var suffix in new[] { "", "/thumbnail", "/download" })
        {
            using var read = await f.Owner.GetAsync(f.Url + suffix);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            expected[suffix] = await read.Content.ReadAsByteArrayAsync();
        }
        using var preAuth = f.D.NewClient();
        using var issued = await preAuth.GetAsync("/api/v1/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        var preAuthCookie = Assert.Single(issued.Headers.GetValues("Set-Cookie")).Split(';')[0];
        using var issuedJson = System.Text.Json.JsonDocument.Parse(await issued.Content.ReadAsStringAsync());
        var oldToken = issuedJson.RootElement.GetProperty("token").GetString()!;
        // No writers remain during the coordinated synthetic DB/files backup.
        await f.D.Factory.DisposeAsync();
        await postgres.BackupRestoreAsync(f.D.Database.ConnectionString, restored.ConnectionString);
        var settings = new Dictionary<string, string?>(f.Storage.Settings);
        var recoveryKeys = Path.Combine(f.Storage.Root, "restored-keys");
        System.IO.Directory.CreateDirectory(recoveryKeys);
        File.SetUnixFileMode(recoveryKeys, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var key in System.IO.Directory.GetFiles(settings["Identity:Csrf:KeyRingPath"]!, "*.xml"))
        {
            var target = Path.Combine(recoveryKeys, Path.GetFileName(key));
            File.Copy(key, target);
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        var certificate = Path.Combine(f.Storage.Root, "restored-protection.pfx");
        File.Copy(settings["Identity:Csrf:CertificatePath"]!, certificate);
        File.SetUnixFileMode(certificate, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        settings["Identity:Csrf:KeyRingPath"] = recoveryKeys;
        settings["Identity:Csrf:CertificatePath"] = certificate;
        foreach (var copy in copies)
        {
            var path = Path.Combine(f.Storage.Root, "volume-0", copy.ObjectKey);
            var backup = path + ".drill-backup";
            var mode = File.GetUnixFileMode(path);
            File.Copy(path, backup);
            File.Delete(path); // Only the fixture's synthetic file; restores to its original protected alias.
            File.Copy(backup, path);
            File.Delete(backup);
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.Equal(copy.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            Assert.Equal(copy.Length, bytes.LongLength);
            Assert.Equal(mode, File.GetUnixFileMode(path));
        }
        await using var factory = new ApiFactory(restored.ConnectionString, "Production", f.D.Clock, settings);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443") });
        using var login = IdentityTestDriver.Mutation(await IdentityTestDriver.TokenAsync(owner), "/api/v1/auth/login");
        login.Content = System.Net.Http.Json.JsonContent.Create(new { username = "pin-employee", password = "Employee-Test-Only-837!" });
        using var loggedIn = await owner.SendAsync(login);
        Assert.Equal(HttpStatusCode.OK, loggedIn.StatusCode);
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443") });
        using var restoredTokenRequest = IdentityTestDriver.Mutation(oldToken, "/api/v1/attendance/evidence/publish");
        restoredTokenRequest.Headers.Add("Cookie", preAuthCookie);
        using var restoredTokenResponse = await anonymous.SendAsync(restoredTokenRequest);
        Assert.Contains(restoredTokenResponse.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        foreach (var (suffix, bytes) in expected)
        {
            using var read = await owner.GetAsync(f.Url + suffix);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal(bytes, await read.Content.ReadAsByteArrayAsync());
            Assert.True(read.Headers.CacheControl?.NoStore);
            using var denied = await anonymous.GetAsync(f.Url + suffix);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        await using var db = restored.CreateContext();
        var after = await db.Set<EvidenceBinding>().AsNoTracking().SingleAsync();
        Assert.Equal(binding.EventId, after.EventId);
        Assert.Equal(binding.EvidenceId, after.EvidenceId);
        var location = await db.Set<StorageLocation>().SingleAsync();
        Assert.Equal("unknown", factory.Services.GetRequiredService<StorageRuntime>().Health(location).Status);
    }

    [Fact, UnsupportedOSPlatform("windows")]
    public async Task New_application_host_probes_and_resumes_persisted_blocked_job()
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        var job = await EvidenceMigrationTests.StartAsync(f, await EvidenceMigrationTests.RequestAsync(f));
        // Revoke creator authority so the real worker durably blocks before copying.
        await using var db = f.D.Database.CreateContext();
        var user = await db.Set<TPR10.Api.Identity.Data.IdentityUser>().SingleAsync(x => x.Id == f.Admin);
        user.IsActive = false;
        await db.SaveChangesAsync();
        Assert.Equal(0, await EvidenceMigrationTests.RunAsync(f, job.Id));
        var blocked = await db.Set<MigrationJob>().AsNoTracking().SingleAsync();
        Assert.Equal("Blocked", blocked.Status);
        user.IsActive = true;
        await db.SaveChangesAsync();
        await f.D.Factory.DisposeAsync();
        await using var factory = new ApiFactory(f.D.Database.ConnectionString, "Production", f.D.Clock, f.Storage.Settings);
        using var scope = factory.Services.CreateScope();
        await EvidenceRevocationTests.SetSession(scope.ServiceProvider, f.Admin);
        var registry = scope.ServiceProvider.GetRequiredService<StorageRegistry>();
        var service = scope.ServiceProvider.GetRequiredService<MigrationService>();
        var stale = await service.ResumeAsync(job.Id, new(blocked.Version, "เริ่มหลังเปิดแอปใหม่"), default);
        Assert.Equal(409, ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)stale).StatusCode);
        foreach (var row in await db.Set<StorageLocation>().AsNoTracking().ToArrayAsync())
        {
            var probe = await registry.ProbeAsync(row.Id, new(row.Version, "ตรวจหลังเปิดแอปใหม่"), default);
            Assert.Equal(200, ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)probe).StatusCode);
        }
        var resumed = await service.ResumeAsync(job.Id, new(blocked.Version, "ดำเนินงานเดิมต่อ"), default);
        Assert.Equal(202, ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)resumed).StatusCode);
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<MigrationWorker>().RunBatchAsync(job.Id, 100, default));
        Assert.Equal("Completed", (await db.Set<MigrationJob>().AsNoTracking().SingleAsync()).Status);
        Assert.Equal(2, await db.Set<EvidenceLocation>().CountAsync(x => x.State == CopyState.Fallback));
    }

    [Fact, UnsupportedOSPlatform("windows")]
    public async Task Migrated_prepared_active_copies_remain_private_until_publication()
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString, "Prepared");
        var job = await EvidenceMigrationTests.StartAsync(f, await EvidenceMigrationTests.RequestAsync(f));
        Assert.Equal(2, await EvidenceMigrationTests.RunAsync(f, job.Id));
        await using var db = f.D.Database.CreateContext();
        Assert.Equal(2, await db.Set<EvidenceLocation>().CountAsync(x => x.State == CopyState.Active));
        Assert.Empty(await db.Set<EvidenceBinding>().ToArrayAsync());
        foreach (var suffix in new[] { "", "/thumbnail", "/download" })
        {
            using var read = await f.Owner.GetAsync(f.Url + suffix);
            Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
            Assert.DoesNotContain("image/", read.Content.Headers.ContentType?.ToString() ?? "");
        }
    }
}
