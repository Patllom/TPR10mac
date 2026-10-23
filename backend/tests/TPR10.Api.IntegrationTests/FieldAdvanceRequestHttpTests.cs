using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Requests;
using TPR10.Api.Identity.Data;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvancePolicyHttpTests;
using P = TPR10.Api.IntegrationTests.FieldAdvancePolicyTests;
using L = TPR10.Api.IntegrationTests.FieldAdvanceRequestLifecycleTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceRequestHttpTests(PostgresFixture postgres)
{
    private const string Root = "/api/v1/field-advances/requests";
    private static string Query => $"workspaceId={WorkspaceId}&projectId={ProjectId}";
    private async Task<FieldAdvanceFixture> Ready()
    {
        var f = await P.Ready(postgres);
        await P.Review(f, await P.Submit(f, await P.Create(f)));
        await using var db = f.CreateContext();
        foreach (var g in await db.Set<GrantProposal>().Where(x => x.ProjectId == ProjectId).ToArrayAsync())
            g.Capabilities = ["request.create", "request.delegate"];
        await db.SaveChangesAsync();
        return f;
    }
    private static CreateRequest Input() => new(new(WorkspaceId, ProjectId), AuthorPersonId,
        new("Individual", "Survey", new(2026, 10, 24), new(2026, 10, 25), new(2026, 10, 23), [],
            [new(Guid.NewGuid(), Guid.Parse("78000000-0000-0000-0000-000000000001"), "Travel", "QuantityRate", "1.2345", null, null)]));
    private static RequestCommand Meta(long version = 0) => new(Guid.NewGuid(), version, "test");
    private static async Task<JsonElement> Create(HttpClient client)
    {
        using var response = await Send(client, HttpMethod.Post, "requests", new { input = Input(), meta = Meta() });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Direct_url_cannot_bypass_scope()
    {
        await using var f = await Ready(); using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var owner = await Client(factory, f, AuthorId); using var outsider = await Client(factory, f, RecipientId);
        var row = await Create(owner); var id = row.GetProperty("id").GetGuid();
        foreach (var suffix in new[] { "", "/history", "/quote" })
        {
            using var denied = await outsider.GetAsync($"{Root}/{id}{suffix}");
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.DoesNotContain("Survey", await denied.Content.ReadAsStringAsync());
        }
        Assert.Empty((await outsider.GetFromJsonAsync<JsonElement[]>($"{Root}?{Query}&mode=mine"))!);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"{Root}?workspaceId={OtherWorkspaceId}&projectId={ProjectId}&mode=mine")).StatusCode);
    }

    [Fact]
    public async Task No_financial_access_for_admin_or_team()
    {
        await using var f = await Ready(); using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var owner = await Client(factory, f, AuthorId);
        using var teammate = await Client(factory, f, RecipientId);
        var input = Input();
        input = input with { Content = input.Content with { Kind = "Team", TeamPersonIds = [RecipientPersonId] } };
        using var created = await Send(owner, HttpMethod.Post, "requests", new { input, meta = Meta() });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var row = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Forbidden, (await teammate.GetAsync($"{Root}/{row.GetProperty("id").GetGuid()}")).StatusCode);
        await using var db = f.CreateContext();
        await TPR10.Api.Identity.Accounts.IdentityCatalog.SeedAsync(db, f.Clock.GetUtcNow(), default);
        db.Add(new UserRole { UserId = AuthorId, RoleId = TPR10.Api.Identity.Accounts.IdentityCatalog.AdministratorRoleId, CreatedAtUtc = f.Clock.GetUtcNow() });
        await db.SaveChangesAsync();
        await db.Set<GrantProposal>().Where(x => x.RecipientId == AuthorId).ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, Array.Empty<string>()));
        foreach (var path in new[] { $"/{row.GetProperty("id").GetGuid()}", $"?{Query}&mode=mine", $"/people?{Query}" })
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(Root + path)).StatusCode);
        Assert.Empty((await owner.GetFromJsonAsync<JsonElement[]>(Root + "/scopes"))!);
    }

    [Fact]
    public async Task Json_number_cannot_bypass_precision()
    {
        await using var f = await Ready(); using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var owner = await Client(factory, f, AuthorId);
        foreach (var quantity in new JsonNode[] { JsonValue.Create(1.23456m)!, JsonValue.Create("1.23456")! })
        {
            var body = JsonSerializer.SerializeToNode(new { input = Input(), meta = Meta() }, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            body["input"]!["content"]!["lines"]![0]!["quantity"] = quantity;
            using var response = await Send(owner, HttpMethod.Post, "requests", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        await using var db = f.CreateContext();
        Assert.Empty(await db.Set<AdvanceRequest>().ToArrayAsync());
    }

    [Fact]
    public async Task Read_pages_money_quote_history_and_receipts_remain_scoped_and_current()
    {
        await using var f = await Ready(); using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var owner = await Client(factory, f, AuthorId);
        var body = new { input = Input(), meta = Meta() };
        using var response = await Send(owner, HttpMethod.Post, "requests", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var first = await response.Content.ReadFromJsonAsync<JsonElement>(); var id = first.GetProperty("id").GetGuid();
        await Create(owner);
        var page1 = Assert.Single((await owner.GetFromJsonAsync<JsonElement[]>($"{Root}?{Query}&pageSize=1"))!);
        var page2 = Assert.Single((await owner.GetFromJsonAsync<JsonElement[]>($"{Root}?{Query}&pageSize=1&page=2"))!);
        Assert.NotEqual(page1.GetProperty("id"), page2.GetProperty("id"));
        Assert.Empty((await owner.GetFromJsonAsync<JsonElement[]>($"{Root}?{Query}&pageSize=1&page=3"))!);
        Assert.Equal("123.45", first.GetProperty("estimate").GetProperty("total").GetString());
        var quote = await owner.GetFromJsonAsync<JsonElement>($"{Root}/{id}/quote");
        Assert.Equal(first.GetProperty("revisionId").GetGuid(), quote.GetProperty("revisionId").GetGuid());
        Assert.Equal("123.45", quote.GetProperty("estimate").GetProperty("total").GetString());
        foreach (var suffix in new[] { $"?{Query}", "/scopes?", $"/people?{Query}", $"/{id}/history?" })
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync(Root + suffix + "&pageSize=101")).StatusCode);
        using var updated = await Send(owner, HttpMethod.Put, $"requests/{id}", new { content = Input().Content, meta = Meta(1) });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var replay = await Send(owner, HttpMethod.Post, "requests", body);
        Assert.Equal(await response.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.Equal(2, (await owner.GetFromJsonAsync<JsonElement>($"{Root}/{id}")).GetProperty("version").GetInt64());
        Assert.Equal(2, (await owner.GetFromJsonAsync<JsonElement[]>($"{Root}/{id}/history"))!.Length);
        await using var db = f.CreateContext();
        var before = await db.Set<AdvanceRequestEvent>().CountAsync();
        var malicious = JsonSerializer.SerializeToNode(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        malicious["input"]!["content"]!["total"] = "0.01";
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(owner, HttpMethod.Post, "requests", malicious)).StatusCode);
        Assert.Equal(before, await db.Set<AdvanceRequestEvent>().CountAsync());
        await db.Set<GrantProposal>().Where(x => x.RecipientId == AuthorId).ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, Array.Empty<string>()));
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(owner, HttpMethod.Post, "requests", body)).StatusCode);
    }

    [Fact]
    public async Task System_history_merges_only_authorized_request_reevaluation_with_null_actor()
    {
        await using var f = await Ready(); using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var owner = await Client(factory, f, AuthorId);
        var first = await Create(owner); var id = first.GetProperty("id").GetGuid(); var revision = first.GetProperty("revisionId").GetGuid();
        await using var db = f.CreateContext();
        var audit = new TPR10.Api.Auditing.AuditEventWriter(db, new TPR10.Api.Correlation.CorrelationContext(), f.Clock);
        var step = Guid.NewGuid();
        foreach (var target in new[] { id, Guid.NewGuid() })
            await audit.WriteAsync(new TPR10.Api.Auditing.SecurityAuditRequest(null, null, WorkspaceId, ProjectId, null,
                "field-advance.request.reevaluate", "field-advance-request", target, "success", new Dictionary<string, string>
                {
                    ["actor-type"] = "system",
                    ["source"] = "request-routing-worker",
                    ["reason"] = "system-re-evaluation",
                    ["assignment-id"] = step.ToString(),
                    ["evidence-reference"] = JsonSerializer.Serialize(new { revisionId = revision, version = 2, before = new { State = "Held" }, after = new { State = "PendingApproval" } })
                }), default);
        await db.SaveChangesAsync();
        var rows = (await owner.GetFromJsonAsync<JsonElement[]>($"{Root}/{id}/history"))!;
        Assert.Equal(2, rows.Length);
        var system = Assert.Single(rows, x => x.GetProperty("action").GetString() == "field-advance.request.reevaluate");
        Assert.Equal(JsonValueKind.Null, system.GetProperty("actor").ValueKind);
        Assert.Equal(revision, system.GetProperty("revisionId").GetGuid());
        Assert.Equal(step, system.GetProperty("stepId").GetGuid());
        Assert.Equal("Held", system.GetProperty("metadata").GetProperty("before").GetProperty("State").GetString());
        Assert.Equal("PendingApproval", system.GetProperty("metadata").GetProperty("after").GetProperty("State").GetString());
        Assert.Single((await owner.GetFromJsonAsync<JsonElement[]>($"{Root}/{id}/history?pageSize=1&page=2"))!);
    }

    [Fact]
    public async Task Queue_uses_current_ordered_step_and_drops_prior_approver_and_closed_revisions()
    {
        await using var f = await L.Ready(postgres); using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var owner = await Client(factory, f, AuthorId); using var reviewer = await Client(factory, f, ReviewerId);
        using var manager = await Client(factory, f, RecipientId);
        await using var db = f.CreateContext();
        await db.Set<GrantProposal>().Where(x => x.RecipientId == ReviewerId && x.ProjectId == ProjectId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "request.approve" }));
        var draft = await Create(owner); var id = draft.GetProperty("id").GetGuid();
        var quote = await owner.GetFromJsonAsync<JsonElement>($"{Root}/{id}/quote");
        using var confirmed = await Send(owner, HttpMethod.Post, $"requests/{id}/confirm", new { confirmationHash = quote.GetProperty("confirmationHash").GetString(), meta = Meta(1) });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var pending = await confirmed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PendingApproval", pending.GetProperty("state").GetString());
        var row = Assert.Single((await reviewer.GetFromJsonAsync<JsonElement[]>($"{Root}/review-queue?{Query}"))!);
        var step = row.GetProperty("currentStep").GetProperty("id").GetGuid();
        Assert.Equal(id, row.GetProperty("id").GetGuid());
        Assert.Empty((await manager.GetFromJsonAsync<JsonElement[]>($"{Root}/review-queue?{Query}"))!);
        using var approved = await Send(reviewer, HttpMethod.Post, $"requests/{id}/review", new { decision = new RequestDecision("Approve", step), meta = Meta(2) });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Empty((await reviewer.GetFromJsonAsync<JsonElement[]>($"{Root}/review-queue?{Query}"))!);
        Assert.Equal(HttpStatusCode.Forbidden, (await reviewer.GetAsync($"{Root}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reviewer.GetAsync($"{Root}/{id}/history")).StatusCode);
        row = Assert.Single((await manager.GetFromJsonAsync<JsonElement[]>($"{Root}/review-queue?{Query}"))!);
        using var returned = await Send(manager, HttpMethod.Post, $"requests/{id}/review", new { decision = new RequestDecision("Return", row.GetProperty("currentStep").GetProperty("id").GetGuid()), meta = Meta(3) });
        Assert.Equal(HttpStatusCode.OK, returned.StatusCode);
        Assert.Empty((await manager.GetFromJsonAsync<JsonElement[]>($"{Root}/review-queue?{Query}"))!);
        using var revised = await Send(owner, HttpMethod.Post, $"requests/{id}/revise", Meta(4));
        Assert.Equal(HttpStatusCode.OK, revised.StatusCode);
        Assert.NotEqual(draft.GetProperty("revisionId").GetGuid(), (await revised.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revisionId").GetGuid());
        Assert.Empty((await manager.GetFromJsonAsync<JsonElement[]>($"{Root}/review-queue?{Query}"))!);
        using var withdrawn = await Send(owner, HttpMethod.Post, $"requests/{id}/withdraw", Meta(5));
        Assert.Equal(HttpStatusCode.OK, withdrawn.StatusCode);
        using var copied = await Send(owner, HttpMethod.Post, $"requests/{id}/copy", Meta(6));
        Assert.Equal(HttpStatusCode.Created, copied.StatusCode);
        Assert.NotEqual(id, (await copied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Pickers_are_bounded_and_only_offer_current_exact_project_owners()
    {
        await using var f = await L.Ready(postgres); using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var owner = await Client(factory, f, AuthorId); using var proxy = await Client(factory, f, RecipientId);
        var scopes = (await owner.GetFromJsonAsync<JsonElement[]>(Root + "/scopes?pageSize=1"))!;
        Assert.Single(scopes);
        var people = (await proxy.GetFromJsonAsync<JsonElement[]>($"{Root}/people?{Query}&pageSize=1"))!;
        Assert.Equal(AuthorPersonId, Assert.Single(people).GetProperty("id").GetGuid());
        Assert.Equal(new[] { "id", "name" }, people[0].EnumerateObject().Select(x => x.Name).Order());
        Assert.Empty((await proxy.GetFromJsonAsync<JsonElement[]>($"{Root}/people?{Query}&pageSize=1&page=2"))!);
        Assert.Empty((await proxy.GetFromJsonAsync<JsonElement[]>($"{Root}/people?{Query}&prefix=missing-person"))!);
        using var created = await Send(proxy, HttpMethod.Post, "requests", new { input = Input(), meta = Meta() });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var sent = await Send(proxy, HttpMethod.Post, $"requests/{id}/send-to-owner", Meta(1));
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        using var returned = await Send(owner, HttpMethod.Post, $"requests/{id}/return-to-author", Meta(2));
        Assert.Equal(HttpStatusCode.OK, returned.StatusCode);
        Assert.Equal("Draft", (await returned.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        await using var db = f.CreateContext();
        await db.Set<GrantProposal>().Where(x => x.RecipientId == AuthorId && x.ProjectId == ProjectId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.EndsAtUtc, f.Clock.GetUtcNow()));
        Assert.Empty((await proxy.GetFromJsonAsync<JsonElement[]>($"{Root}/people?{Query}"))!);
    }

    [Fact]
    public async Task Reads_issue_bounded_queries_and_do_not_write_business_rows()
    {
        await using var f = await Ready(); using var keys = new TestKeyMaterial();
        var sql = new ReadQueries();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings, sql);
        using var owner = await Client(factory, f, AuthorId);
        var first = await Create(owner); var id = first.GetProperty("id").GetGuid();
        await using var db = f.CreateContext();
        var events = await db.Set<AdvanceRequestEvent>().CountAsync();
        var receipts = await db.Set<FinanceCommandReceipt>().CountAsync();
        sql.Capture = true;
        foreach (var path in new[] { $"?{Query}&pageSize=1", "/scopes?pageSize=1", $"/people?{Query}&pageSize=1", $"/{id}/history?pageSize=1", $"/{id}" })
        {
            using var response = await owner.GetAsync(Root + path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
        }
        sql.Capture = false;
        // Assert the SQL boundary contract, not source text: request scans must be bounded in DB.
        var queries = sql.Commands.Where(x => x.Contains("FROM field_advance_requests") || x.Contains("FROM field_advance_request_events")).ToArray();
        Assert.NotEmpty(queries);
        Assert.All(queries, x => { Assert.Contains("WHERE", x); Assert.Contains("LIMIT", x); });
        Assert.Equal(events, await db.Set<AdvanceRequestEvent>().CountAsync());
        Assert.Equal(receipts, await db.Set<FinanceCommandReceipt>().CountAsync());
        Assert.Equal(1, (await db.Set<AdvanceRequest>().SingleAsync()).Version);
    }

    private sealed class ReadQueries : DbCommandInterceptor
    {
        public bool Capture { get; set; }
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Capture) Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task Commands_require_mfa_csrf()
    {
        await using var f = await Ready(); using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var owner = await Client(factory, f, AuthorId);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Root)).StatusCode);
        foreach (var path in new[] { "", "/x/send-to-owner", "/x/confirm", "/x/return-to-author", "/x/review", "/x/revise", "/x/withdraw", "/x/copy" })
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, Root + path) { Content = JsonContent.Create(new { }) };
            message.Headers.Add("Origin", "https://localhost:4443");
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.SendAsync(message)).StatusCode);
        }
        using var put = new HttpRequestMessage(HttpMethod.Put, Root + "/" + Guid.NewGuid()) { Content = JsonContent.Create(new { }) };
        put.Headers.Add("Origin", "https://localhost:4443");
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.SendAsync(put)).StatusCode);
        var csrf = await IdentityTestDriver.TokenAsync(owner);
        await using var db = f.CreateContext();
        await db.Set<IdentitySession>().Where(x => x.UserId == AuthorId).ExecuteUpdateAsync(x => x.SetProperty(s => s.MfaVerifiedAtUtc, (DateTimeOffset?)null));
        using var mutation = IdentityTestDriver.Mutation(csrf, Root);
        mutation.Content = JsonContent.Create(new { input = Input(), meta = Meta() });
        using var response = await owner.SendAsync(mutation);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await db.Set<AdvanceRequest>().ToArrayAsync());
    }
}
