namespace TPR10.Api.Correlation;

public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, CorrelationContext correlation)
    {
        var incoming = http.Request.Headers["X-Correlation-ID"];
        correlation.Initialize(incoming.Count == 1 && Guid.TryParse(incoming[0], out var parsed) ? parsed : Guid.NewGuid());
        var value = correlation.CorrelationId.ToString("D");
        http.Response.OnStarting(() =>
        {
            http.Response.Headers["X-Correlation-ID"] = value;
            return Task.CompletedTask;
        });
        await next(http);
    }
}
