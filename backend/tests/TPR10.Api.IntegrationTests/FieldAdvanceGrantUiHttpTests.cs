using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Data;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvanceGrantTests;
using static TPR10.Api.IntegrationTests.FieldAdvancePolicyHttpTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceGrantUiHttpTests(PostgresFixture postgres)
{
    private static string Query => $"workspaceId={WorkspaceId}&projectId={ProjectId}";
    private const string Root = "/api/v1/field-advances/grants";

    [Theory]
    [InlineData("delegation")]
    [InlineData("source-expired")]
    [InlineData("scope-inactive")]
    [InlineData("identity-ended")]
    public async Task All_read_surfaces_fail_closed_when_current_authority_is_lost(string defect)
    {
        await using var f = await ReadyAsync(postgres);
        var target = (await ProposeAsync(f)).Value!;
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var client = await Client(factory, f, AuthorId);
        await using (var db = f.CreateContext())
        {
            var source = await db.Set<GrantProposal>().SingleAsync(x => x.RecipientId == AuthorId);
            if (defect == "delegation") source.DelegableCapabilities = ["payment.record"];
            if (defect == "source-expired") source.EndsAtUtc = f.Clock.GetUtcNow();
            if (defect == "scope-inactive") (await db.Set<TPR10.Api.Organization.Data.Project>().SingleAsync(x => x.Id == ProjectId)).IsActive = false;
            if (defect == "identity-ended")
            {
                var link = await db.Set<FinanceAccountLink>().SingleAsync(x => x.UserId == AuthorId);
                link.EndedAtUtc = f.Clock.GetUtcNow(); link.EndedBy = ReviewerId; link.EndReason = "ทดสอบสิ้นสุด"; link.Version++;
            }
            await db.SaveChangesAsync();
        }
        foreach (var suffix in new[] { $"/{target.Id}", $"/{target.Id}/history" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Root}{suffix}?{Query}")).StatusCode);
        foreach (var suffix in new[] { "", "/review-queue" })
        {
            using var response = await client.GetAsync($"{Root}{suffix}?{Query}");
            if (defect == "delegation") Assert.Empty((await response.Content.ReadFromJsonAsync<JsonElement[]>())!);
            else Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task Read_filters_delegation_before_pagination_and_revocation_clears_same_session()
    {
        await using var f = await ReadyAsync(postgres);
        var visible = (await ProposeAsync(f)).Value!;
        var hidden = (await ProposeAsync(f, Spec(f) with { ApprovalLimit = 900m })).Value!;
        await using (var db = f.CreateContext())
        {
            var source = await db.Set<GrantProposal>().SingleAsync(x => x.RecipientId == AuthorId);
            source.ApprovalLimit = 100m;
            source.EndsAtUtc = f.Clock.GetUtcNow().AddHours(1);
            await db.SaveChangesAsync();
        }
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var client = await Client(factory, f, AuthorId);
        using var response = await client.GetAsync($"{Root}?{Query}&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var rows = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.Equal(visible.Id, Assert.Single(rows!).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Root}/{hidden.Id}?{Query}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Root}/{Guid.NewGuid()}?{Query}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Root}/{visible.Id}?workspaceId={WorkspaceId}")).StatusCode);
        await using (var db = f.CreateContext())
        {
            (await db.Set<GrantProposal>().SingleAsync(x => x.RecipientId == AuthorId)).Capabilities = ["report.read", "policy.review"];
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Root}?{Query}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Root}/{visible.Id}/history?{Query}")).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<JsonElement[]>($"{Root}/scopes"))!);
    }

    [Fact]
    public async Task Picker_uses_company_employment_and_finance_identity_without_project_membership()
    {
        await using var f = await ReadyAsync(postgres);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var client = await Client(factory, f, AuthorId);
        using var response = await client.GetAsync($"{Root}/recipients?{Query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement[]>())!;
        Assert.Contains(rows, x => x.GetProperty("id").GetGuid() == RecipientId);
        Assert.DoesNotContain(rows, x => x.GetProperty("id").GetGuid() == AuthorId);
        Assert.All(rows, x => Assert.Equal(new[] { "id", "name" }, x.EnumerateObject().Select(p => p.Name).Order()));
        await using (var db = f.CreateContext())
        {
            var old = await db.Set<FinanceAccountLink>().SingleAsync(x => x.UserId == RecipientId);
            old.EndedAtUtc = f.Clock.GetUtcNow(); old.EndedBy = AuthorId; old.EndReason = "จำลองบัญชีบุคคลเดียวกัน"; old.Version++;
            await db.SaveChangesAsync();
            db.Add(new FinanceAccountLink { Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, UserId = RecipientId, PersonId = AuthorPersonId, CreatedBy = AuthorId, CreatedAtUtc = f.Clock.GetUtcNow(), Reason = "จำลองบัญชีบุคคลเดียวกัน" });
            await db.SaveChangesAsync();
        }
        rows = (await client.GetFromJsonAsync<JsonElement[]>($"{Root}/recipients?{Query}"))!;
        Assert.DoesNotContain(rows, x => x.GetProperty("id").GetGuid() == RecipientId);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Root}/recipients?workspaceId={WorkspaceId}")).StatusCode);
    }

    [Fact]
    public async Task Queue_requires_grant_review_and_exposes_safe_actions_assignment_and_history()
    {
        await using var f = await ReadyAsync(postgres);
        var proposal = (await ProposeAsync(f)).Value!;
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var author = await Client(factory, f, AuthorId);
        using var reviewer = await Client(factory, f, ReviewerId);
        var self = await author.GetFromJsonAsync<JsonElement>($"{Root}/{proposal.Id}?{Query}");
        Assert.False(self.GetProperty("canReview").GetBoolean());
        var row = Assert.Single((await reviewer.GetFromJsonAsync<JsonElement[]>($"{Root}/review-queue?{Query}"))!);
        Assert.True(row.GetProperty("canApprove").GetBoolean());
        Assert.Equal(200, (await ReviewAsync(f, proposal.Id)).Status);
        row = await reviewer.GetFromJsonAsync<JsonElement>($"{Root}/{proposal.Id}?{Query}");
        Assert.NotEqual(proposal.Id, row.GetProperty("assignmentId").GetGuid());
        Assert.Equal(1, row.GetProperty("assignmentVersion").GetInt64());
        Assert.Equal("Active", row.GetProperty("effectiveState").GetString());
        Assert.Equal(200, (await RevokeAsync(f, row.GetProperty("assignmentId").GetGuid())).Status);
        var events = (await reviewer.GetFromJsonAsync<JsonElement[]>($"{Root}/{proposal.Id}/history?{Query}"))!;
        Assert.Equal(new[] { "Proposed", "Approved", "Revoked" }, events.Select(x => x.GetProperty("outcome").GetString()));
        Assert.All(events, x => Assert.Equal(new[] { "actorId", "at", "outcome", "proposalId", "reason" }, x.EnumerateObject().Select(p => p.Name).Order()));
        await using (var db = f.CreateContext())
        {
            (await db.Set<GrantProposal>().SingleAsync(x => x.RecipientId == AuthorId)).Capabilities = ["grant.propose", "policy.review"];
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await author.GetAsync($"{Root}/review-queue?{Query}")).StatusCode);
    }

    [Fact]
    public async Task Grant_money_wire_is_lossless_but_retained_receipt_stays_numeric()
    {
        await using var f = await ReadyAsync(postgres);
        await using (var db = f.CreateContext())
        {
            (await db.Set<GrantProposal>().SingleAsync(x => x.RecipientId == AuthorId)).ApprovalLimit = 9999999999999999.99m;
            await db.SaveChangesAsync();
        }
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var client = await Client(factory, f, AuthorId);
        var spec = Spec(f);
        var meta = Meta();
        var body = new { spec = new { spec.RecipientId, spec.Scope, spec.Capabilities, spec.DelegableCapabilities, approvalLimit = "9999999999999999.99", spec.StartsAt, spec.EndsAt }, meta };
        using var response = await Send(client, HttpMethod.Post, "grants", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("9999999999999999.99", json.GetProperty("spec").GetProperty("approvalLimit").GetString());
        using var replay = await Send(client, HttpMethod.Post, "grants", body);
        Assert.Equal(await response.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        await using var check = f.CreateContext();
        using var receipt = JsonDocument.Parse((await check.Set<FinanceCommandReceipt>().SingleAsync(x => x.CommandId == meta.CommandId)).ResultJson);
        Assert.Equal(JsonValueKind.Number, receipt.RootElement.GetProperty("Value").GetProperty("Spec").GetProperty("ApprovalLimit").ValueKind);
    }
}
