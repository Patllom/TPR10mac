using TPR10.Api.Identity.Passwords;

namespace TPR10.Api.Identity;

public static class IdentityRegistration
{
    public static IServiceCollection AddIdentityFoundation(this IServiceCollection services)
    {
        services.AddSingleton<IPasswordHasher, ArgonPasswordHasher>();
        services.AddScoped<IIdentityProvider, LocalIdentityProvider>();
        return services;
    }
}
