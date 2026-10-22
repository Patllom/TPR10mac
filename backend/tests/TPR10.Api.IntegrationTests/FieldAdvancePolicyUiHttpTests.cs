using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Policies;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvancePolicyTests;
using static TPR10.Api.IntegrationTests.FieldAdvancePolicyHttpTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvancePolicyUiHttpTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("9999999999999999.99", 201)]
    [InlineData("10000000000000000.00", 400)]
    [InlineData("0.001", 400)]
    public async Task String_input_uses_decimal_validation_without_rounding(string amount, int status)
    {
        await using var f = await Ready(postgres);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var client = await Client(factory, f, AuthorId);
        using var response = await Send(client, HttpMethod.Post, "policies", new
        {
            scope = new FinanceScope(WorkspaceId, null),
            baseCompanyVersionId = (Guid?)null,
            content = new { clearingDays = 7, bands = new[] { new { order = 1, levelCode = "director", upperInclusive = amount } }, categories = Content().Categories },
            meta = new CommandMeta(Guid.NewGuid(), 1, "ทดสอบ string") { Policy = new("Company") }
        });
        Assert.Equal(status, (int)response.StatusCode);
        if (status == 201)
        {
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(amount, json.GetProperty("content").GetProperty("bands")[0].GetProperty("upperInclusive").GetString());
        }
    }

    [Fact]
    public async Task Money_is_lossless_string_on_http_and_replay_keeps_internal_numeric_receipt()
    {
        await using var f = await Ready(postgres);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var client = await Client(factory, f, AuthorId);
        var content = Content() with { Bands = [new(1, "director", 9999999999999999.99m)] };
        var request = new CreatePolicyRequest(new(WorkspaceId, null), null, content,
            new(Guid.NewGuid(), 1, "ทดสอบ wire") { Policy = new("Company") });
        using var created = await Send(client, HttpMethod.Post, "policies", request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var json = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.String, json.GetProperty("content").GetProperty("bands")[0].GetProperty("upperInclusive").ValueKind);
        Assert.Equal("9999999999999999.99", json.GetProperty("content").GetProperty("bands")[0].GetProperty("upperInclusive").GetString());
        using var replay = await Send(client, HttpMethod.Post, "policies", request);
        Assert.Equal(await created.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        await using var db = f.CreateContext();
        var receipt = JsonDocument.Parse((await db.Set<FinanceCommandReceipt>().SingleAsync()).ResultJson);
        Assert.Equal(JsonValueKind.Number, receipt.RootElement.GetProperty("Value").GetProperty("Content").GetProperty("Bands")[0].GetProperty("UpperInclusive").ValueKind);
    }

    [Fact]
    public async Task Discovery_discloses_only_current_exact_policy_scopes_and_rechecks_revocation()
    {
        await using var f = await Ready(postgres);
        await using (var db = f.CreateContext())
        {
            foreach (var p in await db.Set<GrantProposal>().Where(x => x.RecipientId == AuthorId).ToArrayAsync())
                p.Capabilities = p.ProjectId is null ? ["report.read"] : ["policy.edit.project"];
            await db.SaveChangesAsync();
        }
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var client = await Client(factory, f, AuthorId);
        using var response = await client.GetAsync("/api/v1/field-advances/policies/scopes");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var scopes = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, scopes.GetArrayLength());
        Assert.Equal(ProjectId, scopes[0].GetProperty("scope").GetProperty("projectId").GetGuid());
        Assert.Equal(new[] { "policy.edit.project" }, scopes[0].GetProperty("capabilities").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(new[] { "capabilities", "projectName", "scope", "workspaceName" }, scopes[0].EnumerateObject().Select(x => x.Name).Order());
        await using (var db = f.CreateContext())
        {
            foreach (var p in await db.Set<GrantProposal>().Where(x => x.RecipientId == AuthorId).ToArrayAsync()) p.Capabilities = [];
            await db.SaveChangesAsync();
        }
        Assert.Empty((await client.GetFromJsonAsync<JsonElement[]>("/api/v1/field-advances/policies/scopes"))!);
    }

    [Fact]
    public async Task Project_company_context_contains_only_pinned_and_latest_content_without_catalog_access()
    {
        await using var f = await Ready(postgres);
        var old = await Review(f, await Submit(f, await Create(f)));
        await Review(f, await Submit(f, await Create(f, project: true, basis: old.Id, content: Content(3))));
        var latest = await Review(f, await Submit(f, await Create(f, source: old, active: old, content: Content(20))));
        await using (var db = f.CreateContext())
        {
            foreach (var p in await db.Set<GrantProposal>().Where(x => x.RecipientId == AuthorId && x.ProjectId == null).ToArrayAsync()) p.Capabilities = [];
            await db.SaveChangesAsync();
        }
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var client = await Client(factory, f, AuthorId);
        var url = $"/api/v1/field-advances/policies/company-context?workspaceId={WorkspaceId}&projectId={ProjectId}";
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var context = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(old.Id, context.GetProperty("basis").GetProperty("id").GetGuid());
        Assert.Equal(latest.Id, context.GetProperty("latest").GetProperty("id").GetGuid());
        Assert.Equal(7, context.GetProperty("basis").GetProperty("content").GetProperty("clearingDays").GetInt32());
        Assert.Equal(20, context.GetProperty("latest").GetProperty("content").GetProperty("clearingDays").GetInt32());
        foreach (var name in new[] { "basis", "latest" })
            Assert.Equal(new[] { "content", "contentHash", "id" }, context.GetProperty(name).EnumerateObject().Select(x => x.Name).Order());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/field-advances/policies?workspaceId={WorkspaceId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(url.Replace(ProjectId.ToString(), OtherProjectId.ToString()))).StatusCode);
    }
}
