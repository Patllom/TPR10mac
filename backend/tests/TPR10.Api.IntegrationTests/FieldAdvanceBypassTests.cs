using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.Identity.Accounts;
using TPR10.Api.Identity.Authorization;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Organization.Data;
using TPR10.Api.Scopes;
using TPR10.Api.Scopes.Assignments;
using TPR10.Api.Scopes.Data;
using FinanceGrant = TPR10.Api.FieldAdvances.Data.GrantAssignment;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvanceAccessTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceBypassTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("owner", "request.approve", false)]
    [InlineData("owner", "finance.check", false)]
    [InlineData("owner", "payment.record", false)]
    [InlineData("delegate", "request.approve", false)]
    [InlineData("delegate", "finance.check", false)]
    [InlineData("delegate", "payment.record", false)]
    [InlineData("approver", "request.approve", false)]
    [InlineData("approver", "finance.check", false)]
    [InlineData("approver", "payment.record", false)]
    [InlineData("checker", "request.approve", false)]
    [InlineData("checker", "finance.check", true)]
    [InlineData("checker", "payment.record", false)]
    [InlineData("payer", "request.approve", false)]
    [InlineData("payer", "finance.check", false)]
    [InlineData("payer", "payment.record", true)]
    [InlineData("unrelated", "request.approve", true)]
    [InlineData("unrelated", "finance.check", true)]
    [InlineData("unrelated", "payment.record", true)]
    [InlineData("unrelated", "unknown", false)]
    public void Person_history_enforces_pairwise_duty_separation(string prior, string action, bool allowed)
    {
        var actor = Guid.NewGuid();
        var history = new RequestParticipation(prior == "owner" ? actor : Guid.NewGuid(), prior == "delegate" ? actor : null,
            prior == "approver" ? [actor] : [], prior == "checker" ? [actor] : [], prior == "payer" ? [actor] : []);
        Assert.Equal(allowed, SeparationRules.Check(actor, action, history).Allowed);
    }

    [Fact]
    public void Missing_identity_or_incomplete_history_fails_closed()
    {
        var actor = Guid.NewGuid();
        Assert.False(SeparationRules.Check(Guid.Empty, "payment.record", new(Guid.NewGuid(), null, [], [], [])).Allowed);
        Assert.False(SeparationRules.Check(actor, "payment.record", new(Guid.Empty, null, [], [], [])).Allowed);
        Assert.False(SeparationRules.Check(actor, "payment.record", new(Guid.NewGuid(), null, null!, [], [])).Allowed);
    }

    [Theory]
    [InlineData("system-administration")]
    [InlineData("approval")]
    [InlineData("staff")]
    public async Task Admin_project_manager_and_team_roles_never_replace_approved_finance_grants(string roleClass)
    {
        await using var f = await CreateAsync(postgres);
        await f.SeedPeopleAsync();
        await using var db = f.CreateContext();
        var role = new IdentityRole { Id = Guid.NewGuid(), Name = "บทบาทจำลอง", RoleClass = roleClass };
        db.Add(role);
        db.Add(new UserRole { UserId = RecipientId, RoleId = role.Id });
        if (roleClass != "system-administration")
            db.Add(new ScopeAssignment
            {
                Id = Guid.NewGuid(),
                UserId = RecipientId,
                RoleId = role.Id,
                WorkspaceId = WorkspaceId,
                ProjectId = ProjectId,
                CreatedBy = AuthorId,
                Reason = "ทีมโครงการจำลอง"
            });
        // Even legacy permission rows bearing finance strings are not approved 7A grants.
        foreach (var capability in Capabilities)
        {
            var permission = new IdentityPermission { Id = Guid.NewGuid(), Capability = capability, Domain = "scoped-business" };
            db.Add(permission); db.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id });
        }
        await db.SaveChangesAsync();
        await AssertNoFinanceAsync(f, RecipientId);
    }

    [Fact]
    public async Task Legacy_role_account_and_scope_endpoints_cannot_mint_finance_authority()
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await RoleAuthorizationTests.AdminAsync(driver);
        await using (var db = driver.Database.CreateContext()) await SeedDirectoryAsync(db);
        var f = new FieldAdvanceFixture(driver.Database) { Clock = driver.Clock };
        await f.SeedPeopleAsync();
        using var created = await driver.PostAsync("/api/v1/roles", new { name = "การเงินจำลอง", roleClass = "finance-data-access" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdRole = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var roleId = createdRole.RootElement.GetProperty("id").GetGuid();
        var csrf = await IdentityTestDriver.TokenAsync(driver.Client);
        // Advance the test clock past bootstrap's shared unsafe-request rate-limit window.
        driver.Advance(TimeSpan.FromMinutes(1));
        async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object body)
        {
            using var request = IdentityTestDriver.Mutation(csrf, path);
            request.Method = method;
            request.Content = JsonContent.Create(body);
            return await driver.Client.SendAsync(request);
        }
        await using (var db = f.CreateContext())
        {
            foreach (var capability in Capabilities)
                db.Add(new IdentityPermission { Id = Guid.NewGuid(), Capability = capability, Domain = "scoped-business" });
            await db.SaveChangesAsync();
            var ids = await db.Set<IdentityPermission>().Where(p => Capabilities.Contains(p.Capability)).Select(p => p.Id).ToArrayAsync();
            foreach (var id in ids)
            {
                using var response = await SendAsync(HttpMethod.Put, $"/api/v1/roles/{roleId}/permissions", new { permissionIds = new[] { id } });
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }
            // Simulate a retained role mapping: assigning it through any legacy route still must not grant finance access.
            foreach (var id in ids) db.Add(new RolePermission { RoleId = roleId, PermissionId = id });
            await db.SaveChangesAsync();
        }
        using var assigned = await SendAsync(HttpMethod.Put, $"/api/v1/users/{RecipientId}/roles", new { roleIds = new[] { roleId } });
        Assert.Equal(HttpStatusCode.NoContent, assigned.StatusCode);
        await AssertNoFinanceAsync(f, RecipientId);
        using var changed = await SendAsync(HttpMethod.Patch, $"/api/v1/users/{RecipientId}", new { roleIds = new[] { roleId, IdentityCatalog.StaffRoleId } });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        await AssertNoFinanceAsync(f, RecipientId);
        using var scope = await SendAsync(HttpMethod.Post, "/api/v1/scope-assignments", new
        {
            userId = RecipientId,
            roleId,
            scope = new ScopeKey(WorkspaceId, null, null),
            reason = "ทดสอบขอบเขตบริษัท"
        });
        Assert.Equal(HttpStatusCode.Created, scope.StatusCode);
        var assignment = (await scope.Content.ReadFromJsonAsync<AssignmentView>())!;
        await AssertNoFinanceAsync(f, RecipientId);
        using var replaced = await SendAsync(HttpMethod.Post, $"/api/v1/scope-assignments/{assignment.Id}/replace", new
        {
            roleId,
            scope = new ScopeKey(WorkspaceId, ProjectId, null),
            expectedVersion = assignment.Version,
            reason = "ทดสอบขอบเขตโครงการ"
        });
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        await AssertNoFinanceAsync(f, RecipientId);
        using var account = await SendAsync(HttpMethod.Post, "/api/v1/users", new { username = "fa-created", password = MfaTests.Password, roleIds = new[] { roleId } });
        Assert.Equal(HttpStatusCode.Created, account.StatusCode);
        var userId = (await account.Content.ReadFromJsonAsync<AccountView>())!.Id;
        await using (var db = f.CreateContext())
        {
            db.Add(new FinanceAccountLink
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                UserId = userId,
                PersonId = RecipientPersonId,
                CreatedBy = AuthorId,
                CreatedAtUtc = f.Clock.GetUtcNow(),
                Reason = "บัญชีจำลองคนเดียวกัน"
            });
            db.Add(new EmployeeMembership
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                WorkspaceId = WorkspaceId,
                DepartmentId = (await db.Set<Department>().SingleAsync()).Id,
                ValidFromUtc = f.Clock.GetUtcNow(),
                CreatedBy = AuthorId,
                Reason = "สมาชิกจำลอง"
            });
            (await db.Set<LocalCredential>().SingleAsync(x => x.UserId == userId)).MustChangePassword = false;
            await db.SaveChangesAsync();
        }
        await AssertNoFinanceAsync(f, userId);
        await using var verify = f.CreateContext();
        Assert.Empty(await verify.Set<FinanceGrant>().ToArrayAsync());
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("delegate")]
    [InlineData("approver")]
    [InlineData("checker")]
    public async Task Alias_account_role_change_and_regrant_cannot_erase_person_history(string prior)
    {
        await using var f = await CreateAsync(postgres);
        await f.SeedPeopleAsync();
        var first = await f.ApprovedGrantAsync("payment.record", ProjectId);
        var current = await f.SessionAsync(RecipientId);
        var history = new RequestParticipation(prior == "owner" ? RecipientPersonId : AuthorPersonId,
            prior == "delegate" ? RecipientPersonId : null, prior == "approver" ? [RecipientPersonId] : [],
            prior == "checker" ? [RecipientPersonId] : [], []);
        await using (var db = f.CreateContext())
        {
            db.Add(new FinanceAccountLink
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                UserId = AliasAccountId,
                PersonId = RecipientPersonId,
                CreatedBy = AuthorId,
                CreatedAtUtc = f.Clock.GetUtcNow(),
                Reason = "บัญชีจำลองคนเดียวกัน"
            });
            await db.SaveChangesAsync();
        }
        async Task AssertHistoryAsync()
        {
            await using var db = f.CreateContext();
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
            Assert.True((await Access(db, current, f.Clock).CheckAsync(RecipientId, new(WorkspaceId, ProjectId), "payment.record", default)).Allowed);
            foreach (var account in new[] { RecipientId, AliasAccountId })
            {
                var person = await new FinancePersonResolver(db, f.Clock).ResolveAsync(account, WorkspaceId, default);
                Assert.Equal(RecipientPersonId, person);
                Assert.False(SeparationRules.Check(person!.Value, "payment.record", history).Allowed);
            }
        }
        await AssertHistoryAsync();
        await using (var db = f.CreateContext())
        {
            var grant = await db.Set<FinanceGrant>().SingleAsync(x => x.Id == first);
            grant.RevokedAtUtc = f.Clock.GetUtcNow(); grant.RevokedBy = AuthorId; grant.RevocationReason = "ถอนจำลอง"; grant.Version++;
            var role = new IdentityRole { Id = Guid.NewGuid(), Name = "บทบาทใหม่จำลอง", RoleClass = "accounting" };
            db.Add(role); db.Add(new UserRole { UserId = RecipientId, RoleId = role.Id });
            await db.SaveChangesAsync();
        }
        await f.ApprovedGrantAsync("payment.record", ProjectId);
        await AssertHistoryAsync();
    }

    [Theory]
    [InlineData("project")]
    [InlineData("workspace")]
    [InlineData("site")]
    public async Task Closing_and_reopening_scope_does_not_resurrect_finance_grants(string level)
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var adminId = await RoleAuthorizationTests.AdminAsync(driver);
        await using (var db = driver.Database.CreateContext()) await SeedDirectoryAsync(db);
        var f = new FieldAdvanceFixture(driver.Database) { Clock = driver.Clock };
        await f.SeedPeopleAsync();
        var grantId = await f.ApprovedGrantAsync("report.read", ProjectId);
        var companyId = await f.ApprovedGrantAsync("grant.review", null);
        var siblingId = await f.ApprovedGrantAsync("report.read", Guid.Parse("71000000-0000-0000-0000-000000000012"));
        var current = await f.SessionAsync(RecipientId);
        var siteId = Guid.NewGuid();
        if (level == "site")
        {
            await using var db = f.CreateContext();
            db.Add(new Site { Id = siteId, WorkspaceId = WorkspaceId, ProjectId = ProjectId, Code = "TEST", Name = "จุดจำลอง", CreatedBy = AuthorId });
            await db.SaveChangesAsync();
        }
        var path = $"/api/v1/organization/workspaces/{WorkspaceId}";
        if (level != "workspace") path += $"/projects/{ProjectId}";
        if (level == "site") path += $"/sites/{siteId}";
        driver.Advance(TimeSpan.FromSeconds(1));
        using var closed = await AuthorizationTests.SendAsync(driver, HttpMethod.Patch, path,
            new { name = "ขอบเขตจำลอง", isActive = false, expectedVersion = 1, reason = "ปิดจำลอง" });
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        await using (var db = f.CreateContext())
        {
            var grant = await db.Set<FinanceGrant>().SingleAsync(x => x.Id == grantId);
            Assert.Equal(level != "site", grant.RevokedAtUtc is not null);
            Assert.Equal(level == "site" ? 1 : 2, grant.Version);
            Assert.Equal(level == "workspace", (await db.Set<FinanceGrant>().SingleAsync(x => x.Id == companyId)).RevokedAtUtc is not null);
            Assert.Equal(level == "workspace", (await db.Set<FinanceGrant>().SingleAsync(x => x.Id == siblingId)).RevokedAtUtc is not null);
            Assert.Equal(level != "site", (await db.Set<IdentitySession>().SingleAsync(x => x.Id == current.Entity!.Id)).RevokedAtUtc is not null);
            var audit = await db.AuditEvents.SingleOrDefaultAsync(x => x.TargetId == grantId && x.EventType == "field-advance.grant.revoked");
            if (level == "site") Assert.Null(audit);
            else { Assert.NotNull(audit); Assert.Equal(adminId, audit.ActorId); Assert.Equal(ProjectId, audit.ProjectId); }
        }
        using var reopened = await AuthorizationTests.SendAsync(driver, HttpMethod.Patch, path,
            new { name = "ขอบเขตจำลอง", isActive = true, expectedVersion = 2, reason = "เปิดจำลอง" });
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
        if (level == "workspace")
        {
            driver.Advance(TimeSpan.FromSeconds(1));
            await using var db = f.CreateContext();
            var old = await db.Set<EmployeeMembership>().SingleAsync(x => x.UserId == RecipientId);
            db.Add(new EmployeeMembership
            {
                Id = Guid.NewGuid(),
                UserId = RecipientId,
                WorkspaceId = WorkspaceId,
                DepartmentId = old.DepartmentId,
                ValidFromUtc = f.Clock.GetUtcNow(),
                CreatedBy = AuthorId,
                Reason = "กลับเป็นสมาชิกจำลอง"
            });
            await db.SaveChangesAsync();
        }
        current = await f.SessionAsync(RecipientId);
        async Task<bool> AllowedAsync()
        {
            await using var db = f.CreateContext();
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
            Assert.Equal(RecipientPersonId, await new FinancePersonResolver(db, f.Clock).ResolveAsync(RecipientId, WorkspaceId, default));
            return (await Access(db, current, f.Clock).CheckAsync(RecipientId, new(WorkspaceId, ProjectId), "report.read", default)).Allowed;
        }
        Assert.Equal(level == "site", await AllowedAsync());
        if (level != "site") { await f.ApprovedGrantAsync("report.read", ProjectId); Assert.True(await AllowedAsync()); }
    }

    [Fact]
    public async Task Finance_lifecycle_is_idempotent_inside_caller_transaction_and_audit_failure_rolls_back()
    {
        await using var f = await CreateAsync(postgres);
        await f.SeedPeopleAsync();
        var id = await f.ApprovedGrantAsync("report.read", ProjectId);
        await using (var db = f.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reject_finance_audit() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN IF NEW.event_type = 'field-advance.grant.revoked' THEN RAISE EXCEPTION 'test audit failure'; END IF; RETURN NEW; END $$;
                CREATE TRIGGER reject_finance_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION reject_finance_audit();
                """);
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
            var lifecycle = new AssignmentLifecycle(db, new AuditEventWriter(db, new CorrelationContext(), f.Clock), f.Clock, new PermissionContext());
            Assert.Equal(new[] { RecipientId }, await lifecycle.RevokeForScopeAsync(new(WorkspaceId, ProjectId, null), AuthorId, "ปิดจำลอง", default));
            Assert.Empty(await lifecycle.RevokeForScopeAsync(new(WorkspaceId, ProjectId, null), AuthorId, "ปิดซ้ำจำลอง", default));
            Assert.Equal(2, (await db.Set<FinanceGrant>().SingleAsync(x => x.Id == id)).Version);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            await tx.RollbackAsync();
        }
        await using var verify = f.CreateContext();
        var retained = await verify.Set<FinanceGrant>().SingleAsync(x => x.Id == id);
        Assert.Null(retained.RevokedAtUtc);
        Assert.Equal(1, retained.Version);
        Assert.Empty(await verify.AuditEvents.Where(x => x.EventType == "field-advance.grant.revoked").ToArrayAsync());
    }

    private static readonly string[] Capabilities = ["policy.edit.company", "policy.edit.project", "policy.review", "grant.propose", "grant.review",
        "grant.revoke", "request.approve", "finance.check", "payment.record", "report.read"];

    private static async Task AssertNoFinanceAsync(FieldAdvanceFixture f, Guid userId)
    {
        var current = await f.SessionAsync(userId);
        await using var db = f.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        Assert.NotNull(await new FinancePersonResolver(db, f.Clock).ResolveAsync(userId, WorkspaceId, default));
        foreach (var capability in Capabilities)
        {
            Assert.False((await Access(db, current, f.Clock).CheckAsync(userId, new(WorkspaceId, ProjectId), capability, default)).Allowed);
            Assert.False((await Access(db, current, f.Clock).CheckAsync(userId, new(WorkspaceId, null), capability, default)).Allowed);
        }
    }
}
