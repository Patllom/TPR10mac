using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.Identity.Sessions;

namespace TPR10.Api.FieldAdvances.Policies;

internal sealed class PolicyReadGuard(Tpr10DbContext db, FinanceAccess access, RequestSession session)
{
    internal async Task<FinanceResult<T>> Execute<T>(Guid actor, FinanceScope scope, bool reviewOnly,
        Func<Task<FinanceResult<T>>> read, CancellationToken ct)
    {
        // All public readers own their transaction. Preview uses the resolver's authorized core
        // in this same transaction, so no nested transaction or authorization gap is introduced.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct);
        if (!await Allowed(actor, scope, reviewOnly, ct)) return Denied<T>();
        var result = await read();
        // Time-based expiry must also hold at return; the identity lock orders revocations.
        if (!await Allowed(actor, scope, reviewOnly, ct)) return Denied<T>();
        await tx.CommitAsync(ct);
        return result;
    }

    private async Task<bool> Allowed(Guid actor, FinanceScope scope, bool reviewOnly, CancellationToken ct)
    {
        if (actor == Guid.Empty || session.Entity?.UserId != actor || scope is null) return false;
        if ((await access.CheckAsync(actor, scope, "policy.review", ct)).Allowed) return true;
        return !reviewOnly && (await access.CheckAsync(actor, scope,
            scope.ProjectId is null ? "policy.edit.company" : "policy.edit.project", ct)).Allowed;
    }

    internal static FinanceResult<T> Denied<T>() => new(403, "finance-access-denied", default);
    internal static FinanceResult<T> Conflict<T>() => new(409, "policy-conflict", default);
    internal static FinanceResult<T> Invalid<T>() => new(400, "policy-invalid", default);
}
