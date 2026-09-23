using System.Net;
using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TPR10.Api.Data;
using TPR10.Api.Identity;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Organization;
using TPR10.Api.Organization.Data;
using TPR10.Api.Scopes;
using TPR10.Api.Scopes.Data;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class ScopeAuthorizationTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Only_exact_tuple_is_authorized_and_context_uses_server_actor(int level)
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var f = await ScopeFixture.CreateAsync(d); var key = Keys(f)[level];
        var assignment = await f.GrantAsync(key, "staff", "scope-probe:read");
        await d.LoginAsync("scope-user", MfaTests.Password);
        await using var db = d.Database.CreateContext();
        var current = await CurrentAsync(db, f.UserId);
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        var access = Access(d, db, current);
        var decision = await access.ResolveAsync(key, "scope-probe:read", false, default);
        Assert.Null(decision.Status); Assert.NotNull(decision.Context);
        Assert.Equal(f.UserId, decision.Context.ActorId); Assert.Equal(key, decision.Context.Key);
        Assert.Equal(assignment, decision.Context.AssignmentId);
        Assert.Equal((await db.Set<ScopeAssignment>().SingleAsync(x => x.Id == assignment)).RoleId, decision.Context.ActingRoleId);
        Assert.False(decision.Context.CanReadRestricted);
        foreach (var other in Keys(f).Where(x => x != key))
        {
            var denied = await access.ResolveAsync(other, "scope-probe:read", false, default);
            Assert.Null(denied.Context); Assert.Equal(404, denied.Status);
        }
        Assert.Equal(403, (await access.ResolveAsync(key, "scope-probe:write", false, default)).Status);
    }

    [Fact]
    public async Task Multiple_roles_union_only_business_capabilities_in_the_same_scope()
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var f = await ScopeFixture.CreateAsync(d);
        await SeedCatalogAsync(d);
        var first = await f.GrantAsync(f.Site, "staff", "scope-probe:read", "users:manage");
        var second = await f.GrantAsync(f.Site, "staff", "scope-probe:read", "scope-probe:write");
        await f.GrantAsync(f.SiblingSite, "staff", "scope-probe:export");
        await d.LoginAsync("scope-user", MfaTests.Password);
        await using var db = d.Database.CreateContext(); var current = await CurrentAsync(db, f.UserId);
        await using var tx = await db.Database.BeginTransactionAsync(); await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        var access = Access(d, db, current);
        var expected = await db.Set<ScopeAssignment>().Where(x => x.Id == first || x.Id == second).OrderBy(x => x.RoleId).ThenBy(x => x.Id).FirstAsync();
        Assert.Equal(expected.Id, (await access.ResolveAsync(f.Site, "scope-probe:read", false, default)).Context!.AssignmentId);
        Assert.Null((await access.ResolveAsync(f.Site, "scope-probe:write", false, default)).Status);
        Assert.Equal(403, (await access.ResolveAsync(f.Site, "users:manage", false, default)).Status);
        Assert.Equal(403, (await access.ResolveAsync(f.Site, "scope-probe:export", false, default)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await d.Client.GetAsync("/api/v1/users")).StatusCode);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("forged-workspace")]
    [InlineData("forged-project")]
    [InlineData("revoked")]
    [InlineData("workspace")]
    [InlineData("project")]
    [InlineData("site")]
    public async Task Unavailable_scopes_use_the_same_denial_without_existence_leak(string state)
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var f = await ScopeFixture.CreateAsync(d); var id = await f.GrantAsync(f.Site, "staff", "scope-probe:read");
        await d.LoginAsync("scope-user", MfaTests.Password);
        await using var db = d.Database.CreateContext(); var current = await CurrentAsync(db, f.UserId);
        if (state == "revoked") await db.Set<ScopeAssignment>().Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAtUtc, d.Clock.GetUtcNow()).SetProperty(x => x.RevokedBy, f.UserId).SetProperty(x => x.RevocationReason, "test"));
        if (state == "workspace") await db.Set<Workspace>().Where(x => x.Id == f.Site.WorkspaceId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        if (state == "project") await db.Set<Project>().Where(x => x.Id == f.Site.ProjectId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        if (state == "site") await db.Set<Site>().Where(x => x.Id == f.Site.SiteId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        var key = state switch { "unknown" => f.Site with { SiteId = Guid.NewGuid() }, "forged-workspace" => f.Site with { WorkspaceId = f.OtherSite.WorkspaceId }, "forged-project" => f.Site with { ProjectId = f.OtherSite.ProjectId }, _ => f.Site };
        var result = await Operation(d, db, current).RunAsync(key, "scope-probe:read", false, (_, _) => throw new Exception("must not run"), default);
        Assert.Equal(404, AccountProvisioningTests.Status(result));
        var problem = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult>(result).ProblemDetails;
        Assert.Equal("urn:tpr10:scope-unavailable", problem.Type); Assert.Null(problem.Detail);
        var audit = await db.AuditEvents.SingleAsync(x => x.EventType == "scope.access.denied");
        Assert.Null(audit.ActingRoleId); Assert.Equal(key.WorkspaceId, audit.WorkspaceId);
        Assert.Contains(await db.AuditMetadata.Where(x => x.AuditEventId == audit.Id).ToArrayAsync(), x => x.Key == "scope-validation" && x.Value == "requested-unverified");
    }

    [Theory]
    [InlineData("revoked", 401)]
    [InlineData("expired", 401)]
    [InlineData("idle", 401)]
    [InlineData("version", 401)]
    [InlineData("inactive", 401)]
    [InlineData("wrong-user", 401)]
    [InlineData("password", 403)]
    [InlineData("stage", 403)]
    [InlineData("future-mfa", 403)]
    [InlineData("expired-mfa", 403)]
    public async Task Rechecks_database_not_stale_request_session(string state, int expected)
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var f = await ScopeFixture.CreateAsync(d); await f.GrantAsync(f.Site, "staff", "scope-probe:read");
        await d.LoginAsync("scope-user", MfaTests.Password);
        await using var db = d.Database.CreateContext(); var current = await CurrentAsync(db, f.UserId);
        var sessions = db.Set<IdentitySession>().Where(x => x.Id == current.Entity!.Id);
        if (state == "revoked") await sessions.ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAtUtc, d.Clock.GetUtcNow()));
        if (state == "expired")
        {
            d.Advance(TimeSpan.FromMinutes(1));
            await sessions.ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAtUtc, d.Clock.GetUtcNow()));
        }
        if (state == "idle") await sessions.ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSeenAtUtc, d.Clock.GetUtcNow().AddMinutes(-30)));
        if (state == "version") await db.Set<IdentityUser>().Where(x => x.Id == f.UserId).ExecuteUpdateAsync(s => s.SetProperty(x => x.SecurityVersion, 1));
        if (state == "inactive") await db.Set<IdentityUser>().Where(x => x.Id == f.UserId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        if (state == "wrong-user") current.Entity!.UserId = Guid.NewGuid();
        if (state == "password") await db.Set<LocalCredential>().Where(x => x.UserId == f.UserId).ExecuteUpdateAsync(s => s.SetProperty(x => x.MustChangePassword, true));
        if (state == "stage") await sessions.ExecuteUpdateAsync(s => s.SetProperty(x => x.Stage, SessionStage.MfaEnrollmentRequired));
        if (state == "future-mfa") await sessions.ExecuteUpdateAsync(s => s.SetProperty(x => x.MfaVerifiedAtUtc, d.Clock.GetUtcNow().AddMinutes(1)));
        if (state == "expired-mfa") await sessions.ExecuteUpdateAsync(s => s.SetProperty(x => x.MfaVerifiedAtUtc, d.Clock.GetUtcNow().AddMinutes(-15)));
        var result = await Operation(d, db, current).RunAsync(f.Site, "scope-probe:read", false, (_, _) => throw new Exception("must not run"), default);
        Assert.Equal(expected, AccountProvisioningTests.Status(result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Global_roles_never_bypass_assignment(bool admin)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = admin ? await RoleAuthorizationTests.AdminAsync(d) : await d.SeedUserAsync("global", MfaTests.Password, ["scope-probe:read"]);
        var f = await ScopeFixture.CreateAsync(d);
        if (!admin) await d.LoginAsync("global", MfaTests.Password);
        await using var db = d.Database.CreateContext();
        var result = await Operation(d, db, await CurrentAsync(db, actor)).RunAsync(f.Site, "scope-probe:read", false, (_, _) => throw new Exception("must not run"), default);
        Assert.Equal(404, AccountProvisioningTests.Status(result));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Restricted_read_requires_exact_permission_and_recent_mfa(bool restricted, bool mfa)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var f = await ScopeFixture.CreateAsync(d);
        await f.GrantAsync(f.Site, "staff", "scope-probe:read", "scope-probe:export");
        await f.GrantAsync(restricted ? f.Site : f.SiblingSite, "staff", "scope-probe:restricted-read");
        await d.LoginAsync("scope-user", MfaTests.Password);
        if (mfa) await AuthorizationTests.ConfirmAsync(d);
        await using var db = d.Database.CreateContext(); var current = await CurrentAsync(db, f.UserId);
        await using var tx = await db.Database.BeginTransactionAsync(); await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        var access = Access(d, db, current);
        var read = await access.ResolveAsync(f.Site, "scope-probe:read", false, default);
        Assert.Null(read.Status); Assert.Equal(restricted && mfa, read.Context!.CanReadRestricted);
        Assert.Equal(mfa ? null : (int?)403, (await access.ResolveAsync(f.Site, "scope-probe:export", true, default)).Status);
    }

    [Theory]
    [InlineData("approval")]
    [InlineData("accounting")]
    [InlineData("finance-data-access")]
    public async Task Newly_privileged_role_cannot_use_old_active_session_without_mfa(string roleClass)
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var f = await ScopeFixture.CreateAsync(d); await f.GrantAsync(f.Site, "staff", "scope-probe:read");
        await d.LoginAsync("scope-user", MfaTests.Password);
        // Deliberately bypass lifecycle to prove service-side current role revalidation.
        await f.GrantAsync(f.OtherSite, roleClass, "scope-probe:write");
        await using var db = d.Database.CreateContext();
        var result = await Operation(d, db, await CurrentAsync(db, f.UserId)).RunAsync(f.Site, "scope-probe:read", false, (_, _) => throw new Exception("must not run"), default);
        Assert.Equal(403, AccountProvisioningTests.Status(result));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Invalid_scope_is_400_before_session_check(int shape)
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await using var db = d.Database.CreateContext();
        var key = shape switch { 0 => new ScopeKey(Guid.Empty), 1 => new ScopeKey(Guid.NewGuid(), Guid.Empty), _ => new ScopeKey(Guid.NewGuid(), null, Guid.NewGuid()) };
        Assert.Equal(400, AccountProvisioningTests.Status(await Operation(d, db, new()).RunAsync(key, "scope-probe:read", false, (_, _) => throw new Exception("must not run"), default)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Callback_changes_and_audit_are_atomic_even_when_callback_saves_before_error(bool denied, bool auditFault)
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var f = await ScopeFixture.CreateAsync(d); await f.GrantAsync(f.Site, "staff", "scope-probe:write");
        await d.LoginAsync("scope-user", MfaTests.Password);
        await using var db = d.Database.CreateContext(); var current = await CurrentAsync(db, f.UserId);
        if (auditFault) await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION scope_fault() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.event_type LIKE 'scope.%' THEN RAISE EXCEPTION 'private-db-error'; END IF; RETURN NEW; END $$; CREATE TRIGGER scope_fault BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION scope_fault();");
        var result = await Operation(d, db, current).RunAsync(f.Site, "scope-probe:write", false, async (c, ct) =>
        {
            Assert.NotNull(db.Database.CurrentTransaction);
            db.Add(new ScopeProbeRecord
            {
                Id = Guid.NewGuid(),
                WorkspaceId = c.Key.WorkspaceId,
                ProjectId = c.Key.ProjectId,
                SiteId = c.Key.SiteId,
                Note = "private-callback-payload",
                CreatedAtUtc = d.Clock.GetUtcNow(),
                CreatedBy = c.ActorId
            });
            await db.SaveChangesAsync(ct);
            return denied ? Results.Problem(statusCode: 409) : Results.Ok(new { materialized = true });
        }, default);
        Assert.Equal(auditFault ? 503 : denied ? 409 : 200, AccountProvisioningTests.Status(result));
        await using var observer = d.Database.CreateContext();
        Assert.Equal(!denied && !auditFault ? 1 : 0, await observer.Set<ScopeProbeRecord>().CountAsync());
        var audits = await observer.AuditEvents.Where(x => x.EventType.StartsWith("scope.")).ToArrayAsync();
        if (auditFault) Assert.Empty(audits);
        else Assert.Equal(denied ? "scope.access.denied" : "scope.operation.completed", Assert.Single(audits).EventType);
        Assert.DoesNotContain(await observer.AuditMetadata.ToArrayAsync(), x => x.Value.Contains("private-callback-payload"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revocation_committed_while_waiting_for_lock_blocks_operation_and_discovery(bool discovery)
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var f = await ScopeFixture.CreateAsync(d); await f.GrantAsync(f.Site, "staff", "scope-probe:read");
        await d.LoginAsync("scope-user", MfaTests.Password);
        await using var revoker = d.Database.CreateContext();
        var current = await CurrentAsync(revoker, f.UserId);
        var gate = new BeforeLockGate();
        await using var db = new Tpr10DbContext(new DbContextOptionsBuilder<Tpr10DbContext>().UseNpgsql(d.Database.ConnectionString).AddInterceptors(gate).Options);
        await using var tx = await revoker.Database.BeginTransactionAsync();
        await revoker.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        var pending = discovery ? new ScopeDiscovery(db, Access(d, db, current), current, AccountProvisioningTests.Audit(d, db)).ListAsync(1, 25, default)
            : Operation(d, db, current).RunAsync(f.Site, "scope-probe:read", false, (_, _) => throw new Exception("must not run"), default);
        try
        {
            await gate.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await new SessionService(revoker, d.Clock, new(), new EffectiveRolePolicy(revoker)).RevokeUserAsync(f.UserId, "test", default);
            await revoker.SaveChangesAsync(); await tx.CommitAsync();
        }
        finally { gate.Release.TrySetResult(); }
        Assert.Equal(401, AccountProvisioningTests.Status(await pending.WaitAsync(TimeSpan.FromSeconds(10))));
        Assert.Equal(1, await revoker.AuditEvents.CountAsync(x => x.EventType == "scope.access.denied"));
    }

    private sealed class BeforeLockGate : DbCommandInterceptor
    {
        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("pg_advisory_xact_lock(7241002)", StringComparison.Ordinal))
            { Arrived.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            return result;
        }
    }

    [Fact]
    public async Task Unexpected_callback_exception_is_not_disguised_as_dependency_failure_and_rolls_back()
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var f = await ScopeFixture.CreateAsync(d); await f.GrantAsync(f.Site, "staff", "scope-probe:write");
        await d.LoginAsync("scope-user", MfaTests.Password);
        await using var db = d.Database.CreateContext();
        var current = await CurrentAsync(db, f.UserId);
        await Assert.ThrowsAsync<ArgumentException>(() => Operation(d, db, current).RunAsync(f.Site, "scope-probe:write", false, async (c, ct) =>
        {
            db.Add(new ScopeProbeRecord
            {
                Id = Guid.NewGuid(),
                WorkspaceId = c.Key.WorkspaceId,
                ProjectId = c.Key.ProjectId,
                SiteId = c.Key.SiteId,
                Note = "rollback",
                CreatedBy = c.ActorId,
                CreatedAtUtc = d.Clock.GetUtcNow()
            });
            await db.SaveChangesAsync(ct);
            throw new ArgumentException("unexpected code defect");
        }, default));
        await using var observer = d.Database.CreateContext();
        Assert.Empty(await observer.Set<ScopeProbeRecord>().ToArrayAsync());
    }

    internal static async Task SeedCatalogAsync(IdentityTestDriver d)
    {
        await using var db = d.Database.CreateContext();
        await TPR10.Api.Identity.Accounts.IdentityCatalog.SeedAsync(db, d.Clock.GetUtcNow(), default);
        await db.SaveChangesAsync();
    }
    internal static ScopeKey[] Keys(ScopeFixture f) => [f.Workspace, f.Project, f.Site, f.SiblingSite, f.OtherSite];
    internal static async Task<RequestSession> CurrentAsync(Tpr10DbContext db, Guid user) => new()
    { Entity = await db.Set<IdentitySession>().AsNoTracking().SingleAsync(x => x.UserId == user && x.RevokedAtUtc == null) };
    internal static ScopeAccess Access(IdentityTestDriver d, Tpr10DbContext db, RequestSession current) => new(db, current, new EffectiveRolePolicy(db), d.Clock);
    internal static ScopeOperation Operation(IdentityTestDriver d, Tpr10DbContext db, RequestSession current) => new(db, Access(d, db, current), current, AccountProvisioningTests.Audit(d, db));
}
