using Testcontainers.PostgreSql;
using Npgsql;

namespace TPR10.Api.IntegrationTests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17.11-alpine3.24").Build();
    public string ConnectionString => container.GetConnectionString();
    public Task InitializeAsync() => container.StartAsync();
    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    internal async Task BackupRestoreAsync(string sourceConnection, string targetConnection)
    {
        var source = new NpgsqlConnectionStringBuilder(sourceConnection);
        var target = new NpgsqlConnectionStringBuilder(targetConnection);
        var own = new NpgsqlConnectionStringBuilder(ConnectionString);
        foreach (var value in new[] { source, target })
        {
            Assert.Equal(own.Host, value.Host);
            Assert.Equal(own.Port, value.Port);
            Assert.Matches("^identity_test_[a-f0-9]{32}$", value.Database!);
        }
        Assert.NotEqual(source.Database, target.Database);
        var archive = "/tmp/tpr10-drill-" + Guid.NewGuid().ToString("N") + ".dump";
        try
        {
            var dump = await container.ExecAsync(["pg_dump", "-U", own.Username!, "-Fc", "-f", archive, source.Database!]);
            Assert.True(dump.ExitCode == 0, "pg_dump failed in disposable test container");
            var restore = await container.ExecAsync(["pg_restore", "-U", own.Username!, "--exit-on-error", "-d", target.Database!, archive]);
            Assert.True(restore.ExitCode == 0, "pg_restore failed in disposable test container");
        }
        finally
        {
            await container.ExecAsync(["rm", "-f", archive]);
        }
    }
}

[CollectionDefinition("database")]
public sealed class DatabaseCollection : ICollectionFixture<PostgresFixture> { }
