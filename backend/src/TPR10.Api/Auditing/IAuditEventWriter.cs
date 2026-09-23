namespace TPR10.Api.Auditing;

public interface IAuditEventWriter
{
    Task WriteAsync(string eventType, Guid targetId, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken, Guid? actorId = null);
}
