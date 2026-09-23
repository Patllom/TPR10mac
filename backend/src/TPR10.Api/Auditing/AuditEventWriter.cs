using TPR10.Api.Correlation;
using TPR10.Api.Data;
using TPR10.Api.Data.Entities;
namespace TPR10.Api.Auditing;

public sealed class AuditEventWriter(Tpr10DbContext db, ICorrelationContext correlation, TimeProvider clock) : IAuditEventWriter
{
    public Task WriteAsync(string eventType, Guid targetId, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken, Guid? actorId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = Guid.NewGuid();
        db.AuditEvents.Add(new AuditEvent
        {
            Id = id,
            EventType = eventType,
            TargetId = targetId,
            ActorId = actorId ?? (eventType is "identity.login" or "identity.logout" ? targetId : null),
            OccurredAtUtc = clock.GetUtcNow(),
            CorrelationId = correlation.CorrelationId.ToString("D")
        });
        foreach (var (key, value) in metadata)
            db.AuditMetadata.Add(new AuditEventMetadata { Id = Guid.NewGuid(), AuditEventId = id, Key = key, Value = value });
        return Task.CompletedTask;
    }
}
