using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Attendance.Access;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Data;
using TPR10.Api.Identity.Data;
using TPR10.Api.Scopes;
using TPR10.Api.Organization.Data;
using TPR10.Api.Attendance.Storage;

namespace TPR10.Api.IntegrationTests;

[Collection("database"), UnsupportedOSPlatform("windows")]
public sealed class EvidenceReadTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_audit_preserves_own_or_hr_basis_even_with_overlapping_supervisor_grant(bool hr)
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        await using var db = f.D.Database.CreateContext();
        var now = f.D.Clock.GetUtcNow();
        if (hr)
        {
            var role = await AttendanceIdentityTests.RoleAsync(f.D, "approval", 14, 15);
            db.Add(new UserRole { UserId = f.Admin, RoleId = role, CreatedAtUtc = now });
            db.Add(new HrAssignment
            {
                Id = Guid.NewGuid(),
                UserId = f.Admin,
                WorkspaceId = f.Subject.WorkspaceId,
                DepartmentId = f.Subject.DepartmentId,
                ValidFromUtc = now.AddDays(-1),
                CreatedBy = f.Admin,
                CreatedAtUtc = now,
                Reason = "ทดสอบ HR"
            });
            db.Add(new ReportingLine
            {
                Id = Guid.NewGuid(),
                EmployeeMembershipId = f.Subject.MembershipId,
                EmployeeUserId = f.Subject.EmployeeId,
                SupervisorUserId = f.Admin,
                ValidFromUtc = now.AddHours(-1),
                CreatedBy = f.Admin,
                CreatedAtUtc = now,
                Reason = "หัวหน้าและ HR"
            });
            await db.SaveChangesAsync();
        }
        using var response = await (hr ? f.D.Client : f.Owner).GetAsync(f.Url);
        Assert.Equal(200, (int)response.StatusCode);
        var audit = await db.AuditEvents.SingleAsync(x => x.EventType == "attendance.evidence.read" && x.Outcome == "success");
        Assert.Equal(f.Subject.WorkspaceId, audit.WorkspaceId);
        Assert.Equal(hr ? "Hr" : "Own", (await db.AuditMetadata.SingleAsync(x => x.AuditEventId == audit.Id && x.Key == "scope")).Value);
    }

    [Fact]
    public async Task Initial_denial_is_audited_without_file_access()
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        using var response = await f.D.Client.GetAsync(f.Url);
        Assert.Equal(404, (int)response.StatusCode);
        await using var db = f.D.Database.CreateContext();
        Assert.True(await db.AuditEvents.AnyAsync(x => x.EventType == "attendance.evidence.read" && x.ActorId == f.Admin && x.Outcome == "denied"));
    }
    [Theory]
    [InlineData("")]
    [InlineData("/thumbnail")]
    [InlineData("/download")]
    public async Task Owner_receives_only_published_jpeg_without_cache_or_conditional_bypass(string suffix)
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        using var request = new HttpRequestMessage(HttpMethod.Get, f.Url + suffix);
        request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        request.Headers.TryAddWithoutValidation("If-Modified-Since", "Wed, 01 Jan 2031 00:00:00 GMT");
        using var response = await f.Owner.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Null(response.Headers.ETag);
        Assert.Null(response.Content.Headers.LastModified);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 255, 216 }, bytes[..2]);
        if (suffix == "/download") Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
    }

    [Theory]
    [InlineData("Reserved")]
    [InlineData("Prepared")]
    [InlineData("Orphan")]
    public async Task Unpublished_evidence_is_hidden_in_every_variant(string state)
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString, state);
        foreach (var suffix in new[] { "", "/thumbnail", "/download" })
        {
            using var response = await f.Owner.GetAsync(f.Url + suffix);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.DoesNotContain("image/", response.Content.Headers.ContentType?.ToString() ?? "");
        }
    }

    [Theory]
    [InlineData("admin", 404)]
    [InlineData("supervisor", 404)]
    [InlineData("hr", 200)]
    [InlineData("expired-mfa", 403)]
    [InlineData("cross-unit", 404)]
    [InlineData("own-supervisor", 200)]
    public async Task Business_policy_not_administrative_role_controls_photo_access(string role, int status)
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        await using var db = f.D.Database.CreateContext();
        var now = f.D.Clock.GetUtcNow();
        var url = f.Url;
        if (role == "own-supervisor")
        {
            var member = new EmployeeMembership
            {
                Id = Guid.NewGuid(),
                UserId = f.Admin,
                WorkspaceId = f.Subject.WorkspaceId,
                DepartmentId = f.Subject.DepartmentId,
                ValidFromUtc = now.AddDays(-1),
                CreatedBy = f.Admin,
                CreatedAtUtc = now,
                Reason = "หัวหน้าดูของตนเอง"
            };
            db.Add(member);
            db.Add(new ReportingLine
            {
                Id = Guid.NewGuid(),
                EmployeeMembershipId = f.Subject.MembershipId,
                EmployeeUserId = f.Subject.EmployeeId,
                SupervisorUserId = f.Admin,
                ValidFromUtc = now.AddHours(-1),
                CreatedBy = f.Admin,
                CreatedAtUtc = now,
                Reason = "ทดสอบหัวหน้า"
            });
            var ownRole = await AttendanceIdentityTests.RoleAsync(f.D, "approval", 14);
            db.Add(new UserRole { UserId = f.Admin, RoleId = ownRole, CreatedAtUtc = now });
            await db.SaveChangesAsync();
            var subject = AttendanceAccessTests.Snapshot(member, now);
            var stamp = new StampRequest(now, EvidenceAction.CheckIn);
            var pin = await StoragePinTests.Pin(f.D, f.Admin, Guid.NewGuid(), subject, stamp);
            using var scope = f.D.Factory.Services.CreateScope();
            await EvidenceRevocationTests.SetSession(scope.ServiceProvider, f.Admin);
            var writer = scope.ServiceProvider.GetRequiredService<EvidenceWriter>();
            await writer.PrepareAsync(pin, new MemoryStream(EvidencePublicationTests.Photo()), stamp, default);
            var scopedDb = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>();
            await using var tx = await ScopeOperation.BeginAsync(scopedDb, default);
            await writer.StagePublicationAsync(new(pin.EvidenceId, pin.OperationId, Guid.NewGuid(), subject, stamp), default);
            await scopedDb.SaveChangesAsync(); await tx.CommitAsync(); url = "/api/v1/attendance/evidence/" + pin.EvidenceId;
        }
        if (role is "supervisor" or "hr" or "cross-unit" or "expired-mfa")
        {
            var roleId = await AttendanceIdentityTests.RoleAsync(f.D, "approval", 14, 15);
            db.Add(new UserRole { UserId = f.Admin, RoleId = roleId, CreatedAtUtc = now });
            if (role == "supervisor")
                db.Add(new ReportingLine
                {
                    Id = Guid.NewGuid(),
                    EmployeeMembershipId = f.Subject.MembershipId,
                    EmployeeUserId = f.Subject.EmployeeId,
                    SupervisorUserId = f.Admin,
                    ValidFromUtc = now.AddHours(-1),
                    CreatedBy = f.Admin,
                    CreatedAtUtc = now,
                    Reason = "ทดสอบ"
                });
            else
            {
                var department = f.Subject.DepartmentId;
                if (role == "cross-unit")
                {
                    department = Guid.NewGuid();
                    db.Add(new Department { Id = department, WorkspaceId = f.Subject.WorkspaceId, Code = "OTHER", Name = "อีกหน่วย", CreatedBy = f.Admin, CreatedAtUtc = now });
                }
                db.Add(new HrAssignment
                {
                    Id = Guid.NewGuid(),
                    UserId = f.Admin,
                    WorkspaceId = f.Subject.WorkspaceId,
                    DepartmentId = department,
                    ValidFromUtc = now.AddDays(-1),
                    CreatedBy = f.Admin,
                    CreatedAtUtc = now,
                    Reason = "ทดสอบ"
                });
            }
            await db.SaveChangesAsync();
        }
        if (role == "expired-mfa") f.D.Advance(TimeSpan.FromMinutes(16));
        var client = f.D.Client;
        foreach (var suffix in new[] { "", "/thumbnail", "/download" })
        {
            using var response = await client.GetAsync(url + suffix);
            Assert.Equal(status, (int)response.StatusCode);
            if (status != 200) Assert.DoesNotContain("image/", response.Content.Headers.ContentType?.ToString() ?? "");
        }
    }

    [Theory]
    [InlineData("full", "valid", 200)]
    [InlineData("thumbnail", "valid", 200)]
    [InlineData("full", "corrupt", 503)]
    [InlineData("thumbnail", "corrupt", 503)]
    [InlineData("full", "quarantined", 503)]
    [InlineData("thumbnail", "quarantined", 503)]
    public async Task Fallback_requires_authorized_metadata_and_exact_checksum(string variant, string fallback, int status)
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        var target = await StorageTestFixture.ProbeAsync(f.D, await StorageTestFixture.RegisterAsync(f.D, "local-1"));
        await using var db = f.D.Database.CreateContext();
        var original = await db.Set<EvidenceLocation>().SingleAsync(x => x.Variant == variant);
        var oldPath = Path.Combine(f.Storage.Root, "volume-0", original.ObjectKey);
        var bytes = await File.ReadAllBytesAsync(oldPath);
        var runtime = f.D.Factory.Services.GetRequiredService<StorageRuntime>();
        var destination = await db.Set<StorageLocation>().SingleAsync(x => x.Id == target.Id);
        await runtime.Resolve(destination).WriteImmutableAsync(original.ObjectKey, bytes, default);
        db.Add(new EvidenceLocation
        {
            Id = Guid.NewGuid(),
            EvidenceId = original.EvidenceId,
            StorageId = target.Id,
            ObjectKey = original.ObjectKey,
            Variant = variant,
            Sha256 = original.Sha256,
            Length = original.Length,
            State = fallback == "quarantined" ? CopyState.Quarantined : CopyState.Fallback,
            VerifiedAtUtc = f.D.Clock.GetUtcNow(),
            CreatedAtUtc = f.D.Clock.GetUtcNow()
        });
        await db.SaveChangesAsync();
        await File.WriteAllBytesAsync(oldPath, [0, 1, 2]);
        if (fallback == "corrupt") await File.WriteAllBytesAsync(Path.Combine(f.Storage.Root, "volume-1", original.ObjectKey), [0, 1, 2]);
        using var response = await f.Owner.GetAsync(f.Url + (variant == "thumbnail" ? "/thumbnail" : ""));
        Assert.Equal(status, (int)response.StatusCode);
        if (status == 200) Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        else Assert.DoesNotContain("image/", response.Content.Headers.ContentType?.ToString() ?? "");
    }

    [Fact]
    public async Task Range_and_head_authenticate_before_disclosing_metadata()
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        foreach (var client in new[] { f.Owner, f.D.Client })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, f.Url);
            request.Headers.Range = new(0, 9);
            using var response = await client.SendAsync(request);
            Assert.Equal(client == f.Owner ? 416 : 404, (int)response.StatusCode);
            using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, f.Url));
            Assert.Equal(client == f.Owner ? 200 : 404, (int)head.StatusCode);
            Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        }
        using var anonymous = f.D.NewClient();
        using var anon = await anonymous.SendAsync(new HttpRequestMessage(HttpMethod.Head, f.Url));
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
    }
}

[UnsupportedOSPlatform("windows")]
internal sealed class EvidenceFixture(StorageTestFixture storage, IdentityTestDriver d, Guid admin, EmploymentSnapshot subject,
    EvidenceReservation pin, HttpClient owner) : IAsyncDisposable
{
    public StorageTestFixture Storage { get; } = storage;
    public IdentityTestDriver D { get; } = d;
    public Guid Admin { get; } = admin;
    public EmploymentSnapshot Subject { get; } = subject;
    public EvidenceReservation Pin { get; } = pin;
    public HttpClient Owner { get; } = owner;
    public string Url => "/api/v1/attendance/evidence/" + Pin.EvidenceId;

    public static async Task<EvidenceFixture> CreateAsync(string connection, string state = "Published")
    {
        var storage = new StorageTestFixture();
        var d = await IdentityTestDriver.CreateAsync(connection, storage.Settings);
        try
        {
            var admin = await StorageTestFixture.OperatorAsync(d);
            var target = await StorageTestFixture.ProbeAsync(d, await StorageTestFixture.RegisterAsync(d));
            await StoragePinTests.Switch(d, target.Id, 1);
            var subject = await StoragePinTests.Employee(d, admin);
            var stamp = new StampRequest(subject.OccurredAtUtc, EvidenceAction.CheckIn);
            var pin = await StoragePinTests.Pin(d, subject.EmployeeId, Guid.NewGuid(), subject, stamp);
            using (var scope = d.Factory.Services.CreateScope())
            {
                await AttendanceAccessTests.SetSessionAsync(scope.ServiceProvider, subject.EmployeeId);
                var writer = scope.ServiceProvider.GetRequiredService<EvidenceWriter>();
                if (state != "Reserved") await writer.PrepareAsync(pin, new MemoryStream(EvidencePublicationTests.Photo()), stamp, default);
                var db = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>();
                if (state == "Published")
                {
                    await using var tx = await ScopeOperation.BeginAsync(db, default);
                    await writer.StagePublicationAsync(new(pin.EvidenceId, pin.OperationId, Guid.NewGuid(), subject, stamp), default);
                    await db.SaveChangesAsync(); await tx.CommitAsync();
                }
                if (state == "Orphan")
                {
                    var row = await db.Set<EvidenceObject>().SingleAsync(); row.State = EvidenceState.Orphan; row.Version++;
                    await db.SaveChangesAsync();
                }
            }
            var owner = d.NewClient();
            using var login = IdentityTestDriver.Mutation(await IdentityTestDriver.TokenAsync(owner), "/api/v1/auth/login");
            login.Content = JsonContent.Create(new { username = "pin-employee", password = "Employee-Test-Only-837!" });
            using var response = await owner.SendAsync(login);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return new(storage, d, admin, subject, pin, owner);
        }
        catch { await d.DisposeAsync(); storage.Dispose(); throw; }
    }
    public async ValueTask DisposeAsync() { Owner.Dispose(); await D.DisposeAsync(); Storage.Dispose(); }
}
