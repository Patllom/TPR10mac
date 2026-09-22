using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TPR10.Api.IntegrationTests;

public sealed class HealthAndOpenApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient client;
    public HealthAndOpenApiTests(WebApplicationFactory<Program> factory) => client = factory.CreateClient();

    [Fact]
    public async Task Liveness_returns_live_without_database()
    {
        var response = await client.GetAsync("/api/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("live", body.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task OpenApi_is_available_under_api_prefix()
    {
        var response = await client.GetAsync("/api/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.TryGetProperty("openapi", out _));
    }

    [Fact]
    public async Task Unprefixed_health_is_not_exposed()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/health")).StatusCode);
    }
}
