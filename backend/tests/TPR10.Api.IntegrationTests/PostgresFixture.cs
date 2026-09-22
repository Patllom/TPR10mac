using Testcontainers.PostgreSql;

namespace TPR10.Api.IntegrationTests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17.11-alpine3.24").Build();
    public string ConnectionString => container.GetConnectionString();
    public Task InitializeAsync() => container.StartAsync();
    public Task DisposeAsync() => container.DisposeAsync().AsTask();
}

[CollectionDefinition("database")]
public sealed class DatabaseCollection : ICollectionFixture<PostgresFixture> { }
