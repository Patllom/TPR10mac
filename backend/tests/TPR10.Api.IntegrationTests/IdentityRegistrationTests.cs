using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Identity;
using TPR10.Api.Identity.Passwords;

namespace TPR10.Api.IntegrationTests;

public sealed class IdentityRegistrationTests
{
    [Fact]
    public async Task Host_provides_identity_services_and_login_is_post_only()
    {
        await using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=unused;Username=test;Timeout=1");
        using var scope = factory.Services.CreateScope();
        Assert.IsType<ArgonPasswordHasher>(scope.ServiceProvider.GetRequiredService<IPasswordHasher>());
        Assert.IsType<LocalIdentityProvider>(scope.ServiceProvider.GetRequiredService<IIdentityProvider>());
        using var client = factory.CreateClient();
        Assert.Equal(System.Net.HttpStatusCode.MethodNotAllowed,
            (await client.GetAsync("/api/v1/auth/login")).StatusCode);
    }
}
