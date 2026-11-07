using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace TPR10.Api.IntegrationTests;

public sealed class OpenApiHostLifetimeTests
{
    [Fact]
    public async Task Database_free_metadata_host_survives_beyond_background_worker_tick()
    {
        await using var factory = new OpenApiFactory();
        using var client = factory.CreateClient();
        var lifetime = factory.Services.GetRequiredService<IHostApplicationLifetime>();
        using var initial = await client.GetAsync("/api/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);

        // Both production workers first poll after 30 seconds. A quick metadata
        // request alone misses the delayed StopHost/disposed-provider regression.
        await Task.Delay(TimeSpan.FromSeconds(35));

        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
        using var response = await client.GetAsync("/api/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/v1/auth/session", out _));
        Assert.True(paths.TryGetProperty("/api/v1/scopes", out _));
        using var live = await client.GetAsync("/api/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }
}
