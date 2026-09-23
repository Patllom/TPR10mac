using TPR10.Api.Identity.Passwords;
using TPR10.Api.Identity.Sessions;
using Microsoft.AspNetCore.Authentication;
using System.Text.Json.Serialization;

namespace TPR10.Api.Identity;

public static class IdentityRegistration
{
    public static IServiceCollection AddIdentityFoundation(this IServiceCollection services)
    {
        services.AddSingleton<IPasswordHasher, ArgonPasswordHasher>();
        services.AddScoped<IIdentityProvider, LocalIdentityProvider>();
        services.AddScoped<RequestSession>();
        services.AddScoped<LoginService>();
        services.AddScoped<ISessionService, SessionService>();
        services.AddAuthentication(SessionAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionAuthenticationHandler.SchemeName, _ => { });
        services.AddAuthorization();
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter<SessionStage>()));
        return services;
    }
}
