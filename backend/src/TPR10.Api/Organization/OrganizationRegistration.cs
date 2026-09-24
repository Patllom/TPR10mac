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
        services.AddScoped<Scopes.Probes.ScopeProbeRepository>();
        services.AddScoped<Scopes.Probes.ScopeProbeService>();
        services.AddScoped<Scopes.Probes.ScopeExportService>();
        return services;
    }
}
