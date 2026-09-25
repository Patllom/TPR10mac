using TPR10.Api.Attendance.Directory;

namespace TPR10.Api.Attendance.Storage;

public sealed record StorageBindingBoundary;

public static class StorageEndpoints
{
    public static IEndpointRouteBuilder MapAttendanceStorage(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/attendance/storage").RequireAuthorization(StorageRegistry.Capability)
            .WithMetadata(new StorageBindingBoundary(), new DirectoryEndpointMetadata("attendance-storage", StorageRegistry.Capability));
        group.AddEndpointFilter(async (context, next) =>
        {
            if (context.HttpContext.Response.StatusCode == 400) return Results.Empty;
            context.HttpContext.Items[typeof(StorageBindingBoundary)] = true;
            return await next(context);
        });
        group.MapGet("/options", (StorageRegistry s, int? offset, int? limit, CancellationToken ct) => s.OptionsAsync(offset ?? 0, limit ?? 25, ct)).Produces<Page<StorageOptionView>>();
        group.MapGet("/locations", (StorageRegistry s, int? offset, int? limit, CancellationToken ct) => s.LocationsAsync(offset ?? 0, limit ?? 25, ct)).Produces<Page<StorageView>>();
        group.MapPost("/locations", (StorageRegistry s, RegisterStorage request, CancellationToken ct) => s.RegisterAsync(request, ct)).Produces<StorageView>(201);
        group.MapPost("/locations/{id}/probe", (StorageRegistry s, Guid id, ProbeStorage request, CancellationToken ct) => s.ProbeAsync(id, request, ct)).Produces<StorageHealthView>();
        group.MapGet("/write-target", (StorageRegistry s, CancellationToken ct) => s.TargetAsync(ct)).Produces<StorageTargetView>();
        group.MapPost("/write-target", (StorageRegistry s, SwitchWriteTarget request, CancellationToken ct) => s.SwitchAsync(request, ct)).Produces<StorageTargetView>();
        group.MapGet("/health", (StorageRegistry s, int? offset, int? limit, CancellationToken ct) => s.HealthAsync(offset ?? 0, limit ?? 25, ct)).Produces<Page<StorageHealthView>>();
        return endpoints;
    }
}

public sealed class StorageBindingAuditMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, StorageRegistry service)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<StorageBindingBoundary>() is null) { await next(context); return; }
        try { await next(context); }
        catch (BadHttpRequestException e) when (e.StatusCode == 400 && !context.Response.HasStarted && !context.Items.ContainsKey(typeof(StorageBindingBoundary)))
        { context.Response.StatusCode = 400; }
        if (context.Response.StatusCode != 400 || context.Response.HasStarted || context.Items.ContainsKey(typeof(StorageBindingBoundary))) return;
        var result = await service.RejectBindingAsync(context.RequestAborted);
        context.Response.Clear(); await result.ExecuteAsync(context);
    }
}
