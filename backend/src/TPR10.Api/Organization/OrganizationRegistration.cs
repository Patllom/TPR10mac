namespace TPR10.Api.Organization;

public static class OrganizationRegistration
{
    public static IServiceCollection AddOrganizationScope(this IServiceCollection services)
    {
        services.AddScoped<OrganizationService>();
        services.AddScoped<Scopes.Assignments.AssignmentService>();
        return services;
    }
}
