namespace TPR10.Api.Auditing;

public interface IAuditEventWriter
{
    Task WriteAsync(SecurityAuditRequest request, CancellationToken ct);
    Task WriteAsync(string eventType, Guid targetId, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken, Guid? actorId = null);
}
