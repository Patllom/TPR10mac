using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TPR10.Api.Data;

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
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>();
        await db.Database.MigrateAsync();
        using var client = factory.CreateClient();
        if (incoming is not null) client.DefaultRequestHeaders.Add("X-Correlation-ID", incoming);
        var response = await client.PostAsJsonAsync("/api/v1/system/technical-probes", new { note = " module-1-proof " });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var correlation = response.Headers.GetValues("X-Correlation-ID").Single();
        Assert.True(Guid.TryParseExact(correlation, "D", out _));
        if (Guid.TryParse(incoming, out var valid)) Assert.Equal(valid.ToString("D"), correlation);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(correlation, body.RootElement.GetProperty("correlationId").GetString());
        var probe = await db.TechnicalProbes.SingleAsync(x => x.Id == id);
        Assert.Equal("module-1-proof", probe.Note);
        Assert.Equal(correlation, probe.CorrelationId);
        var audit = await db.AuditEvents.SingleAsync(x => x.TargetId == id);
        Assert.Equal("technical.probe.created", audit.EventType);
        Assert.Equal(correlation, audit.CorrelationId);
        Assert.Equal("module-1-controlled-endpoint",
            (await db.AuditMetadata.SingleAsync(x => x.AuditEventId == audit.Id && x.Key == "source")).Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Empty_note_is_rejected(string? note)
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/v1/system/technical-probes", new { note })).StatusCode);
    }

    [Fact]
    public async Task Oversized_note_is_rejected()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/v1/system/technical-probes", new { note = new string('a', 501) })).StatusCode);
    }

    [Fact]
    public async Task Production_does_not_expose_mutation()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString, "Production");
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/api/v1/system/technical-probes", new { note = "proof" })).StatusCode);
    }

    [Fact]
    public async Task Audit_failure_rolls_back_probe_and_returns_correlated_problem()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>();
        await db.Database.MigrateAsync();
        var before = await db.TechnicalProbes.CountAsync();
        var auditBefore = await db.AuditEvents.CountAsync();
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using (var fault = new NpgsqlCommand("""
            CREATE FUNCTION test_reject_audit() RETURNS trigger AS $$
            BEGIN RAISE EXCEPTION 'injected audit failure'; END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER test_reject_audit BEFORE INSERT ON audit_events
            FOR EACH ROW EXECUTE FUNCTION test_reject_audit();
            """, connection))
            await fault.ExecuteNonQueryAsync();
        try
        {
            using var client = factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/v1/system/technical-probes", new { note = "must-rollback" });
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.True(Guid.TryParse(response.Headers.GetValues("X-Correlation-ID").Single(), out _));
            Assert.DoesNotContain("injected audit failure", await response.Content.ReadAsStringAsync());
            Assert.Equal(before, await db.TechnicalProbes.CountAsync());
            Assert.Equal(auditBefore, await db.AuditEvents.CountAsync());
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand("DROP TRIGGER test_reject_audit ON audit_events; DROP FUNCTION test_reject_audit()", connection);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
