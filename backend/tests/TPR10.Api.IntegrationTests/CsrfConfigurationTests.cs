using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TPR10.Api.Identity.Csrf;

namespace TPR10.Api.IntegrationTests;

public sealed class CsrfConfigurationTests
{
    [Fact]
    public async Task Production_without_explicit_origin_and_protected_keyring_refuses_start()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    [Theory]
    [InlineData("Identity:Csrf:AllowedOrigins:0", "http://localhost:4443")]
    [InlineData("Identity:Csrf:AllowedOrigins:0", "https://localhost:4443/path")]
    [InlineData("Identity:Csrf:MaxActiveFlows", "0")]
    [InlineData("Identity:Csrf:LifetimeMinutes", "0")]
    [InlineData("Identity:Csrf:PerIpPermitLimit", "0")]
    public async Task Invalid_security_configuration_refuses_start(string key, string value)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })));
        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }
}
