using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.Scopes.Data;

namespace TPR10.Api.Scopes.Probes;

public sealed class ScopeProbeRepository(Tpr10DbContext db)
{
    private IQueryable<ScopeProbeRecord> Query(ScopeContext context) => db.Set<ScopeProbeRecord>()
        .Where(r => r.WorkspaceId == context.Key.WorkspaceId && r.ProjectId == context.Key.ProjectId && r.SiteId == context.Key.SiteId);

    public async Task<Page<ScopeProbeRecord>> ListAsync(ScopeContext context, int page, int pageSize, CancellationToken ct)
    {
        var query = Query(context).AsNoTracking();
        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.Id).Skip(checked((page - 1) * pageSize)).Take(pageSize).ToArrayAsync(ct);
        return new(items, total, page, pageSize);
    }

    public Task<ScopeProbeRecord?> FindAsync(ScopeContext context, Guid id, CancellationToken ct) =>
        Query(context).SingleOrDefaultAsync(r => r.Id == id, ct);

    public Task<ScopeProbeRecord[]> ExportAsync(ScopeContext context, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
        Query(context).AsNoTracking().Where(r => (from == null || r.CreatedAtUtc >= from) && (to == null || r.CreatedAtUtc < to))
            .OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.Id).Take(101).ToArrayAsync(ct);
}
