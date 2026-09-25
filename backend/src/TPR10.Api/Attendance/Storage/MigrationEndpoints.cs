using TPR10.Api.Attendance.Directory;

namespace TPR10.Api.Attendance.Storage;

public static class MigrationEndpoints
{
    public static IEndpointRouteBuilder MapAttendanceMigrations(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/attendance/storage/migrations").RequireAuthorization(StorageRegistry.Capability)
            .WithMetadata(new StorageBindingBoundary(), new DirectoryEndpointMetadata("attendance-storage", StorageRegistry.Capability));
        group.AddEndpointFilter(async (context, next) =>
        {
            if (context.HttpContext.Response.StatusCode == 400) return Results.Empty;
            context.HttpContext.Items[typeof(StorageBindingBoundary)] = true;
            return await next(context);
        });
        group.MapPost("", (MigrationService s, StartMigration request, CancellationToken ct) => s.StartAsync(request, ct)).Produces<MigrationView>(202);
        group.MapGet("", (MigrationService s, int? offset, int? limit, CancellationToken ct) => s.ListAsync(offset ?? 0, limit ?? 25, ct)).Produces<Page<MigrationView>>();
        group.MapGet("/{id}", (MigrationService s, Guid id, CancellationToken ct) => s.GetAsync(id, ct)).Produces<MigrationView>();
        group.MapPost("/{id}/resume", (MigrationService s, Guid id, ResumeMigration request, CancellationToken ct) => s.ResumeAsync(id, request, ct)).Produces<MigrationView>(202);
        return endpoints;
    }
}
