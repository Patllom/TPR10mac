using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Scopes.Data;

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
        var pending = client.SendAsync(request);
        Task<HttpResponseMessage>? revoking = null;
        try
        {
            await gate.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            revoking = d.Client.SendAsync(revoke);
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
