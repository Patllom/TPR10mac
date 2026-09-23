namespace TPR10.Api.Organization;

public static class OrganizationRegistration
{
    public static IServiceCollection AddOrganizationScope(this IServiceCollection services)
    {
        services.AddScoped<OrganizationService>();
        services.AddScoped<Scopes.Assignments.AssignmentService>();
        services.AddScoped<Scopes.ScopeAccess>();
        services.AddScoped<Scopes.ScopeOperation>();
        services.AddScoped<Scopes.ScopeDiscovery>();
        return services;
    }
}
