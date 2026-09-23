namespace TPR10.Api.Scopes;

public sealed class ScopeContext
{
    internal ScopeContext(Guid actorId, ScopeKey key, string capability, Guid actingRoleId, Guid assignmentId, bool canReadRestricted)
        => (ActorId, Key, Capability, ActingRoleId, AssignmentId, CanReadRestricted) = (actorId, key, capability, actingRoleId, assignmentId, canReadRestricted);
    public Guid ActorId { get; }
    public ScopeKey Key { get; }
    public string Capability { get; }
    public Guid ActingRoleId { get; }
    public Guid AssignmentId { get; }
    public bool CanReadRestricted { get; }
}

public sealed record ScopeDecision(ScopeContext? Context, int? Status, string? ProblemType);
