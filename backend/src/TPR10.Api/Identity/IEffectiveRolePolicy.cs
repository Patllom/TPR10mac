namespace TPR10.Api.Identity;

public interface IEffectiveRolePolicy
{
    Task<bool> RequiresMfaAsync(Guid userId, CancellationToken ct);
    Task<Guid[]> AffectedUsersAsync(Guid roleId, CancellationToken ct);
}
