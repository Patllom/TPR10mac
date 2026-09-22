namespace TPR10.Api.Data.Entities;

public sealed class AuditEventMetadata
{
    public Guid Id { get; set; }
    public Guid AuditEventId { get; set; }
    public required string Key { get; set; }
    public required string Value { get; set; }
}
