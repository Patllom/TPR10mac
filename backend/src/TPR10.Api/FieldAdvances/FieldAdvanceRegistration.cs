using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Grants;
using TPR10.Api.FieldAdvances.Policies;
using TPR10.Api.Identity;

namespace TPR10.Api.FieldAdvances;

public static class FieldAdvanceRegistration
{
    public static IServiceCollection AddFieldAdvances(this IServiceCollection services)
    {
        services.AddScoped<FinancePersonResolver>();
        services.AddScoped<FinanceAccess>();
        services.AddScoped<PolicyService>();
        services.AddScoped<GrantService>();
        services.AddScoped<GrantLifecycle>();
        services.AddOpenApi(options => options.AddOperationTransformer<FieldAdvanceOpenApiTransformer>());
        return services;
    }

    public static IEndpointRouteBuilder MapFieldAdvances(this IEndpointRouteBuilder endpoints)
    {
        // Session middleware handles authentication/CSRF; services require current MFA and exact finance scope.
        var group = endpoints.MapGroup("/api/v1/field-advances").RequireAuthorization()
            .WithMetadata(new IdentityProblemTypes(409));
        group.MapPost("/policies", PolicyEndpoints.CreateAsync).Produces<PolicyView>(201);
        group.MapPut("/policies/{id}", PolicyEndpoints.UpdateAsync).Produces<PolicyView>();
        group.MapPost("/policies/{id}/submit", PolicyEndpoints.SubmitAsync).Produces<PolicyView>();
        group.MapPost("/policies/{id}/review", PolicyEndpoints.ReviewAsync).Produces<PolicyView>();
        group.MapPost("/grants", GrantEndpoints.ProposeAsync).Produces<GrantView>(201);
        group.MapPost("/grants/{id}/review", GrantEndpoints.ReviewAsync).Produces<GrantView>();
        group.MapPost("/grants/{id}/revoke", GrantEndpoints.RevokeAsync).Produces<GrantView>();
        return endpoints;
    }
}
