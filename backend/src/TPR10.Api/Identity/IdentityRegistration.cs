using TPR10.Api.Identity.Passwords;
using TPR10.Api.Identity.Accounts;
using TPR10.Api.Identity.Mfa;
using TPR10.Api.Identity.Sessions;
using Microsoft.AspNetCore.Authentication;
using System.Text.Json.Serialization;

namespace TPR10.Api.Identity;

public static class IdentityRegistration
{
    public static IServiceCollection AddIdentityFoundation(this IServiceCollection services)
    {
        services.AddOpenApi(options =>
        {
            options.AddOperationTransformer<IdentityOpenApiTransformer>();
            options.AddDocumentTransformer<IdentityOpenApiTransformer>();
        });
        services.AddSingleton<IPasswordHasher, ArgonPasswordHasher>();
        services.AddScoped<IIdentityProvider, LocalIdentityProvider>();
        services.AddScoped<RequestSession>();
        services.AddScoped<LoginService>();
        services.AddScoped<Reset.PasswordResetService>();
        services.AddScoped<IResetDelivery, Reset.ResetDelivery>();
        services.AddScoped<AccountProvisioning>();
        services.AddScoped<RoleAdministration>();
        services.AddScoped<Authorization.PermissionMutationGuard>();
        services.AddScoped<BootstrapService>();
        services.AddScoped<MfaService>();
        services.AddScoped<OperatorMfaRecovery>();
        services.AddScoped<ISessionService, SessionService>();
        services.AddAuthentication(SessionAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionAuthenticationHandler.SchemeName, _ => { });
        services.AddScoped<Authorization.PermissionContext>();
        services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, Authorization.PermissionHandler>();
        services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, Authorization.PermissionResultHandler>();
        services.AddAuthorization(options =>
        {
            foreach (var capability in new[] { "system:probe", "users:manage", "roles:manage", "roles:read", "users:recover-mfa" })
                options.AddPolicy(capability, policy => policy.RequireAuthenticatedUser()
                    .AddRequirements(new Authorization.PermissionRequirement(capability, RequireMfa: true)));
        });
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter<SessionStage>()));
        return services;
    }
}
