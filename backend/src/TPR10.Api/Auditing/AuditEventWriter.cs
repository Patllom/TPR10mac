using TPR10.Api.Correlation;
using TPR10.Api.Data;
using TPR10.Api.Data.Entities;
namespace TPR10.Api.Auditing;

public sealed class AuditEventWriter(Tpr10DbContext db, ICorrelationContext correlation, TimeProvider clock,
    Identity.Authorization.PermissionContext? permission = null) : IAuditEventWriter
{
    public Task WriteAsync(SecurityAuditRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string[] allowed = ["outcome", "scope", "target-type", "changed-fields", "acting-roles", "actor-type", "reason", "evidence-reference", "source", "capability", "scope-validation", "assignment-id"];
        if (string.IsNullOrWhiteSpace(request.Action) || request.Action.Length > 120
            || string.IsNullOrWhiteSpace(request.TargetType) || request.TargetType.Length > 120
            || request.Outcome is not ("success" or "denied" or "failure")
            || request.Metadata.Any(x => !allowed.Contains(x.Key) || x.Value.Length > 1000 || x.Value.Any(char.IsControl)))
            throw new ArgumentException("Invalid or sensitive security audit metadata.", nameof(request));
        var id = Guid.NewGuid();
        db.AuditEvents.Add(new AuditEvent
        {
            Id = id,
            EventType = request.Action,
            TargetId = request.TargetId,
            ActorId = request.ActorId,
            ActingRoleId = request.ActingRoleId,
            WorkspaceId = request.WorkspaceId,
            ProjectId = request.ProjectId,
            SiteId = request.SiteId,
            TargetType = request.TargetType,
            Outcome = request.Outcome,
            OccurredAtUtc = clock.GetUtcNow(),
            CorrelationId = correlation.CorrelationId.ToString("D")
        });
        foreach (var (key, value) in request.Metadata)
            db.AuditMetadata.Add(new AuditEventMetadata { Id = Guid.NewGuid(), AuditEventId = id, Key = key, Value = value });
        return Task.CompletedTask;
    }

    // Compatibility overload; historical rows remain untouched by the new nullable columns.
    public Task WriteAsync(string eventType, Guid targetId, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken, Guid? actorId = null) =>
        WriteAsync(new SecurityAuditRequest(actorId ?? (eventType is "identity.login" or "identity.logout" ? targetId : null),
            actorId is null ? null : permission?.ActingRoleId, null, null, null, eventType,
            metadata.GetValueOrDefault("target-type") ?? (eventType.StartsWith("identity.", StringComparison.Ordinal) ? "user" : "request"),
            targetId, metadata.GetValueOrDefault("outcome") ?? "success", metadata), cancellationToken);
}
