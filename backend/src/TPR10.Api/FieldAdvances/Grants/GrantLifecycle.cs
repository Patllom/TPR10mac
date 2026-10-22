using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;

namespace TPR10.Api.FieldAdvances.Grants;

public sealed class GrantLifecycle(Tpr10DbContext db, FinanceAccess access, FinancePersonResolver people,
    IAuditEventWriter audit, TimeProvider clock)
{
    public Task<FinanceResult<GrantView>> RevokeAsync(Guid actorId, Guid grantId, CommandMeta meta, CancellationToken ct) =>
        new GrantService(db, access, people, audit, clock).RevokeAsync(actorId, grantId, meta, ct);
}
