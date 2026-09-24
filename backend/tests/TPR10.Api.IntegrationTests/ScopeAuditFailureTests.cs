using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TPR10.Api.Data;
using TPR10.Api.Identity.Data;
using TPR10.Api.Organization.Data;
using TPR10.Api.Scopes.Data;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class ScopeAuditFailureTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Export_audit_has_context_correlation_normalized_filters_count_destination_and_is_immutable()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var f = await ScopeFixture.CreateAsync(d); var assignment = await f.GrantAsync(f.Site, "staff", "scope-probe:export", "scope-probe:restricted-read");
        await f.RecordAsync(f.Site, "private-note", "private-restricted");
        await d.LoginAsync("scope-user", MfaTests.Password); await AuthorizationTests.ConfirmAsync(d);
        var from = d.Clock.GetUtcNow(); var to = from.AddMinutes(1);
        using var response = await d.PostAsync(f.Path(f.Site) + "/export-simulation", new { createdFrom = from, createdTo = to });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        await using var db = d.Database.CreateContext();
        var audit = await db.AuditEvents.SingleAsync(x => x.EventType == "scope.record.export");
        Assert.Equal(f.UserId, audit.ActorId);
        Assert.Equal((await db.Set<ScopeAssignment>().SingleAsync(x => x.Id == assignment)).RoleId, audit.ActingRoleId);
        Assert.Equal(f.Site.WorkspaceId, audit.WorkspaceId); Assert.Equal(f.Site.ProjectId, audit.ProjectId); Assert.Equal(f.Site.SiteId, audit.SiteId);
        Assert.Equal("success", audit.Outcome);
        Assert.Equal(response.Headers.GetValues("X-Correlation-ID").Single(), audit.CorrelationId);
        var metadata = await db.AuditMetadata.Where(x => x.AuditEventId == audit.Id).ToDictionaryAsync(x => x.Key, x => x.Value);
        Assert.Equal("1", metadata["row-count"]); Assert.Equal("response-json", metadata["destination-type"]);
        Assert.Equal(from.ToString("O", CultureInfo.InvariantCulture), metadata["filter-from"]);
        Assert.Equal(to.ToString("O", CultureInfo.InvariantCulture), metadata["filter-to"]);
        Assert.Equal("scope-probe:export", metadata["capability"]); Assert.Equal("authorized", metadata["scope-validation"]);
        Assert.DoesNotContain(metadata.Values, v => v.Contains("private-", StringComparison.Ordinal));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE audit_events SET outcome='denied' WHERE id={audit.Id}"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM audit_events WHERE id={audit.Id}"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE audit_event_metadata SET value='forged' WHERE audit_event_id={audit.Id}"));
    }

    [Theory]
    [InlineData("list")]
    [InlineData("detail")]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("export")]
    [InlineData("export-denied")]
    public async Task Failed_audit_never_sends_data_or_changes_records_and_session_security_state(string operation)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var f = await ScopeFixture.CreateAsync(d); await f.GrantAsync(f.Site, "staff", "scope-probe:read", "scope-probe:write", "scope-probe:export");
        var id = await f.RecordAsync(f.Site, "private-note", "private-restricted");
        await d.LoginAsync("scope-user", MfaTests.Password); await AuthorizationTests.ConfirmAsync(d);
        await using var db = d.Database.CreateContext();
        var sessions = await SessionsAsync(db, f.UserId);
        var version = (await db.Set<IdentityUser>().AsNoTracking().SingleAsync(x => x.Id == f.UserId)).SecurityVersion;
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION fail_scope_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.event_type LIKE 'scope.%' THEN RAISE EXCEPTION 'private-db-marker'; END IF; RETURN NEW; END $$; CREATE TRIGGER fail_scope_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION fail_scope_audit();");
        using var response = operation switch
        {
            "list" => await d.Client.GetAsync(f.Path(f.Site)),
            "detail" => await d.Client.GetAsync(f.Path(f.Site) + "/" + id),
            "create" => await d.PostAsync(f.Path(f.Site), new { note = "changed" }),
            "update" => await AuthorizationTests.SendAsync(d, HttpMethod.Patch, f.Path(f.Site) + "/" + id, new { note = "changed", expectedVersion = 1 }),
            _ => await d.PostAsync(f.Path(f.Site) + "/export-simulation", operation == "export" ? new { } : (object)new { createdFrom = d.Clock.GetUtcNow(), createdTo = d.Clock.GetUtcNow() })
        };
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-", body); Assert.DoesNotContain("items", body);
        Assert.Equal(response.Headers.GetValues("X-Correlation-ID").Single(), (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("correlationId").GetString());
        var row = Assert.Single(await db.Set<ScopeProbeRecord>().AsNoTracking().ToArrayAsync());
        Assert.Equal("private-note", row.Note); Assert.Equal("private-restricted", row.RestrictedNote); Assert.Equal(1, row.Version);
        Assert.Equal(version, (await db.Set<IdentityUser>().AsNoTracking().SingleAsync(x => x.Id == f.UserId)).SecurityVersion);
        Assert.Equal(sessions, await SessionsAsync(db, f.UserId));
        Assert.False(await db.AuditEvents.AnyAsync(x => x.EventType.StartsWith("scope.")));
        Assert.Equal(HttpStatusCode.OK, (await d.Client.GetAsync("/api/v1/auth/session")).StatusCode);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("grants")]
    [InlineData("account")]
    [InlineData("workspace")]
    [InlineData("project")]
    [InlineData("site")]
    public async Task Lifecycle_audit_failure_rolls_back_assignment_grants_parents_and_live_sessions(string change)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        var assignment = await f.GrantAsync(f.Site, "staff", "scope-probe:read", "scope-probe:write", "scope-probe:export");
        var id = await f.RecordAsync(f.Site, "private-original", "private-restricted");
        using var client = d.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443") });
        await ScopeRaceTests.EnrollAsync(client, d);
        using var control = await ScopeRaceTests.SendAsync(client, HttpMethod.Post, f.Path(f.Site) + "/export-simulation", new { });
        Assert.Equal(HttpStatusCode.OK, control.StatusCode);
        await using var db = d.Database.CreateContext();
        var beforeAssignment = await db.Set<ScopeAssignment>().AsNoTracking().SingleAsync(x => x.Id == assignment);
        var role = beforeAssignment.RoleId;
        var version = (await db.Set<IdentityUser>().AsNoTracking().SingleAsync(x => x.Id == f.UserId)).SecurityVersion;
        var sessions = await SessionsAsync(db, f.UserId);
        var grants = await db.Set<RolePermission>().Where(x => x.RoleId == role).OrderBy(x => x.PermissionId).Select(x => x.PermissionId).ToArrayAsync();
        var audits = await db.AuditEvents.CountAsync();
        using var mutation = ScopeRaceTests.LifecycleRequest(change, f, assignment, role, await IdentityTestDriver.TokenAsync(d.Client));
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION fail_lifecycle_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private-db-marker'; END $$; CREATE TRIGGER fail_lifecycle_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION fail_lifecycle_audit();");
        using var response = await d.Client.SendAsync(mutation);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("private-", await response.Content.ReadAsStringAsync());
        var user = await db.Set<IdentityUser>().AsNoTracking().SingleAsync(x => x.Id == f.UserId);
        Assert.True(user.IsActive); Assert.Equal(version, user.SecurityVersion);
        Assert.Equal(sessions, await SessionsAsync(db, f.UserId));
        var current = await db.Set<ScopeAssignment>().AsNoTracking().SingleAsync(x => x.Id == assignment);
        Assert.Null(current.RevokedAtUtc); Assert.Equal(beforeAssignment.Version, current.Version); Assert.Equal(role, current.RoleId);
        Assert.Equal(grants, await db.Set<RolePermission>().Where(x => x.RoleId == role).OrderBy(x => x.PermissionId).Select(x => x.PermissionId).ToArrayAsync());
        var workspace = await db.Set<Workspace>().AsNoTracking().SingleAsync(x => x.Id == f.Site.WorkspaceId);
        var project = await db.Set<Project>().AsNoTracking().SingleAsync(x => x.Id == f.Site.ProjectId);
        var site = await db.Set<Site>().AsNoTracking().SingleAsync(x => x.Id == f.Site.SiteId);
        Assert.True(workspace.IsActive && project.IsActive && site.IsActive);
        Assert.Equal(1, workspace.Version); Assert.Equal(1, project.Version); Assert.Equal(1, site.Version);
        var row = await db.Set<ScopeProbeRecord>().AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(1, row.Version); Assert.Equal("private-original", row.Note); Assert.Equal("private-restricted", row.RestrictedNote);
        Assert.Equal(audits, await db.AuditEvents.CountAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_lifecycle_audit ON audit_events;");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/session")).StatusCode);
        using var again = await ScopeRaceTests.SendAsync(client, HttpMethod.Post, f.Path(f.Site) + "/export-simulation", new { });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task Export_commit_failure_never_serializes_prepared_records()
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var f = await ScopeFixture.CreateAsync(d); await f.GrantAsync(f.Site, "staff", "scope-probe:export");
        await f.RecordAsync(f.Site, "private-note");
        await d.LoginAsync("scope-user", MfaTests.Password); await AuthorizationTests.ConfirmAsync(d);
        await using var db = d.Database.CreateContext();
        var sessions = await SessionsAsync(db, f.UserId);
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION fail_export_commit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.event_type='scope.record.export' THEN RAISE EXCEPTION 'private-commit-marker'; END IF; RETURN NEW; END $$; CREATE CONSTRAINT TRIGGER fail_export_commit AFTER INSERT ON audit_events DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fail_export_commit();");
        using var response = await d.PostAsync(f.Path(f.Site) + "/export-simulation", new { });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-", body); Assert.DoesNotContain("items", body);
        Assert.False(await db.AuditEvents.AnyAsync(x => x.EventType.StartsWith("scope.")));
        Assert.Equal(sessions, await SessionsAsync(db, f.UserId));
        Assert.Equal(1, await db.Set<ScopeProbeRecord>().CountAsync());
    }

    internal sealed record SessionState(Guid Id, string Stage, long Version, DateTimeOffset Expires, DateTimeOffset? Mfa, DateTimeOffset? Revoked);
    internal static async Task<SessionState[]> SessionsAsync(Tpr10DbContext db, Guid user)
    {
        var rows = await db.Set<IdentitySession>().AsNoTracking().Where(x => x.UserId == user).OrderBy(x => x.Id).ToArrayAsync();
        // LastSeenAtUtc is renewed by middleware outside the business transaction.
        return rows.Select(x => new SessionState(x.Id, x.Stage.ToString(), x.SecurityVersion, x.ExpiresAtUtc, x.MfaVerifiedAtUtc, x.RevokedAtUtc)).ToArray();
    }
}
