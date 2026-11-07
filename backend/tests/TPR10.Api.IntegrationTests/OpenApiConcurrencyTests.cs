using System.Net;
using System.Text.Json;

namespace TPR10.Api.IntegrationTests;

public sealed class OpenApiConcurrencyTests
{
    [Fact]
    public async Task Concurrent_document_requests_return_complete_documents()
    {
        await using var factory = new OpenApiFactory();
        using var client = factory.CreateClient();
        for (var round = 0; round < 3; round++)
        {
            await Task.WhenAll(Enumerable.Range(0, 16).Select(async request =>
            {
                using var response = await client.GetAsync("/api/openapi/v1.json");
                var body = await response.Content.ReadAsStringAsync();
                Assert.True(response.StatusCode == HttpStatusCode.OK,
                    $"Round {round}, request {request}: {response.StatusCode}: {body}");
                using var document = JsonDocument.Parse(body);
                var paths = document.RootElement.GetProperty("paths");
                Assert.True(paths.TryGetProperty("/api/v1/auth/session", out _));
                Assert.True(paths.TryGetProperty("/api/v1/scopes", out _));
                Assert.True(document.RootElement.GetProperty("components").GetProperty("schemas").EnumerateObject().Any());
            }));
        }
    }
}
