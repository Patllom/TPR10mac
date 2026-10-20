using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;

namespace TPR10.Api.FieldAdvances.Access;

public sealed class FinancePersonResolver(Tpr10DbContext db, TimeProvider clock)
{
    public async Task<Guid?> ResolveAsync(Guid userId, Guid workspaceId, CancellationToken ct)
    {
        if (userId == Guid.Empty || workspaceId == Guid.Empty) return null;
        var now = clock.GetUtcNow();
        if (await DirectoryQueries.Employment(db, now)
                .CountAsync(x => x.UserId == userId && x.WorkspaceId == workspaceId, ct) != 1) return null;
        var identities = await (from link in db.Set<FinanceAccountLink>().AsNoTracking()
                                join person in db.Set<FinancePerson>()
                                    on new { link.WorkspaceId, Id = link.PersonId } equals new { person.WorkspaceId, person.Id }
                                where link.UserId == userId && link.WorkspaceId == workspaceId
                                    && link.CreatedAtUtc <= now && link.EndedAtUtc == null
                                select person.Id).Take(2).ToArrayAsync(ct);
        return identities.Length == 1 && identities[0] != Guid.Empty ? identities[0] : null;
    }
}
