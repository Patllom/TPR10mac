using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Scopes.Data;
using TPR10.Api.Identity.Data;
using TPR10.Api.Organization.Data;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class ScopeRaceTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("list", false)]
    [InlineData("detail", false)]
    [InlineData("create", false)]
    [InlineData("update", false)]
    [InlineData("list", true)]
    [InlineData("detail", true)]
    [InlineData("create", true)]
    [InlineData("update", true)]
    public async Task Http_operation_and_assignment_revoke_follow_lock_commit_order(string operation, bool operationFirst)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        var assignment = await f.GrantAsync(f.Site, "staff", "scope-probe:read", "scope-probe:write");
        var id = await f.RecordAsync(f.Site, "original");
        var gate = new ScopeRaceBarrier(operationFirst);
        var revokeGate = new ScopeRaceBarrier(false);
        revokeGate.Release.TrySetResult();
        d.Factory.CommandInterceptor = revokeGate;
        await using var factory = new ApiFactory(d.Database.ConnectionString, clock: d.Clock, settings: keys.Settings, interceptor: gate);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443") });
        using var login = IdentityTestDriver.Mutation(await IdentityTestDriver.TokenAsync(client), "/api/v1/auth/login");
        login.Content = JsonContent.Create(new { username = "scope-user", password = MfaTests.Password });
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(login)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(f.Path(f.Site) + "/" + id)).StatusCode);
        var token = await IdentityTestDriver.TokenAsync(client);
        var adminToken = await IdentityTestDriver.TokenAsync(d.Client);
        using var request = IdentityTestDriver.Mutation(token, f.Path(f.Site) + (operation is "detail" or "update" ? "/" + id : ""));
        request.Method = operation is "list" or "detail" ? HttpMethod.Get : operation == "create" ? HttpMethod.Post : HttpMethod.Patch;
        request.Content = request.Method == HttpMethod.Get ? null : JsonContent.Create(new { note = "changed", expectedVersion = 1 });
        if (operation == "create") request.Content = JsonContent.Create(new { note = "created" });
        using var revoke = IdentityTestDriver.Mutation(adminToken, $"/api/v1/scope-assignments/{assignment}/revoke");
        revoke.Content = JsonContent.Create(new { expectedVersion = 1, reason = "race-test" });
        gate.Arm();
        revokeGate.Arm();
        var pending = client.SendAsync(request);
        Task<HttpResponseMessage>? revoking = null;
        try
        {
            await gate.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            revoking = d.Client.SendAsync(revoke);
            await revokeGate.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (!operationFirst) Assert.Equal(HttpStatusCode.OK, (await revoking.WaitAsync(TimeSpan.FromSeconds(10))).StatusCode);
        }
        finally { gate.Release.TrySetResult(); }
        using var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(operationFirst ? operation == "create" ? HttpStatusCode.Created : HttpStatusCode.OK : HttpStatusCode.Unauthorized, result.StatusCode);
        using var revoked = await revoking!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(f.Path(f.Site))).StatusCode);
        await using var db = d.Database.CreateContext();
        Assert.Equal(operationFirst && operation == "create" ? 2 : 1, await db.Set<ScopeProbeRecord>().CountAsync());
        var row = await db.Set<ScopeProbeRecord>().SingleAsync(x => x.Id == id);
        Assert.Equal(operationFirst && operation == "update" ? 2 : 1, row.Version);
        Assert.Equal(operationFirst && operation == "update" ? "changed" : "original", row.Note);
    }

    public static IEnumerable<object[]> LifecycleRaces()
    {
        foreach (var operation in new[] { "detail", "update", "export" })
            foreach (var change in new[] { "revoke", "grants", "account", "workspace", "project", "site" })
                foreach (var first in new[] { false, true }) yield return [operation, change, first];
    }

    [Theory]
    [MemberData(nameof(LifecycleRaces))]
    public async Task Read_write_export_serialize_with_lifecycle_and_invalidate_real_cookies(string operation, string change, bool operationFirst)
    {
        using var keys = new TestKeyMaterial();
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await RoleAuthorizationTests.AdminAsync(d);
        var f = await ScopeFixture.CreateAsync(d);
        var assignment = await f.GrantAsync(f.Site, "staff", "scope-probe:read", "scope-probe:write", "scope-probe:export");
        var id = await f.RecordAsync(f.Site, "private-original", "private-restricted");
        await using var db = d.Database.CreateContext();
        var role = (await db.Set<ScopeAssignment>().AsNoTracking().SingleAsync(x => x.Id == assignment)).RoleId;
        var version = (await db.Set<IdentityUser>().AsNoTracking().SingleAsync(x => x.Id == f.UserId)).SecurityVersion;
        var operationGate = new ScopeRaceBarrier(operationFirst);
        var mutationGate = new ScopeRaceBarrier(false);
        mutationGate.Release.TrySetResult();
        d.Factory.CommandInterceptor = mutationGate;
        await using var factory = new ApiFactory(d.Database.ConnectionString, clock: d.Clock, settings: keys.Settings, interceptor: operationGate);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443") });
        await EnrollAsync(client, d);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(f.Path(f.Site) + "/" + id)).StatusCode);
        using var request = IdentityTestDriver.Mutation(await IdentityTestDriver.TokenAsync(client), f.Path(f.Site) + (operation == "export" ? "/export-simulation" : "/" + id));
        request.Method = operation == "detail" ? HttpMethod.Get : operation == "update" ? HttpMethod.Patch : HttpMethod.Post;
        request.Content = operation == "detail" ? null : operation == "update" ? JsonContent.Create(new { note = "changed", expectedVersion = 1 }) : JsonContent.Create(new { });
        using var mutation = LifecycleRequest(change, f, assignment, role, await IdentityTestDriver.TokenAsync(d.Client));
        operationGate.Arm(); mutationGate.Arm();
        var pending = client.SendAsync(request);
        Task<HttpResponseMessage>? changing = null;
        try
        {
            await operationGate.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            changing = d.Client.SendAsync(mutation);
            await mutationGate.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (operationFirst) Assert.False(changing.IsCompleted);
            else Assert.Equal(change == "grants" ? HttpStatusCode.NoContent : HttpStatusCode.OK, (await changing.WaitAsync(TimeSpan.FromSeconds(10))).StatusCode);
        }
        finally { operationGate.Release.TrySetResult(); }
        using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(operationFirst ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, response.StatusCode);
        if (!operationFirst) Assert.DoesNotContain("private-", await response.Content.ReadAsStringAsync());
        using var changed = await changing!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(change == "grants" ? HttpStatusCode.NoContent : HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/session")).StatusCode);
        Assert.Equal(version + 1, (await db.Set<IdentityUser>().AsNoTracking().SingleAsync(x => x.Id == f.UserId)).SecurityVersion);
        Assert.All(await ScopeAuditFailureTests.SessionsAsync(db, f.UserId), x => Assert.NotNull(x.Revoked));
        var row = await db.Set<ScopeProbeRecord>().AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(operationFirst && operation == "update" ? 2 : 1, row.Version);
        Assert.Equal(operationFirst && operation == "update" ? "changed" : "private-original", row.Note);
        Assert.Equal(change != "grants", (await db.Set<ScopeAssignment>().AsNoTracking().SingleAsync(x => x.Id == assignment)).RevokedAtUtc is not null);
        if (change == "workspace") Assert.False((await db.Set<Workspace>().AsNoTracking().SingleAsync(x => x.Id == f.Site.WorkspaceId)).IsActive);
        if (change == "project") Assert.False((await db.Set<Project>().AsNoTracking().SingleAsync(x => x.Id == f.Site.ProjectId)).IsActive);
        if (change == "site") Assert.False((await db.Set<Site>().AsNoTracking().SingleAsync(x => x.Id == f.Site.SiteId)).IsActive);
        if (change == "account") Assert.False((await db.Set<IdentityUser>().AsNoTracking().SingleAsync(x => x.Id == f.UserId)).IsActive);
    }

    internal static HttpRequestMessage LifecycleRequest(string change, ScopeFixture f, Guid assignment, Guid role, string token)
    {
        var path = change switch
        {
            "revoke" => $"/api/v1/scope-assignments/{assignment}/revoke",
            "grants" => $"/api/v1/roles/{role}/permissions",
            "account" => $"/api/v1/users/{f.UserId}",
            "workspace" => $"/api/v1/organization/workspaces/{f.Site.WorkspaceId}",
            "project" => $"/api/v1/organization/workspaces/{f.Site.WorkspaceId}/projects/{f.Site.ProjectId}",
            "site" => $"/api/v1/organization/workspaces/{f.Site.WorkspaceId}/projects/{f.Site.ProjectId}/sites/{f.Site.SiteId}",
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        var request = IdentityTestDriver.Mutation(token, path);
        request.Method = change == "revoke" ? HttpMethod.Post : change == "grants" ? HttpMethod.Put : HttpMethod.Patch;
        request.Content = change switch
        {
            "revoke" => JsonContent.Create(new { expectedVersion = 1, reason = "race-test" }),
            "grants" => JsonContent.Create(new { permissionIds = Array.Empty<Guid>() }),
            "account" => JsonContent.Create(new { isActive = false }),
            _ => JsonContent.Create(new { name = "ปิดเพื่อทดสอบ", isActive = false, expectedVersion = 1, reason = "race-test" })
        };
        return request;
    }

    internal static async Task<string> EnrollAsync(HttpClient client, IdentityTestDriver d)
    {
        using var login = await SendAsync(client, HttpMethod.Post, "/api/v1/auth/login", new { username = "scope-user", password = MfaTests.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var enroll = await SendAsync(client, HttpMethod.Post, "/api/v1/auth/mfa/enroll", new { });
        Assert.Equal(HttpStatusCode.OK, enroll.StatusCode);
        var secret = MfaTests.Secret(await enroll.Content.ReadAsStringAsync());
        using var confirm = await SendAsync(client, HttpMethod.Post, "/api/v1/auth/mfa/confirm", new { code = MfaTests.Code(secret, d.Clock.GetUtcNow()) });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        return secret;
    }

    internal static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object body)
    {
        using var request = IdentityTestDriver.Mutation(await IdentityTestDriver.TokenAsync(client), path);
        request.Method = method; request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Concurrent_same_expected_version_has_exactly_one_winner()
    {
        await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        var f = await ScopeFixture.CreateAsync(d); await f.GrantAsync(f.Site, "staff", "scope-probe:write");
        var id = await f.RecordAsync(f.Site, "original");
        await d.LoginAsync("scope-user", MfaTests.Password);
        var token = await IdentityTestDriver.TokenAsync(d.Client);
        async Task<HttpStatusCode> Update(string note)
        {
            using var request = IdentityTestDriver.Mutation(token, f.Path(f.Site) + "/" + id);
            request.Method = HttpMethod.Patch; request.Content = JsonContent.Create(new { note, expectedVersion = 1 });
            using var response = await d.Client.SendAsync(request);
            return response.StatusCode;
        }
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, (await Task.WhenAll(Update("first"), Update("second"))).Order());
        await using var db = d.Database.CreateContext();
        Assert.Equal(2, (await db.Set<ScopeProbeRecord>().SingleAsync(x => x.Id == id)).Version);
        Assert.Equal(1, await db.AuditEvents.CountAsync(x => x.EventType == "scope.record.update"));
    }
}
