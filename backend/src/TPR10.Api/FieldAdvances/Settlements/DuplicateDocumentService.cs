using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Payments;
namespace TPR10.Api.FieldAdvances.Settlements;

public sealed class DuplicateDocumentService(Tpr10DbContext db, SettlementAccess access, SettlementCommandExecutor executor, TimeProvider clock)
{
    public Task<FinanceResult<SettlementCommandResult>> ReviewDuplicateAsync(Guid actor, Guid requestId, Guid duplicateId,
        bool SameDocument, SettlementCommand command, CancellationToken ct)
        => executor.ExecuteAsync(actor, requestId, SettlementActions.DuplicateReview, duplicateId, command,
            SettlementCommandExecutor.Hash(new { operation = "review-duplicate", duplicateId, SameDocument }), async token =>
        {
            var row = await db.Set<ExpenseDuplicateCase>().SingleAsync(x => x.Id == duplicateId, token);
            var doc = await db.Set<ExpenseDocument>().AsNoTracking().SingleAsync(x => x.Id == row.DocumentId, token);
            var other = await db.Set<ExpenseDocument>().AsNoTracking().SingleAsync(x => x.Id == row.OtherDocumentId, token);
            if (row.State != "Pending" || row.DocumentVersion != doc.Version || row.OtherDocumentVersion != other.Version)
                return new(409, "duplicate-review-stale", null);
            var decision = await access.CheckAsync(actor, requestId, SettlementActions.DuplicateReview, duplicateId, token);
            if (!decision.Allowed || decision.PersonId is null) return new(403, "finance-access-denied", null);
            var otherRequest = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == other.RequestId, token);
            await FamilyParticipation.RecordAuthorizedAsync(db, otherRequest, decision.PersonId.Value, "Checker", actor, clock.GetUtcNow(), token);
            row.State = SameDocument ? "SameDocument" : "DistinctDocument";
            row.ReviewedBy = actor; row.ReviewedByPersonId = decision.PersonId; row.ReviewedAtUtc = clock.GetUtcNow(); row.Reason = command.Reason;
            // No other request identifiers, metadata, amounts or scope versions in the response.
            return new(200, "duplicate-reviewed", new(requestId, row.Id, row.State, 0, 0, row.Version));
        }, ct);

    // Submission/allocation consumers must use the version-bound status for either side,
    // not a historical document.State or a review from an older document revision.
    public async Task<bool> IsBlockedAsync(Guid actor, Guid requestId, Guid documentId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null) throw new InvalidOperationException("ต้องเป็นเจ้าของธุรกรรม");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockRegistryAsync(db, requestId, ct);
        if (!(await access.CheckAsync(actor, requestId, SettlementActions.Read, null, ct)).Allowed) return true;
        return await IsBlockedLockedAsync(db, requestId, documentId, ct);
    }

    // Caller owns registry -> family -> request locks for the whole financial command.
    internal static async Task<bool> IsBlockedLockedAsync(Tpr10DbContext db, Guid requestId, Guid documentId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("ต้องตรวจเอกสารภายในธุรกรรม");
        var doc = await db.Set<ExpenseDocument>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == documentId && x.RequestId == requestId, ct);
        if (doc is null) return true;
        var cases = await db.Set<ExpenseDuplicateCase>().AsNoTracking()
            .Where(x => x.DocumentId == documentId || x.OtherDocumentId == documentId).ToArrayAsync(ct);
        foreach (var pair in cases.GroupBy(x => x.DocumentId == documentId ? x.OtherDocumentId : x.DocumentId))
        {
            var other = await db.Set<ExpenseDocument>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == pair.Key, ct);
            if (other is null) return true;
            var current = pair.Where(x => x.DocumentId == documentId
                ? x.DocumentVersion == doc.Version && x.OtherDocumentVersion == other.Version
                : x.OtherDocumentVersion == doc.Version && x.DocumentVersion == other.Version).ToArray();
            if (current.Length == 0 || current.Any(x => x.State != "DistinctDocument")) return true;
        }
        return false;
    }

    internal static async Task LockRegistryAsync(Tpr10DbContext db, Guid requestId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("ต้องล็อกทะเบียนภายในธุรกรรม");
        // All document writers enter here BEFORE any family lock. The existing 7C global
        // participation gate comes next, then database GUID order, then request/entity rows.
        // Dedicated one-argument registry key: 7241002 is identity, 7241003 CSRF, 7241004 login.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241005)", ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct);
        var workspace = await db.Set<AdvanceRequest>().AsNoTracking().Where(x => x.Id == requestId).Select(x => (Guid?)x.WorkspaceId).SingleOrDefaultAsync(ct);
        if (workspace is null) return;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM field_advance_families WHERE workspace_id={workspace.Value} ORDER BY id FOR UPDATE", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM field_advance_requests WHERE workspace_id={workspace.Value} ORDER BY id FOR UPDATE", ct);
    }
}
