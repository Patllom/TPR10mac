using Microsoft.EntityFrameworkCore;
using Npgsql;
using TPR10.Api.Data;

namespace TPR10.Api.IntegrationTests;

internal sealed class IdentityDatabase(string adminConnectionString) : IAsyncDisposable
{
    private readonly string name = "identity_test_" + Guid.NewGuid().ToString("N");
    public string ConnectionString => new NpgsqlConnectionStringBuilder(adminConnectionString)
    { Database = name, Pooling = false }.ConnectionString;

    public async Task InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
        await command.ExecuteNonQueryAsync();
    }

    public Tpr10DbContext CreateContext() => new(new DbContextOptionsBuilder<Tpr10DbContext>().UseNpgsql(ConnectionString).Options);

    public async ValueTask DisposeAsync()
    {
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}
