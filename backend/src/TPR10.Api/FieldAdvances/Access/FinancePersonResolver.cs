using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;

namespace TPR10.Api.FieldAdvances.Access;

public sealed class FinancePersonResolver(Tpr10DbContext db, TimeProvider clock)
{
    // Operation-local results only. Uniqueness is checked before projecting any person.
    internal async Task<Dictionary<(Guid User, Guid Workspace), Guid>> ResolveManyAsync(
        (Guid User, Guid Workspace)[] identities, CancellationToken ct)
    {
        var result = new Dictionary<(Guid User, Guid Workspace), Guid>();
        foreach (var batch in identities.Where(x => x.User != Guid.Empty && x.Workspace != Guid.Empty).Distinct().Chunk(100))
        {
            var users = batch.Select(x => x.User).Distinct().ToArray();
            var workspaces = batch.Select(x => x.Workspace).Distinct().ToArray();
            var now = clock.GetUtcNow();
            var links = await (from link in db.Set<FinanceAccountLink>().AsNoTracking()
                               join person in db.Set<FinancePerson>()
                                   on new { link.WorkspaceId, Id = link.PersonId } equals new { person.WorkspaceId, person.Id }
                               where users.Contains(link.UserId) && workspaces.Contains(link.WorkspaceId)
                                   && link.CreatedAtUtc <= now && link.EndedAtUtc == null
                                   && DirectoryQueries.Employment(db, now).Count(x => x.UserId == link.UserId && x.WorkspaceId == link.WorkspaceId) == 1
                               select new { link.UserId, link.WorkspaceId, person.Id }).ToArrayAsync(ct);
            foreach (var group in links.GroupBy(x => (x.UserId, x.WorkspaceId)))
                if (batch.Contains(group.Key) && group.Count() == 1 && group.First().Id != Guid.Empty)
                    result[group.Key] = group.First().Id;
        }
        return result;
    }

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
