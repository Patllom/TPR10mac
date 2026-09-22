using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class DatabaseFoundationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Readiness_checks_database_and_liveness_survives_outage()
    {
        await using var ready = new ApiFactory(postgres.ConnectionString);
        using var client = ready.CreateClient();
        var response = await client.GetAsync("/api/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ready", body.RootElement.GetProperty("status").GetString());

        await using var unavailable = new ApiFactory("Host=127.0.0.1;Port=1;Database=unavailable;Username=test;Timeout=1");
        using var offline = unavailable.CreateClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await offline.GetAsync("/api/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await offline.GetAsync("/api/health/live")).StatusCode);
    }

    [Fact]
    public async Task Migration_roundtrip_preserves_append_only_audit_contract()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetService<DbContext>();
        Assert.NotNull(db);
        await db.Database.MigrateAsync();
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using (var tables = new NpgsqlCommand("SELECT count(*) FROM information_schema.tables WHERE table_schema='public' AND table_name IN ('technical_probes','audit_events','audit_event_metadata')", connection))
            Assert.Equal(3L, await tables.ExecuteScalarAsync());
        var id = Guid.NewGuid();
        await using (var seed = new NpgsqlCommand("INSERT INTO audit_events(id,event_type,occurred_at_utc,correlation_id) VALUES (@id,'test',now(),@correlation); INSERT INTO audit_event_metadata(id,audit_event_id,key,value) VALUES (@metadata,@id,'source','test')", connection))
        {
            seed.Parameters.AddWithValue("id", id);
            seed.Parameters.AddWithValue("correlation", Guid.NewGuid().ToString("D"));
            seed.Parameters.AddWithValue("metadata", Guid.NewGuid());
            await seed.ExecuteNonQueryAsync();
        }
        foreach (var table in new[] { "audit_events", "audit_event_metadata" })
        {
            foreach (var sql in new[] { $"UPDATE {table} SET id=id", $"DELETE FROM {table}", $"TRUNCATE {table} CASCADE" })
            {
                await using var mutate = new NpgsqlCommand(sql, connection);
                var error = await Assert.ThrowsAsync<PostgresException>(() => mutate.ExecuteNonQueryAsync());
                Assert.Equal("P0001", error.SqlState);
            }
        }
        var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync("0");
        await db.Database.MigrateAsync();
        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
    }
}
