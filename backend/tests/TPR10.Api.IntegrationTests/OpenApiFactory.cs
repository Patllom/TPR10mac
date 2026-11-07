using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TPR10.Api.FieldAdvances.Requests;
using TPR10.Api.FieldAdvances.Settlements;

namespace TPR10.Api.IntegrationTests;

// Metadata tests have no database and must not depend on a developer database.
public sealed class OpenApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // These metadata-only hosts do not provision PostgreSQL. Keep all other
        // hosted services and production registrations intact; DB-backed tests
        // continue to use ApiFactory with the real workers.
        builder.ConfigureServices(services =>
        {
            var databaseWorkers = services.Where(service => service.ServiceType == typeof(IHostedService)
                && (service.ImplementationType == typeof(RequestRoutingWorker)
                    || service.ImplementationType == typeof(SettlementClosureWorker))).ToArray();
            foreach (var worker in databaseWorkers) services.Remove(worker);
        });
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["TPR10_CONNECTION_STRING"] = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1"
            }));
    }
}
