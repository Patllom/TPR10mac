namespace TPR10.Api.Organization;

public static class OrganizationRegistration
{
    public static IServiceCollection AddOrganizationScope(this IServiceCollection services)
    {
        services.AddScoped<OrganizationService>();
        return services;
    }
}
