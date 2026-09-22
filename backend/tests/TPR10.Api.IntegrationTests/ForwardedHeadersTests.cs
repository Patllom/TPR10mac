using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace TPR10.Api.IntegrationTests;

public sealed class ForwardedHeadersTests
{
    [Theory]
    [InlineData("127.0.0.1", "https")]
    [InlineData("::1", "https")]
    [InlineData("192.0.2.50", "http")]
    public async Task Only_loopback_proxy_can_supply_scheme(string remote, string expected)
    {
        var observed = "";
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(new CaptureFilter(remote, scheme => observed = scheme))));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        await client.GetAsync("/api/health/live");
        Assert.Equal(expected, observed);
    }

    private sealed class CaptureFilter(string remote, Action<string> capture) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (HttpContext context, RequestDelegate downstream) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
                await downstream(context);
                capture(context.Request.Scheme);
            });
            next(app);
        };
    }
}
