namespace TPR10.Api.Data.Entities;

public sealed class AuditEvent
{
    public Guid Id { get; set; }
    public required string EventType { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public required string CorrelationId { get; set; }
    public Guid? ActorId { get; set; }
    public Guid? WorkspaceId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? TargetId { get; set; }
}
