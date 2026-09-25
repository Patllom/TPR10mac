namespace TPR10.Api.Attendance.Storage;

// Protected deployment configuration, never an HTTP request/response model.
public sealed class StorageOptions
{
    public StorageDefinition[] Locations { get; set; } = [];
    public bool HealthWorkerEnabled { get; set; } = true;
    public bool MigrationWorkerEnabled { get; set; } = true;
}
