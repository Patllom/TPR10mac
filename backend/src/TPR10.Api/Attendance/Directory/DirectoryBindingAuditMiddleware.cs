namespace TPR10.Api.Attendance.Directory;

public sealed record DirectoryBindingBoundary;

public sealed class DirectoryBindingAuditMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, DirectoryService service)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<DirectoryBindingBoundary>() is null) { await next(context); return; }
        try { await next(context); }
        catch (BadHttpRequestException e) when (e.StatusCode == 400 && !context.Response.HasStarted && !context.Items.ContainsKey(typeof(DirectoryBindingBoundary)))
        { context.Response.StatusCode = 400; }
        if (context.Response.StatusCode != 400 || context.Response.HasStarted || context.Items.ContainsKey(typeof(DirectoryBindingBoundary))) return;
        var result = await service.RejectBindingAsync(context.RequestAborted);
        context.Response.Clear();
        await result.ExecuteAsync(context);
    }
}
