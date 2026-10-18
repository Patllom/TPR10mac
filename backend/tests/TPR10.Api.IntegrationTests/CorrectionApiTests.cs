using System.Net;
using System.Runtime.Versioning;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Attendance.History;
using TPR10.Api.Attendance.Corrections;
using TPR10.Api.Identity.Csrf;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Sessions;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class CorrectionApiTests(PostgresFixture postgres)
{
    internal const string Root = "/api/v1/attendance/corrections";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [UnsupportedOSPlatform("windows")]
    public async Task Discovery_revalidates_authority_before_first_byte(bool routing)
    {
        await using var f = await new CorrectionApprovalTests(postgres).Fixture();
        await CorrectionApprovalTests.Pending(f);
        await using var factory = new ApiFactory(f.Data.Database.ConnectionString, clock: f.Clock);
        using var scope = factory.Services.CreateScope();
        await EvidenceRevocationTests.SetSession(scope.ServiceProvider, routing ? Guid.Parse("00000000-0000-0000-0000-000000000033") : CaptureFixture.Employee);
        var http = EvidenceRevocationTests.Context(scope.ServiceProvider); http.Request.Path = Root;
        await new AttendanceJsonResult(async (s, ct) =>
        {
            var result = routing ? await s.CorrectionsAsync("routing", 1, 20, ct)
                : await s.CorrectionSitesAsync(DateTimeOffset.Parse("2026-09-25T01:00:00Z"), 1, 100, ct);
            Assert.Equal(200, result.Status); Assert.NotNull(result.Value);
            f.Clock.Advance(TimeSpan.FromMinutes(routing ? 15 : 31));
            return result;
        }).ExecuteAsync(http);
        Assert.Equal(routing ? 403 : 401, http.Response.StatusCode);
        http.Response.Body.Position = 0;
        Assert.DoesNotContain("items", await new StreamReader(http.Response.Body).ReadToEndAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("?at=invalid")]
    [InlineData("?at=2026-09-25T01:00:00Z&at=2026-09-25T02:00:00Z")]
    [InlineData("?at=2026-09-25T01:00:00Z&pageSize=101")]
    [InlineData("?at=2026-09-25T01:00:00Z&page=0")]
    [InlineData("?at=2027-09-25T01:00:00Z")]
    public async Task Historical_sites_reject_invalid_or_unbounded_queries(string query)
    {
        await using var f = await new CorrectionApprovalTests(postgres).Fixture();
        await using var factory = new ApiFactory(f.Data.Database.ConnectionString, clock: f.Clock);
        using var owner = await Client(factory, f, CaptureFixture.Session);
        using var response = await owner.GetAsync(Root + "/sites" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Historical_site_choices_use_own_retained_interval_not_todays_scope()
    {
        await using var f = await new CorrectionApprovalTests(postgres).Fixture();
        await using var db = f.Data.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE user_scope_assignments SET revoked_at_utc='2026-09-25T12:00:00Z',revoked_by=user_id,revocation_reason='ย้าย',version=version+1;
            INSERT INTO sites(id,workspace_id,project_id,code,name,created_by)
            SELECT '00000000-0000-0000-0000-000000000052',workspace_id,project_id,'S2','ปัจจุบัน',created_by FROM sites;
            INSERT INTO user_scope_assignments(id,user_id,workspace_id,project_id,site_id,role_id,created_at_utc,created_by,reason)
            SELECT gen_random_uuid(),user_id,workspace_id,project_id,'00000000-0000-0000-0000-000000000052',role_id,'2026-09-25T12:00:00Z',created_by,reason FROM user_scope_assignments;
            """);
        await using var factory = new ApiFactory(f.Data.Database.ConnectionString, clock: f.Clock);
        using var owner = await Client(factory, f, CaptureFixture.Session);
        using var response = await owner.GetAsync(Root + "/sites?at=2026-09-25T01:00:00Z&page=1&pageSize=100");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var row = Assert.Single(body.GetProperty("items").EnumerateArray());
        Assert.Equal(CaptureFixture.Site.SiteId, row.GetProperty("site").GetProperty("siteId").GetGuid());
        Assert.Equal(new[] { "label", "site" }, row.EnumerateObject().Select(x => x.Name).Order());
        using var boundary = await owner.GetAsync(Root + "/sites?at=2026-09-25T12:00:00Z&page=1&pageSize=100");
        Assert.Equal(HttpStatusCode.OK, boundary.StatusCode);
        var next = await boundary.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000052"), Assert.Single(next.GetProperty("items").EnumerateArray()).GetProperty("site").GetProperty("siteId").GetGuid());
        using var forged = await owner.GetAsync(Root + "/sites?at=2026-09-25T01:00:00Z&employeeId=" + CorrectionApprovalTests.Head);
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Root + "/sites?at=2026-09-25T01:00:00Z")).StatusCode);
    }

    private static SaveCorrection Input() => new(null, new(2026, 9, 25), null,
        new(new(2026, 9, 25), DateTimeOffset.Parse("2026-09-25T01:00:00Z"), null,
            new(CaptureFixture.Site.WorkspaceId, CaptureFixture.Site.ProjectId!.Value, CaptureFixture.Site.SiteId!.Value), null, "ลืมลงเวลา"));

    [Fact]
    public async Task Create_save_submit_two_approvals_return_only_acknowledgment_and_read_uses_current_grants()
    {
        await using var f = await new CorrectionApprovalTests(postgres).Fixture();
        await using var factory = new ApiFactory(f.Data.Database.ConnectionString, clock: f.Clock);
        using var owner = await Client(factory, f, CaptureFixture.Session);
        var key = Guid.NewGuid(); var create = new { commandId = key, input = Input() };
        using var first = await Post(owner, Root, create); Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var ack = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "commandId", "id" }, ack.EnumerateObject().Select(x => x.Name).Order());
        var id = ack.GetProperty("id").GetGuid(); Assert.Equal(key, ack.GetProperty("commandId").GetGuid());
        using var replay = await Post(owner, Root, create); Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(id, (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        var v = await View(owner, id); Assert.Equal(CorrectionState.Draft, v.State); Assert.NotNull(v.History);
        using var saved = await Post(owner, $"{Root}/{id}/revisions", new { input = Input(), command = CorrectionApprovalTests.Command(v) });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode); v = await View(owner, id); Assert.Equal(2, v.CurrentRevision);
        using var submitted = await Post(owner, $"{Root}/{id}/submit", CorrectionApprovalTests.Command(v));
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode); v = await View(owner, id);
        using var head = await Client(factory, f, CorrectionApprovalTests.HeadSession);
        var queue = await head.GetFromJsonAsync<CorrectionPage>(Root + "/approvals?stage=supervisor");
        Assert.Single(queue!.Items); Assert.Null(queue.Items[0].History); Assert.Equal("WorkflowSupervisor", queue.Items[0].ReadBasis);
        var command = CorrectionApprovalTests.Command(v);
        using var approved = await Post(head, $"{Root}/{id}/supervisor-approve", command);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal(new[] { "commandId", "id" }, (await approved.Content.ReadFromJsonAsync<JsonElement>()).EnumerateObject().Select(x => x.Name).Order());
        using var headRead = await head.GetAsync($"{Root}/{id}"); Assert.Equal(HttpStatusCode.NotFound, headRead.StatusCode);
        using var headReplay = await Post(head, $"{Root}/{id}/supervisor-approve", command); Assert.Equal(HttpStatusCode.OK, headReplay.StatusCode);
        using var hr = await Client(factory, f, CorrectionApprovalTests.HrSession);
        v = await View(hr, id); Assert.Equal(CorrectionState.PendingHr, v.State);
        using var final = await Post(hr, $"{Root}/{id}/hr-approve", CorrectionApprovalTests.Command(v)); Assert.Equal(HttpStatusCode.OK, final.StatusCode);
        using var hrRead = await hr.GetAsync($"{Root}/{id}"); Assert.Equal(HttpStatusCode.NotFound, hrRead.StatusCode);
        Assert.Equal(CorrectionState.Approved, (await View(owner, id)).State);
        Assert.True(first.Headers.CacheControl!.NoStore); Assert.True(final.Headers.CacheControl!.NoStore);
    }

    [Theory]
    [InlineData("return")]
    [InlineData("reroute")]
    public async Task Return_and_reroute_have_separate_authorized_routes(string action)
    {
        await using var f = await new CorrectionApprovalTests(postgres).Fixture(); var v = await CorrectionApprovalTests.Pending(f);
        await using var factory = new ApiFactory(f.Data.Database.ConnectionString, clock: f.Clock);
        using var client = await Client(factory, f, action == "return" ? CorrectionApprovalTests.HeadSession : CorrectionApprovalTests.HrSession);
        using var result = await Post(client, $"{Root}/{v.Id}/{action}", CorrectionApprovalTests.Command(v)); Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        await using var db = f.Data.Database.CreateContext(); var row = await db.Set<CorrectionRequest>().SingleAsync();
        Assert.Equal(action == "return" ? CorrectionState.Returned : CorrectionState.PendingSupervisor, row.State);
        Assert.Equal(action == "return" ? 1 : 2, row.CurrentRevision);
    }

    [Theory]
    [InlineData("unknown", 400)]
    [InlineData("duplicate", 400)]
    [InlineData("missing", 400)]
    [InlineData("null", 400)]
    [InlineData("nested", 400)]
    [InlineData("oversize", 413)]
    [InlineData("multipart", 415)]
    public async Task Forged_or_malformed_body_never_creates_request(string fault, int expected)
    {
        await using var f = await new CorrectionApprovalTests(postgres).Fixture();
        await using var factory = new ApiFactory(f.Data.Database.ConnectionString, clock: f.Clock);
        using var owner = await Client(factory, f, CaptureFixture.Session);
        var raw = JsonSerializer.Serialize(new { commandId = Guid.NewGuid(), input = Input() }, Json);
        raw = fault switch
        {
            "unknown" => raw[..^1] + ",\"actorId\":\"forged\"}",
            "duplicate" => raw[..^1] + ",\"commandId\":\"00000000-0000-0000-0000-000000000001\"}",
            "missing" => "{}",
            "null" => "{\"commandId\":\"00000000-0000-0000-0000-000000000001\",\"input\":null}",
            "nested" => raw.Replace("\"proposal\":{", "\"proposal\":{\"state\":4,"),
            "oversize" => new string(' ', 65537),
            _ => raw
        };
        using var req = IdentityTestDriver.Mutation(await IdentityTestDriver.TokenAsync(owner), Root);
        req.Content = fault == "multipart" ? new MultipartFormDataContent() : new StringContent(raw, Encoding.UTF8, "application/json");
        using var response = await owner.SendAsync(req); Assert.Equal(expected, (int)response.StatusCode);
        await using var db = f.Data.Database.CreateContext(); Assert.False(await db.Set<CorrectionRequest>().AnyAsync());
    }

    [Theory]
    [InlineData("/approvals?stage=admin")]
    [InlineData("?page=0")]
    [InlineData("?pageSize=101")]
    [InlineData("?actorId=other")]
    [InlineData("?page=1&page=2")]
    [InlineData("/approvals")]
    public async Task Queries_reject_scope_injection_and_unbounded_pages(string suffix)
    {
        await using var f = await new CorrectionApprovalTests(postgres).Fixture();
        await using var factory = new ApiFactory(f.Data.Database.ConnectionString, clock: f.Clock);
        using var client = await Client(factory, f, CaptureFixture.Session);
        using var response = await client.GetAsync(Root + suffix); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("csrf", 403)]
    [InlineData("mfa", 403)]
    [InlineData("self", 403)]
    [InlineData("revoked", 403)]
    [InlineData("stale", 409)]
    [InlineData("missing", 404)]
    public async Task Decision_rejects_invalid_auth_or_version_without_mutation(string fault, int expected)
    {
        await using var f = await new CorrectionApprovalTests(postgres).Fixture(); var v = await CorrectionApprovalTests.Pending(f);
        await using var factory = new ApiFactory(f.Data.Database.ConnectionString, clock: f.Clock);
        using var client = await Client(factory, f, fault == "self" ? CaptureFixture.Session : CorrectionApprovalTests.HeadSession);
        var token = await IdentityTestDriver.TokenAsync(client);
        await using var db = f.Data.Database.CreateContext();
        if (fault == "mfa") await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sessions SET mfa_verified_at_utc={f.Clock.GetUtcNow().AddMinutes(-15)} WHERE id={CorrectionApprovalTests.HeadSession}");
        if (fault == "revoked") await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE users SET is_active=false,security_version=security_version+1 WHERE id={CorrectionApprovalTests.Head}");
        using var req = IdentityTestDriver.Mutation(fault == "csrf" ? "invalid" : token, $"{Root}/{(fault == "missing" ? Guid.NewGuid() : v.Id)}/supervisor-approve");
        req.Content = JsonContent.Create(CorrectionApprovalTests.Command(v) with { ExpectedVersion = fault == "stale" ? 1 : v.Version });
        using var response = await client.SendAsync(req); Assert.Equal(expected, (int)response.StatusCode);
        Assert.Empty(await db.Set<CorrectionApprovalAction>().ToArrayAsync());
        if (fault == "revoked")
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("urn:tpr10:csrf-invalid", problem.GetProperty("type").GetString());
            using var read = await client.GetAsync($"{Root}/{v.Id}"); Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Body_limit_is_enforced_only_on_correction_mutations(bool correction)
    {
        var http = new DefaultHttpContext(); var feature = new BodySize(); http.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(correction ? [new CorrectionBodyBoundary()] : Array.Empty<object>()), "test"));
        CorrectionEndpoints.ApplyBodyLimit(http); Assert.Equal(correction ? 65536 : 30000000, feature.MaxRequestBodySize);
    }
    private sealed class BodySize : IHttpMaxRequestBodySizeFeature
    { public bool IsReadOnly => false; public long? MaxRequestBodySize { get; set; } = 30000000; }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task Successful_command_rechecks_MFA_before_first_byte_without_repeating_approval()
    {
        await using var f = await new CorrectionApprovalTests(postgres).Fixture(); var v = await CorrectionApprovalTests.Pending(f);
        await using var factory = new ApiFactory(f.Data.Database.ConnectionString, clock: f.Clock);
        using var scope = factory.Services.CreateScope(); await EvidenceRevocationTests.SetSession(scope.ServiceProvider, CorrectionApprovalTests.Head);
        var http = EvidenceRevocationTests.Context(scope.ServiceProvider); http.Request.Path = Root;
        await new AttendanceJsonResult(async (s, ct) =>
        {
            var result = await s.MutateCorrectionAsync(v.Id, "supervisor-approve", null, CorrectionApprovalTests.Command(v), ct);
            Assert.IsType<CorrectionAcknowledgment>(result.Value); f.Clock.Advance(TimeSpan.FromMinutes(15)); return result;
        }).ExecuteAsync(http);
        Assert.Equal(403, http.Response.StatusCode); http.Response.Body.Position = 0;
        var body = await new StreamReader(http.Response.Body).ReadToEndAsync(); Assert.DoesNotContain(v.Id.ToString(), body); Assert.DoesNotContain("proposal", body);
        await using var db = f.Data.Database.CreateContext(); Assert.Single(await db.Set<CorrectionApprovalAction>().ToArrayAsync());
        Assert.Equal(CorrectionState.PendingHr, (await db.Set<CorrectionRequest>().SingleAsync()).State);
    }

    [Fact]
    public async Task Missing_session_cannot_read_private_routes()
    {
        await using var f = await new CorrectionApprovalTests(postgres).Fixture();
        await using var factory = new ApiFactory(f.Data.Database.ConnectionString, clock: f.Clock);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443"), AllowAutoRedirect = false });
        foreach (var path in new[] { Root, Root + "/approvals?stage=hr", Root + "/" + Guid.NewGuid() })
        { using var response = await client.GetAsync(path); Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); }
    }

    [Fact]
    public async Task Unavailable_audit_returns_503_without_logging_out_or_half_approval()
    {
        await using var f = await new CorrectionApprovalTests(postgres).Fixture(); var v = await CorrectionApprovalTests.Pending(f);
        await using var factory = new ApiFactory(f.Data.Database.ConnectionString, clock: f.Clock);
        using var head = await Client(factory, f, CorrectionApprovalTests.HeadSession);
        var token = await IdentityTestDriver.TokenAsync(head);
        await using var db = f.Data.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION test_api_fail() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic'; END $$; CREATE TRIGGER test_api_fail BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION test_api_fail()");
        using var req = IdentityTestDriver.Mutation(token, $"{Root}/{v.Id}/supervisor-approve"); req.Content = JsonContent.Create(CorrectionApprovalTests.Command(v));
        using var response = await head.SendAsync(req); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(x => x.Contains("max-age=0")));
        Assert.Empty(await db.Set<CorrectionApprovalAction>().ToArrayAsync());
    }

    internal static async Task<HttpClient> Client(ApiFactory factory, CaptureFixture f, Guid session)
    {
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        await using var db = f.Data.Database.CreateContext();
        var row = await db.Set<IdentitySession>().SingleAsync(x => x.Id == session); row.TokenHash = SessionService.HashToken(token)!; await db.SaveChangesAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", CsrfService.SessionCookieName + "=" + token); return client;
    }
    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, object body)
    {
        using var req = IdentityTestDriver.Mutation(await IdentityTestDriver.TokenAsync(client), path); req.Content = JsonContent.Create(body); return await client.SendAsync(req);
    }
    private static async Task<CorrectionView> View(HttpClient client, Guid id) => (await client.GetFromJsonAsync<CorrectionView>($"{Root}/{id}"))!;
}
