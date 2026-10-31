using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;

namespace TPR10.Api.FieldAdvances.Evidence;

// Report only. Historical locations and staged references are retained; no delete operation exists.
public sealed class FinanceEvidenceReconciler(Tpr10DbContext db, IFinanceEvidenceStore store)
{
    public async Task<EvidenceLocator[]> FindOrphansAsync(CancellationToken ct)
    {
        if (db.Database.CurrentTransaction != null) throw new InvalidOperationException("Reconciliation must run outside financial locks.");
        var files = store.Enumerate().ToArray();
        var referenced = (await db.Set<AdvanceEvidenceLocation>().AsNoTracking().Select(x => x.StorageKey).ToArrayAsync(ct)).ToHashSet(StringComparer.Ordinal);
        return files.Where(x => !referenced.Contains(x.ToString())).ToArray();
    }
}
