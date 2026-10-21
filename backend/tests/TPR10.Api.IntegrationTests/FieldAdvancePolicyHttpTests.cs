using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Policies;
using TPR10.Api.Identity.Csrf;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Sessions;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvancePolicyTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvancePolicyHttpTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Production_policy_routes_execute_lifecycle_with_server_actor_and_no_store()
    {
        await using var f = await Ready(postgres);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var editor = await Client(factory, f, AuthorId);
        using var reviewer = await Client(factory, f, ReviewerId);
        var meta = new CommandMeta(Guid.NewGuid(), 1, "สร้างผ่าน API") { Policy = new("Company") };
        using var created = await Send(editor, HttpMethod.Post, "policies", new CreatePolicyRequest(new(WorkspaceId, null), null, Content(), meta));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.True(created.Headers.CacheControl!.NoStore);
        var draft = (await created.Content.ReadFromJsonAsync<PolicyView>())!;
        using var updated = await Send(editor, HttpMethod.Put, $"policies/{draft.Id}", new UpdatePolicyRequest(Content(9), Meta(draft)));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        draft = (await updated.Content.ReadFromJsonAsync<PolicyView>())!;
        using var submitted = await Send(editor, HttpMethod.Post, $"policies/{draft.Id}/submit", Meta(draft));
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        var pending = (await submitted.Content.ReadFromJsonAsync<PolicyView>())!;
        using var self = await Send(editor, HttpMethod.Post, $"policies/{draft.Id}/review", new ReviewPolicyRequest("approve", Meta(pending)));
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        using var reviewed = await Send(reviewer, HttpMethod.Post, $"policies/{draft.Id}/review", new ReviewPolicyRequest("approve", Meta(pending)));
        Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode);
        Assert.Equal("Active", (await reviewed.Content.ReadFromJsonAsync<PolicyView>())!.State);
        await using var db = f.CreateContext();
        Assert.Equal(AuthorId, (await db.Set<PolicyRevision>().SingleAsync()).CreatedBy);
        Assert.Equal(ReviewerId, (await db.Set<PolicyReview>().SingleAsync()).ReviewerId);
    }

    [Theory]
    [InlineData("policies", "POST")]
    [InlineData("policies/78000000-0000-0000-0000-000000000001", "PUT")]
    [InlineData("policies/78000000-0000-0000-0000-000000000001/submit", "POST")]
    [InlineData("policies/78000000-0000-0000-0000-000000000001/review", "POST")]
    [InlineData("grants", "POST")]
    [InlineData("grants/78000000-0000-0000-0000-000000000001/review", "POST")]
    [InlineData("grants/78000000-0000-0000-0000-000000000001/revoke", "POST")]
    public async Task All_production_mutations_enforce_csrf_before_dispatch(string path, string method)
    {
        await using var f = await Ready(postgres);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var client = await Client(factory, f, AuthorId);
        using var request = new HttpRequestMessage(new(method), "/api/v1/field-advances/" + path) { Content = JsonContent.Create(new { }) };
        request.Headers.Add("Origin", "https://localhost:4443");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Contains("urn:tpr10:csrf-invalid", await response.Content.ReadAsStringAsync());
        await using var db = f.CreateContext();
        Assert.Empty(await db.Set<PolicyRevision>().ToArrayAsync());
        Assert.Empty(await db.Set<FinanceCommandReceipt>().ToArrayAsync());
    }

    [Theory]
    [InlineData("mfa")]
    [InlineData("admin-only")]
    [InlineData("cross-workspace")]
    public async Task Valid_csrf_cannot_bypass_mfa_grants_or_scope(string defect)
    {
        await using var f = await Ready(postgres);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var client = await Client(factory, f, AuthorId);
        var csrf = await IdentityTestDriver.TokenAsync(client);
        await using (var db = f.CreateContext())
        {
            if (defect == "mfa")
                foreach (var session in await db.Set<IdentitySession>().Where(x => x.UserId == AuthorId).ToArrayAsync()) session.MfaVerifiedAtUtc = null;
            if (defect == "admin-only")
                foreach (var grant in await db.Set<GrantProposal>().Where(x => x.RecipientId == AuthorId).ToArrayAsync()) grant.Capabilities = [];
            await db.SaveChangesAsync();
        }
        var request = new CreatePolicyRequest(new(defect == "cross-workspace" ? OtherWorkspaceId : WorkspaceId, null), null, Content(),
            new(Guid.NewGuid(), 1, "ทดสอบสิทธิ์") { Policy = new("Company") });
        using var mutation = IdentityTestDriver.Mutation(csrf, "/api/v1/field-advances/policies");
        mutation.Content = JsonContent.Create(request);
        using var response = await client.SendAsync(mutation);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        await using var check = f.CreateContext();
        Assert.Empty(await check.Set<PolicyRevision>().ToArrayAsync());
    }

    [Fact]
    public async Task Grant_routes_preserve_proposal_and_assignment_identifiers()
    {
        await using var f = await FieldAdvanceGrantTests.ReadyAsync(postgres);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var proposer = await Client(factory, f, AuthorId);
        using var reviewer = await Client(factory, f, ReviewerId);
        using var proposed = await Send(proposer, HttpMethod.Post, "grants", new { Spec = FieldAdvanceGrantTests.Spec(f), Meta = FieldAdvanceGrantTests.Meta() });
        Assert.Equal(HttpStatusCode.Created, proposed.StatusCode);
        var proposal = (await proposed.Content.ReadFromJsonAsync<GrantView>())!;
        using var approved = await Send(reviewer, HttpMethod.Post, $"grants/{proposal.Id}/review", new { Decision = "approve", Meta = FieldAdvanceGrantTests.Meta() });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal(proposal.Id, (await approved.Content.ReadFromJsonAsync<GrantView>())!.Id);
        var assignmentId = await FieldAdvanceGrantTests.AssignmentIdAsync(f, proposal.Id);
        using var revoked = await Send(proposer, HttpMethod.Post, $"grants/{assignmentId}/revoke", FieldAdvanceGrantTests.Meta());
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Equal(assignmentId, (await revoked.Content.ReadFromJsonAsync<GrantView>())!.Id);
        Assert.False(await FieldAdvanceGrantTests.AllowedAsync(f));
    }

    internal static async Task<HttpClient> Client(ApiFactory factory, FieldAdvanceFixture f, Guid actor)
    {
        var session = await f.SessionAsync(actor);
        var raw = RandomNumberGenerator.GetBytes(32);
        await using var db = f.CreateContext();
        var row = await db.Set<IdentitySession>().SingleAsync(x => x.Id == session.Entity!.Id);
        row.TokenHash = SHA256.HashData(raw);
        await db.SaveChangesAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443") });
        client.DefaultRequestHeaders.Add("Cookie", CsrfService.SessionCookieName + "=" + WebEncoders.Base64UrlEncode(raw));
        return client;
    }
    internal static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, object body)
    {
        var token = await IdentityTestDriver.TokenAsync(client);
        using var request = IdentityTestDriver.Mutation(token, "/api/v1/field-advances/" + path);
        request.Method = method;
        request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }
}
