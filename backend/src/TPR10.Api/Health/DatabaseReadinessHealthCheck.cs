using Microsoft.Extensions.Diagnostics.HealthChecks;
using TPR10.Api.Data;
namespace TPR10.Api.Health;

public sealed class DatabaseReadinessHealthCheck(Tpr10DbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        return await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy();
    }
}
