using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace TPR10.Api.IntegrationTests;

public sealed class ApiFactory(string connectionString, string environment = "Testing", TimeProvider? clock = null,
    Dictionary<string, string?>? settings = null, DbCommandInterceptor? interceptor = null) : WebApplicationFactory<Program>
{
    public async Task<HttpClient> CreateCsrfClientAsync()
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<Tpr10DbContext>().Database.MigrateAsync();
        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443") });
        var token = await IdentityTestDriver.TokenAsync(client);
        client.DefaultRequestHeaders.Add("Origin", "https://localhost:4443");
        client.DefaultRequestHeaders.Add("X-CSRF-Token", token);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        if (interceptor is not null)
            builder.ConfigureServices(services => services.AddDbContext<Tpr10DbContext>((_, options) => options.AddInterceptors(interceptor)));
        if (clock is not null)
            builder.ConfigureServices(services => services.AddSingleton(clock));
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?> { ["TPR10_CONNECTION_STRING"] = connectionString }));
        if (settings is not null)
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
    }
}
