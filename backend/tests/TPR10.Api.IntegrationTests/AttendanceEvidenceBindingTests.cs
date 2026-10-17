using System.Runtime.Versioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TPR10.Api.Attendance.Access;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Attendance.Recording;
using TPR10.Api.Data;
using TPR10.Api.Identity.Data;
using TPR10.Api.Organization.Data;
using TPR10.Api.Scopes;

namespace TPR10.Api.IntegrationTests;

[Collection("database"), UnsupportedOSPlatform("windows")]
public sealed class AttendanceEvidenceBindingTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CheckoutAfterTransferUsesCheckinDepartmentForRead(bool newUnitHr, bool crossWorkspace)
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        var (checkout, moved) = await CheckoutAfterTransfer(f, crossWorkspace);
        await GrantHr(f, newUnitHr ? moved : f.Subject);
        foreach (var id in new[] { f.Pin.EvidenceId, checkout })
            foreach (var suffix in new[] { "", "/thumbnail", "/download" })
            {
                using var response = await f.D.Client.GetAsync("/api/v1/attendance/evidence/" + id + suffix);
                Assert.Equal(newUnitHr ? 404 : 200, (int)response.StatusCode);
                using var own = await f.Owner.GetAsync("/api/v1/attendance/evidence/" + id + suffix);
                Assert.Equal(200, (int)own.StatusCode);
            }
        await using var db = f.D.Database.CreateContext();
        var audit = await db.AuditEvents.Where(x => x.EventType == "attendance.evidence.read" && x.Outcome == "success").ToArrayAsync();
        Assert.NotEmpty(audit);
        Assert.All(audit, x => Assert.Equal(f.Subject.WorkspaceId, x.WorkspaceId));
    }

    [Fact]
    public async Task SupervisorCannotReadEitherPhoto()
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        var (checkout, _) = await CheckoutAfterTransfer(f);
        var role = await AttendanceIdentityTests.RoleAsync(f.D, "approval", 14);
        await using var db = f.D.Database.CreateContext();
        db.Add(new UserRole { UserId = f.Admin, RoleId = role, CreatedAtUtc = f.D.Clock.GetUtcNow() });
        db.Add(new ReportingLine
        {
            Id = Guid.NewGuid(),
            EmployeeMembershipId = f.Subject.MembershipId,
            EmployeeUserId = f.Subject.EmployeeId,
            SupervisorUserId = f.Admin,
            ValidFromUtc = f.Subject.OccurredAtUtc.AddHours(-1),
            CreatedBy = f.Admin,
            CreatedAtUtc = f.D.Clock.GetUtcNow(),
            Reason = "ทดสอบหัวหน้า"
        });
        await db.SaveChangesAsync();
        foreach (var id in new[] { f.Pin.EvidenceId, checkout })
            foreach (var suffix in new[] { "", "/thumbnail", "/download" })
            {
                using var response = await f.D.Client.GetAsync("/api/v1/attendance/evidence/" + id + suffix);
                Assert.Equal(404, (int)response.StatusCode);
            }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingEventFailsClosed(bool missingDay)
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        await using var db = f.D.Database.CreateContext();
        // Simulate legacy/corrupt data in this isolated test database only.
        if (missingDay)
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE attendance_events DISABLE TRIGGER ALL;
                UPDATE attendance_events SET day_id=gen_random_uuid();
                ALTER TABLE attendance_events ENABLE TRIGGER ALL;
                """);
        else await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE evidence_bindings DISABLE TRIGGER ALL;
            UPDATE evidence_bindings SET event_id=gen_random_uuid();
            ALTER TABLE evidence_bindings ENABLE TRIGGER ALL;
            """);
        foreach (var suffix in new[] { "", "/thumbnail", "/download" })
        {
            using var response = await f.Owner.GetAsync(f.Url + suffix);
            Assert.Equal(404, (int)response.StatusCode);
        }
    }

    [Fact]
    public async Task BindingMayPrecedeEventOnlyInsideSameTransaction()
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString, "Prepared");
        using var scope = f.D.Factory.Services.CreateScope();
        await EvidenceRevocationTests.SetSession(scope.ServiceProvider, f.Subject.EmployeeId);
        var db = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>();
        await using var tx = await ScopeOperation.BeginAsync(db, default);
        var eventId = Guid.NewGuid();
        await scope.ServiceProvider.GetRequiredService<EvidenceWriter>().StagePublicationAsync(
            new(f.Pin.EvidenceId, f.Pin.OperationId, eventId, f.Subject,
                new(f.Subject.OccurredAtUtc, EvidenceAction.CheckIn)), default);
        await db.SaveChangesAsync();
        await AttendanceEvidenceTestData.AddEventAsync(db, eventId, f.Pin.EvidenceId, f.Subject);
        await tx.CommitAsync();
        using var response = await f.Owner.GetAsync(f.Url);
        Assert.Equal(200, (int)response.StatusCode);
    }

    [Fact]
    public async Task MissingEventCannotBePublished()
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString, "Prepared");
        using var scope = f.D.Factory.Services.CreateScope();
        await EvidenceRevocationTests.SetSession(scope.ServiceProvider, f.Subject.EmployeeId);
        var db = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>();
        await using var tx = await ScopeOperation.BeginAsync(db, default);
        await scope.ServiceProvider.GetRequiredService<EvidenceWriter>().StagePublicationAsync(
            new(f.Pin.EvidenceId, f.Pin.OperationId, Guid.NewGuid(), f.Subject,
                new(f.Subject.OccurredAtUtc, EvidenceAction.CheckIn)), default);
        await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
        Assert.Contains(error.SqlState, new[] { "23503", "23514" });
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("action")]
    [InlineData("time")]
    [InlineData("evidence")]
    public async Task BindingOwnerActionTimeMismatchRejected(string mismatch)
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString, "Prepared");
        var pin = f.Pin;
        var stamp = new StampRequest(f.Subject.OccurredAtUtc,
            mismatch == "action" ? EvidenceAction.CheckOut : EvidenceAction.CheckIn);
        if (mismatch == "action")
            pin = await PinAsync(f, f.Subject, stamp);
        var eventEvidence = mismatch == "evidence"
            ? (await PinAsync(f, f.Subject, stamp)).EvidenceId : pin.EvidenceId;
        using var scope = f.D.Factory.Services.CreateScope();
        await EvidenceRevocationTests.SetSession(scope.ServiceProvider, f.Subject.EmployeeId);
        var writer = scope.ServiceProvider.GetRequiredService<EvidenceWriter>();
        if (mismatch == "action")
            await writer.PrepareAsync(pin, new MemoryStream(EvidencePublicationTests.Photo()), stamp, default);
        var db = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>();
        await using var tx = await ScopeOperation.BeginAsync(db, default);
        var subject = f.Subject;
        if (mismatch == "time") subject = subject with { OccurredAtUtc = subject.OccurredAtUtc.AddSeconds(1) };
        if (mismatch == "owner")
        {
            var member = new EmployeeMembership
            {
                Id = Guid.NewGuid(),
                UserId = f.Admin,
                WorkspaceId = subject.WorkspaceId,
                DepartmentId = subject.DepartmentId,
                ValidFromUtc = subject.OccurredAtUtc.AddDays(-1),
                CreatedAtUtc = subject.OccurredAtUtc,
                CreatedBy = f.Admin,
                Reason = "ทดสอบเจ้าของไม่ตรงกัน"
            };
            db.Add(member); await db.SaveChangesAsync();
            subject = AttendanceAccessTests.Snapshot(member, subject.OccurredAtUtc);
        }
        var row = await AttendanceEvidenceTestData.AddEventAsync(db, Guid.NewGuid(), eventEvidence, subject);
        await writer.StagePublicationAsync(new(pin.EvidenceId, pin.OperationId, row.Id, f.Subject, stamp), default);
        await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
        Assert.Equal("23514", error.SqlState);
        Assert.Equal("Evidence binding does not match attendance event", error.MessageText);
        await using var check = f.D.Database.CreateContext();
        Assert.Empty(await check.Set<EvidenceBinding>().ToArrayAsync());
        Assert.Empty(await check.Set<AttendanceEvent>().ToArrayAsync());
    }

    [Theory]
    [InlineData("full", false)]
    [InlineData("thumbnail", false)]
    [InlineData("full", true)]
    public async Task RevokedGrantBeforeDeliveryDenied(string variant, bool download)
    {
        await using var f = await EvidenceFixture.CreateAsync(postgres.ConnectionString);
        var (checkout, _) = await CheckoutAfterTransfer(f);
        await GrantHr(f, f.Subject);
        using var scope = f.D.Factory.Services.CreateScope();
        await EvidenceRevocationTests.SetSession(scope.ServiceProvider, f.Admin);
        var result = await scope.ServiceProvider.GetRequiredService<EvidenceReader>().ReadAsync(checkout, variant, download, default);
        Assert.IsType<AuthorizedImageResult>(result); // Initial day-based authorization must succeed.
        await using var db = f.D.Database.CreateContext();
        var grant = await db.Set<HrAssignment>().SingleAsync(x => x.UserId == f.Admin);
        grant.ValidToUtc = f.D.Clock.GetUtcNow(); grant.EndedBy = f.Admin; grant.Version++;
        await db.SaveChangesAsync();
        var http = EvidenceRevocationTests.Context(scope.ServiceProvider);
        await result.ExecuteAsync(http);
        Assert.Equal(404, http.Response.StatusCode);
        Assert.DoesNotContain("image/", http.Response.ContentType ?? "");
    }

    private static async Task GrantHr(EvidenceFixture f, EmploymentSnapshot subject)
    {
        var role = await AttendanceIdentityTests.RoleAsync(f.D, "approval", 15);
        await using var db = f.D.Database.CreateContext();
        db.Add(new UserRole { UserId = f.Admin, RoleId = role, CreatedAtUtc = f.D.Clock.GetUtcNow() });
        db.Add(new HrAssignment
        {
            Id = Guid.NewGuid(),
            UserId = f.Admin,
            WorkspaceId = subject.WorkspaceId,
            DepartmentId = subject.DepartmentId,
            ValidFromUtc = f.Subject.OccurredAtUtc.AddDays(-1),
            CreatedBy = f.Admin,
            CreatedAtUtc = f.D.Clock.GetUtcNow(),
            Reason = "ทดสอบสิทธิ์ HR"
        });
        await db.SaveChangesAsync();
    }

    private static async Task<(Guid, EmploymentSnapshot)> CheckoutAfterTransfer(EvidenceFixture f, bool crossWorkspace = false)
    {
        f.D.Advance(TimeSpan.FromSeconds(2));
        var now = f.D.Clock.GetUtcNow();
        await using var db = f.D.Database.CreateContext();
        var previous = await db.Set<EmployeeMembership>().SingleAsync(x => x.Id == f.Subject.MembershipId);
        previous.ValidToUtc = now; previous.EndedBy = f.Admin; previous.Version++;
        var workspace = f.Subject.WorkspaceId;
        if (crossWorkspace)
        {
            workspace = Guid.NewGuid();
            db.Add(new Workspace { Id = workspace, Code = "TRANSFER", Name = "พื้นที่ใหม่", CreatedBy = f.Admin, CreatedAtUtc = now });
        }
        var department = new Department
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace,
            Code = "TRANSFER",
            Name = "หน่วยงานใหม่",
            CreatedBy = f.Admin,
            CreatedAtUtc = now
        };
        db.Add(department);
        var member = new EmployeeMembership
        {
            Id = Guid.NewGuid(),
            UserId = f.Subject.EmployeeId,
            WorkspaceId = department.WorkspaceId,
            DepartmentId = department.Id,
            ValidFromUtc = now,
            CreatedBy = f.Admin,
            CreatedAtUtc = now,
            Reason = "ย้ายหน่วยงานระหว่างวันทำงาน"
        };
        db.Add(member); await db.SaveChangesAsync();
        var moved = AttendanceAccessTests.Snapshot(member, now);
        var stamp = new StampRequest(now, EvidenceAction.CheckOut);
        var pin = await PinAsync(f, moved, stamp);
        using var scope = f.D.Factory.Services.CreateScope();
        await EvidenceRevocationTests.SetSession(scope.ServiceProvider, moved.EmployeeId);
        var writer = scope.ServiceProvider.GetRequiredService<EvidenceWriter>();
        await writer.PrepareAsync(pin, new MemoryStream(EvidencePublicationTests.Photo()), stamp, default);
        var scopedDb = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>();
        await using var tx = await ScopeOperation.BeginAsync(scopedDb, default);
        var day = await scopedDb.Set<AttendanceDay>().SingleAsync();
        var row = await AttendanceEvidenceTestData.AddEventAsync(scopedDb, Guid.NewGuid(), pin.EvidenceId, moved, EvidenceAction.CheckOut, day.Id);
        day.EffectiveOutUtc = now; day.Version++;
        await writer.StagePublicationAsync(new(pin.EvidenceId, pin.OperationId, row.Id, moved, stamp), default);
        await scopedDb.SaveChangesAsync(); await tx.CommitAsync();
        return (pin.EvidenceId, moved);
    }

    private static async Task<EvidenceReservation> PinAsync(EvidenceFixture f, EmploymentSnapshot subject, StampRequest stamp)
    {
        using var scope = f.D.Factory.Services.CreateScope();
        await EvidenceRevocationTests.SetSession(scope.ServiceProvider, subject.EmployeeId);
        return await scope.ServiceProvider.GetRequiredService<TPR10.Api.Attendance.Storage.StorageRegistry>()
            .PinAsync(Guid.NewGuid(), subject, stamp, default);
    }
}
