using Microsoft.EntityFrameworkCore;
using TPR10.Api.Identity.Data;
using TPR10.Api.Organization.Data;
using TPR10.Api.Scopes;
using TPR10.Api.Scopes.Data;

namespace TPR10.Api.IntegrationTests;

// Setup only: this helper intentionally bypasses administration APIs and is not
// evidence for their authorization, audit or session-revocation behavior.
internal sealed record ScopeFixture(IdentityTestDriver Driver, Guid UserId,
    ScopeKey Workspace, ScopeKey Project, ScopeKey Site, ScopeKey SiblingSite, ScopeKey OtherSite)
{
    public static async Task<ScopeFixture> CreateAsync(IdentityTestDriver driver)
    {
        var user = await driver.SeedUserAsync("scope-user", MfaTests.Password, []);
        var w1 = Guid.NewGuid(); var w2 = Guid.NewGuid();
        var p1 = Guid.NewGuid(); var p2 = Guid.NewGuid();
        var s1 = Guid.NewGuid(); var s2 = Guid.NewGuid(); var s3 = Guid.NewGuid();
        await using var db = driver.Database.CreateContext();
        var now = driver.Clock.GetUtcNow();
        db.AddRange(
            new Workspace { Id = w1, Code = "W1", Name = "พื้นที่หนึ่ง", CreatedBy = user, CreatedAtUtc = now },
            new Workspace { Id = w2, Code = "W2", Name = "พื้นที่สอง", CreatedBy = user, CreatedAtUtc = now },
            new Project { Id = p1, WorkspaceId = w1, Code = "P1", Name = "โครงการหนึ่ง", CreatedBy = user, CreatedAtUtc = now },
            new Project { Id = p2, WorkspaceId = w2, Code = "P2", Name = "โครงการสอง", CreatedBy = user, CreatedAtUtc = now },
            new Site { Id = s1, WorkspaceId = w1, ProjectId = p1, Code = "S1", Name = "ไซต์หนึ่ง", CreatedBy = user, CreatedAtUtc = now },
            new Site { Id = s2, WorkspaceId = w1, ProjectId = p1, Code = "S2", Name = "ไซต์สอง", CreatedBy = user, CreatedAtUtc = now },
            new Site { Id = s3, WorkspaceId = w2, ProjectId = p2, Code = "S3", Name = "ไซต์ต่างพื้นที่", CreatedBy = user, CreatedAtUtc = now });
        await db.SaveChangesAsync();
        return new(driver, user, new(w1), new(w1, p1), new(w1, p1, s1), new(w1, p1, s2), new(w2, p2, s3));
    }

    public async Task<Guid> GrantAsync(ScopeKey key, string roleClass, params string[] capabilities)
    {
        await using var db = Driver.Database.CreateContext();
        var role = Guid.NewGuid(); var assignment = Guid.NewGuid();
        db.Add(new IdentityRole { Id = role, Name = "scope-test-" + role, RoleClass = roleClass, CreatedAtUtc = Driver.Clock.GetUtcNow() });
        foreach (var capability in capabilities.Distinct())
        {
            var permission = await db.Set<IdentityPermission>().SingleAsync(p => p.Capability == capability);
            db.Add(new RolePermission { RoleId = role, PermissionId = permission.Id });
        }
        db.Add(new ScopeAssignment
        {
            Id = assignment,
            UserId = UserId,
            WorkspaceId = key.WorkspaceId,
            ProjectId = key.ProjectId,
            SiteId = key.SiteId,
            RoleId = role,
            CreatedBy = UserId,
            CreatedAtUtc = Driver.Clock.GetUtcNow(),
            Reason = "เตรียมชุดทดสอบ"
        });
        await db.SaveChangesAsync();
        return assignment;
    }

    public async Task<Guid> RecordAsync(ScopeKey key, string note, string? restricted = null)
    {
        await using var db = Driver.Database.CreateContext();
        var id = Guid.NewGuid();
        db.Add(new ScopeProbeRecord
        {
            Id = id,
            WorkspaceId = key.WorkspaceId,
            ProjectId = key.ProjectId,
            SiteId = key.SiteId,
            Note = note,
            RestrictedNote = restricted,
            CreatedBy = UserId,
            CreatedAtUtc = Driver.Clock.GetUtcNow()
        });
        await db.SaveChangesAsync();
        return id;
    }

    public string Path(ScopeKey key) => "/api/v1/workspaces/" + key.WorkspaceId
        + (key.ProjectId is { } p ? "/projects/" + p : "")
        + (key.SiteId is { } s ? "/sites/" + s : "") + "/scope-probe-records";
}
