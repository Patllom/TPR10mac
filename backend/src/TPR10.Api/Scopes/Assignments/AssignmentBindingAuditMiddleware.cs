namespace TPR10.Api.Scopes.Assignments;

// Authorized binding failures need durable denial audit, without reading body data.
public sealed class AssignmentBindingAuditMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, AssignmentService service)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<AssignmentBindingBoundary>() is null) { await next(context); return; }
        try { await next(context); }
        catch (BadHttpRequestException error) when (error.StatusCode == 400 && !context.Response.HasStarted && !context.Items.ContainsKey(typeof(AssignmentBindingBoundary)))
        { context.Response.StatusCode = 400; }
        if (context.Response.StatusCode != 400 || context.Response.HasStarted || context.Items.ContainsKey(typeof(AssignmentBindingBoundary))) return;
        Guid? id = Guid.TryParse(context.Request.RouteValues["id"]?.ToString(), out var value) ? value : null;
        var result = await service.RejectBindingAsync(id, context.RequestAborted);
        context.Response.Clear();
        await result.ExecuteAsync(context);
    }
}
