using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Identity.Data;
using TPR10.Api.Organization.Data;
using TPR10.Api.Scopes.Data;
using TPR10.Api.Identity.Authorization;
using TPR10.Api.Identity.Sessions;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using static TPR10.Api.IntegrationTests.MfaTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class ScopedMfaTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Forced_password_change_returns_to_scoped_mfa_policy_on_next_login()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var f = await ScopeFixture.CreateAsync(d);
        await f.GrantAsync(f.Site, "approval");
        await using var db = d.Database.CreateContext();
        (await db.Set<LocalCredential>().SingleAsync()).MustChangePassword = true;
        await db.SaveChangesAsync();
        using var login = await d.LoginAsync("scope-user", Password);
        await AssertStageAsync(login, "PasswordChangeRequired");
        using var changed = await d.PostAsync("/api/v1/auth/password/change", new { currentPassword = Password, newPassword = Password + "new" });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await d.Client.GetAsync("/api/v1/auth/session")).StatusCode);
        using var next = await d.LoginAsync("scope-user", Password + "new");
        await AssertStageAsync(next, "MfaEnrollmentRequired");
    }

    [Fact]
    public async Task Scoped_mfa_expires_at_fifteen_minutes_and_recovery_never_supplies_assurance()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var f = await ScopeFixture.CreateAsync(d);
        await f.GrantAsync(f.Site, "approval");
        using var login = await d.LoginAsync("scope-user", Password);
        using var enroll = await d.PostAsync("/api/v1/auth/mfa/enroll", new { });
        Assert.Equal(HttpStatusCode.OK, enroll.StatusCode);
        var secret = Secret(await enroll.Content.ReadAsStringAsync());
        using var confirm = await d.PostAsync("/api/v1/auth/mfa/confirm", new { code = Code(secret, d.Clock.GetUtcNow()) });
        await AssertStageAsync(confirm, "Active", nested: true);
        using var body = JsonDocument.Parse(await confirm.Content.ReadAsStringAsync());
        var recoveryCode = body.RootElement.GetProperty("recoveryCodes")[0].GetString();
        d.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
        using var fresh = await d.Client.GetAsync("/api/v1/auth/session");
        await AssertStageAsync(fresh, "Active");
        d.Advance(TimeSpan.FromSeconds(1));
        using var expired = await d.Client.GetAsync("/api/v1/auth/session");
        await AssertStageAsync(expired, "MfaChallengeRequired");
        using var recovery = await d.PostAsync("/api/v1/auth/mfa/recover", new { code = recoveryCode });
        await AssertStageAsync(recovery, "MfaEnrollmentRequired", nested: true);
        await using var db = d.Database.CreateContext();
        var session = await db.Set<IdentitySession>().SingleAsync(x => x.RevokedAtUtc == null);
        Assert.Null(session.MfaVerifiedAtUtc);
        Assert.False(await db.Set<MfaFactor>().AnyAsync(x => x.RevokedAtUtc == null && x.ConfirmedAtUtc != null));
    }

    [Fact]
    public async Task Confirmed_factor_still_requires_challenge_when_scoped_privilege_is_revoked()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var f = await ScopeFixture.CreateAsync(d);
        var id = await f.GrantAsync(f.Site, "approval");
        using var login = await d.LoginAsync("scope-user", Password);
        await AuthorizationTests.ConfirmAsync(d);
        using var logout = await d.PostAsync("/api/v1/auth/logout", new { });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        await using var db = d.Database.CreateContext();
        var a = await db.Set<ScopeAssignment>().SingleAsync(x => x.Id == id);
        a.RevokedAtUtc = d.Clock.GetUtcNow(); a.RevokedBy = f.UserId; a.RevocationReason = "test";
        await db.SaveChangesAsync();
        using var next = await d.LoginAsync("scope-user", Password);
        await AssertStageAsync(next, "MfaChallengeRequired");
    }

    [Theory]
    [InlineData("scope-probe:read", false)]
    [InlineData("system:probe", true)]
    public async Task Global_permission_handler_and_mutation_guard_only_accept_system_domain(string capability, bool allowed)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var user = await d.SeedUserAsync("staff", Password, ["system:probe", "scope-probe:read"]);
        using var login = await d.LoginAsync("staff", Password);
        await AuthorizationTests.ConfirmAsync(d);
        await using var db = d.Database.CreateContext();
        var entity = await db.Set<IdentitySession>().SingleAsync(x => x.RevokedAtUtc == null);
        var current = new RequestSession { Entity = entity, View = new(user, Identity.SessionStage.Active, [], entity.MfaVerifiedAtUtc) };
        var requirement = new PermissionRequirement(capability, RequireMfa: true);
        var context = new AuthorizationHandlerContext([requirement], new ClaimsPrincipal(), null);
        await new PermissionHandler(db, current, new PermissionContext(), d.Clock).HandleAsync(context);
        Assert.Equal(allowed, context.HasSucceeded);
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        Assert.Equal(allowed, await new PermissionMutationGuard(db, current, new PermissionContext(), d.Clock).AllowsAsync(user, capability, default));
    }

    [Fact]
    public async Task Scoped_management_permission_never_opens_global_management_or_counts_as_last_admin()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var admin = await AccountProvisioningTests.BootstrapAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        await f.GrantAsync(f.Site, "approval", "users:manage", "roles:manage");
        using var login = await d.LoginAsync("scope-user", Password);
        await AuthorizationTests.ConfirmAsync(d);
        Assert.Equal(HttpStatusCode.Forbidden, (await d.Client.GetAsync("/api/v1/users")).StatusCode);
        await using var db = d.Database.CreateContext();
        (await db.Set<IdentityUser>().SingleAsync(x => x.Id == admin)).IsActive = false;
        await db.SaveChangesAsync();
        Assert.False(await PermissionMutationGuard.HasManagingAdminAsync(db, default));
    }

    [Theory]
    [InlineData("approval", "workspace")]
    [InlineData("accounting", "project")]
    [InlineData("finance-data-access", "site")]
    public async Task Scoped_privilege_requires_enrollment_without_global_privilege(string roleClass, string level)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var f = await ScopeFixture.CreateAsync(d);
        await f.GrantAsync(level == "workspace" ? f.Workspace : level == "project" ? f.Project : f.Site, roleClass, "scope-probe:read");
        using var response = await d.LoginAsync("scope-user", Password);
        await AssertStageAsync(response, "MfaEnrollmentRequired");
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("workspace")]
    [InlineData("project")]
    [InlineData("site")]
    [InlineData("staff")]
    public async Task Ineffective_or_staff_assignment_does_not_require_mfa(string state)
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var f = await ScopeFixture.CreateAsync(d);
        var id = await f.GrantAsync(f.Site, state == "staff" ? "staff" : "approval");
        await using var db = d.Database.CreateContext();
        if (state == "revoked")
        {
            var a = await db.Set<ScopeAssignment>().SingleAsync(x => x.Id == id);
            a.RevokedAtUtc = d.Clock.GetUtcNow(); a.RevokedBy = f.UserId; a.RevocationReason = "test";
        }
        if (state == "workspace") (await db.Set<Workspace>().SingleAsync(x => x.Id == f.Workspace.WorkspaceId)).IsActive = false;
        if (state == "project") (await db.Set<Project>().SingleAsync(x => x.Id == f.Project.ProjectId)).IsActive = false;
        if (state == "site") (await db.Set<Site>().SingleAsync(x => x.Id == f.Site.SiteId)).IsActive = false;
        await db.SaveChangesAsync();
        using var response = await d.LoginAsync("scope-user", Password);
        await AssertStageAsync(response, "Active");
    }

    [Fact]
    public async Task Newly_granted_privilege_rejects_old_unverified_cookie()
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var f = await ScopeFixture.CreateAsync(d);
        using var login = await d.LoginAsync("scope-user", Password);
        await AssertStageAsync(login, "Active");
        await f.GrantAsync(f.Site, "approval");
        await using var db = d.Database.CreateContext();
        var token = SessionCookie(login).Split('=')[1];
        Assert.Null(await new SessionService(db, d.Clock, new RequestSession(), new Organization.EffectiveRolePolicy(db)).ValidateAsync(token, default));
        Assert.Equal(HttpStatusCode.Unauthorized, (await d.Client.GetAsync("/api/v1/auth/session")).StatusCode);
    }

    [Fact]
    public async Task Global_session_never_flattens_business_permissions()
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await d.SeedUserAsync("staff", Password, ["profile:read", "scope-probe:read"]);
        using var response = await d.LoginAsync("staff", Password);
        await AssertStageAsync(response, "Active");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "profile:read" }, body.RootElement.GetProperty("permissions").EnumerateArray().Select(x => x.GetString()).ToArray());
    }

    internal static async Task AssertStageAsync(HttpResponseMessage response, string stage, bool nested = false)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var view = nested ? body.RootElement.GetProperty("session") : body.RootElement;
        Assert.Equal(stage, view.GetProperty("stage").GetString());
    }
}
