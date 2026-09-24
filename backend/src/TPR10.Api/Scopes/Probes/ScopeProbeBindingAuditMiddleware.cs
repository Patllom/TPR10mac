using Microsoft.AspNetCore.Http.HttpResults;

namespace TPR10.Api.Scopes.Probes;

public sealed class ScopeProbeBindingAuditMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ScopeProbeService service)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<ScopeProbeBoundary>() is not { } boundary) { await next(context); return; }
        try { await next(context); }
        catch (BadHttpRequestException error) when (error.StatusCode == 400 && !context.Response.HasStarted && !context.Items.ContainsKey(typeof(ScopeProbeBoundary)))
        { context.Response.StatusCode = 400; }
        if (context.Response.StatusCode != 400 || context.Response.HasStarted || context.Items.ContainsKey(typeof(ScopeProbeBoundary))) return;
        Guid Parse(string name) => Guid.TryParse(context.Request.RouteValues[name]?.ToString(), out var id) ? id : Guid.Empty;
        var key = new ScopeKey(Parse("workspaceId"), boundary.Level is "project" or "site" ? Parse("projectId") : null, boundary.Level == "site" ? Parse("siteId") : null);
        var result = await service.RejectBindingAsync(key, boundary.Capability, context.RequestAborted);
        if (result is ProblemHttpResult problem) ScopeProbeEndpoints.Correlate(context, problem);
        context.Response.Clear();
        await result.ExecuteAsync(context);
    }
}
