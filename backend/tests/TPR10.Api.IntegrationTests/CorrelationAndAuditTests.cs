using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Identity.Data;
using static TPR10.Api.IntegrationTests.AuthorizationTests;
using static TPR10.Api.IntegrationTests.MfaTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class CorrelationAndAuditTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("9B3C7C0D-7FC1-4114-9081-686D367AF924")]
    [InlineData("not-a-guid")]
    [InlineData(null)]
    public async Task Mutation_records_matching_correlation_and_audit(string? incoming)
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        var actor = await ReadyAsync(driver);
        if (incoming is not null) driver.Client.DefaultRequestHeaders.Add("X-Correlation-ID", incoming);
        driver.Client.DefaultRequestHeaders.Add("X-Acting-Role-ID", Guid.NewGuid().ToString());
        using var response = await driver.PostAsync("/api/v1/system/technical-probes", new { note = " module-1-proof ", actingRoleId = Guid.NewGuid(), actorId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var correlation = response.Headers.GetValues("X-Correlation-ID").Single();
        Assert.True(Guid.TryParseExact(correlation, "D", out _));
        if (Guid.TryParse(incoming, out var valid)) Assert.Equal(valid.ToString("D"), correlation);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(correlation, body.RootElement.GetProperty("correlationId").GetString());
        await using var db = driver.Database.CreateContext();
        var probe = await db.TechnicalProbes.SingleAsync(x => x.Id == id);
        Assert.Equal("module-1-proof", probe.Note);
        Assert.Equal(correlation, probe.CorrelationId);
        var audit = await db.AuditEvents.SingleAsync(x => x.TargetId == id);
        Assert.Equal("technical.probe.created", audit.EventType);
        Assert.Equal(correlation, audit.CorrelationId);
        Assert.Equal(actor, audit.ActorId);
        Assert.Equal((await db.Set<UserRole>().SingleAsync(x => x.UserId == actor)).RoleId, audit.ActingRoleId);
        Assert.Equal("technical-probe", audit.TargetType);
        Assert.Equal("success", audit.Outcome);
        Assert.Null(audit.WorkspaceId); Assert.Null(audit.ProjectId); Assert.Null(audit.SiteId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Empty_note_is_rejected(string? note)
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await ReadyAsync(driver);
        Assert.Equal(HttpStatusCode.BadRequest, (await driver.PostAsync("/api/v1/system/technical-probes", new { note })).StatusCode);
    }

    [Fact]
    public async Task Oversized_note_is_rejected()
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await ReadyAsync(driver);
        Assert.Equal(HttpStatusCode.BadRequest, (await driver.PostAsync("/api/v1/system/technical-probes", new { note = new string('a', 501) })).StatusCode);
    }

    [Fact]
    public async Task Production_does_not_expose_mutation()
    {
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(postgres.ConnectionString, "Production", settings: keys.Settings);
        using var client = await factory.CreateCsrfClientAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/v1/system/technical-probes", new { note = "proof" })).StatusCode);
    }

    [Fact]
    public async Task Audit_failure_rolls_back_probe_and_returns_correlated_problem()
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await ReadyAsync(driver);
        var token = await IdentityTestDriver.TokenAsync(driver.Client);
        await using var db = driver.Database.CreateContext();
        var before = await db.TechnicalProbes.CountAsync();
        var auditBefore = await db.AuditEvents.CountAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION test_reject_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected audit failure'; END $$; CREATE TRIGGER test_reject_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION test_reject_audit();");
        using var request = IdentityTestDriver.Mutation(token, "/api/v1/system/technical-probes");
        using var response = await driver.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(Guid.TryParse(response.Headers.GetValues("X-Correlation-ID").Single(), out _));
        Assert.DoesNotContain("injected audit failure", await response.Content.ReadAsStringAsync());
        Assert.Equal(before, await db.TechnicalProbes.CountAsync());
        Assert.Equal(auditBefore, await db.AuditEvents.CountAsync());
    }

    internal static async Task<Guid> ReadyAsync(IdentityTestDriver driver)
    {
        var actor = await driver.SeedUserAsync("probe", Password, ["system:probe"]);
        Assert.Equal(HttpStatusCode.OK, (await driver.LoginAsync("probe", Password)).StatusCode);
        await ConfirmAsync(driver);
        return actor;
    }
}
