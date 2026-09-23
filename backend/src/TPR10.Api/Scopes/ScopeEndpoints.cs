using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using TPR10.Api.Correlation;
using TPR10.Api.Identity;

namespace TPR10.Api.Scopes;

public static class ScopeEndpoints
{
    public static IEndpointRouteBuilder MapScopeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Parse query scalars explicitly so invalid binding goes through durable denial audit.
        endpoints.MapGet("/api/v1/scopes", (ScopeDiscovery service, string? page, string? pageSize, CancellationToken ct)
            => service.ListAsync(Parse(page, 1), Parse(pageSize, 25), ct))
            .RequireAuthorization().Produces<Page<ScopeChoice>>()
            .AddEndpointFilter(async (context, next) =>
            {
                var result = await next(context);
                if (result is ProblemHttpResult problem)
                    problem.ProblemDetails.Extensions["correlationId"] = context.HttpContext.RequestServices.GetRequiredService<ICorrelationContext>().CorrelationId.ToString("D");
                return result;
            })
            .WithMetadata(new IdentityProblemTypes(400, "urn:tpr10:scope-invalid"),
                new IdentityProblemTypes(401, "urn:tpr10:session-required"), new IdentityProblemTypes(403, "urn:tpr10:scope-forbidden"));
        return endpoints;
    }
    private static int Parse(string? input, int fallback) => input is null ? fallback
        : int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
