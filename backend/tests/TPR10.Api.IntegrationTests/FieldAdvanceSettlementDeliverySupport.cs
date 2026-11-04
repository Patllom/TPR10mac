using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Settlements;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementCommandTests;

namespace TPR10.Api.IntegrationTests;

internal static class FieldAdvanceSettlementDeliverySupport
{
    internal static async Task<string> Rows<T>(FieldAdvanceFixture f) where T : class
    {
        await using var db = f.CreateContext();
        var rows = await db.Set<T>().AsNoTracking().ToArrayAsync();
        return JsonSerializer.Serialize(rows.Select(x => JsonSerializer.Serialize(x)).Order(StringComparer.Ordinal));
    }

    // Sorted database values, independent of the balance calculator being verified.
    internal static async Task<string> Ledger(FieldAdvanceFixture f)
    {
        await using var db = f.CreateContext();
        var tables = await db.Database.SqlQueryRaw<string>("SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname='public' AND (tablename LIKE 'field_advance_%' OR tablename IN ('audit_events','audit_event_metadata')) ORDER BY tablename").ToArrayAsync();
        var result = new SortedDictionary<string, string>();
        foreach (var table in tables)
        {
            // Names are obtained from PostgreSQL's catalog and quoted as identifiers.
            var quoted = "\"" + table.Replace("\"", "\"\"") + "\"";
            var sql = $"SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text),'[]')::text AS \"Value\" FROM {quoted} t";
            result[table] = await db.Database.SqlQueryRaw<string>(sql).SingleAsync();
        }
        return JsonSerializer.Serialize(result);
    }

    internal static SettlementClosureService Closure(FieldAdvanceFixture f, Tpr10DbContext db)
        => new(db, new AuditEventWriter(db, new CorrelationContext(), f.Clock), f.Clock);

    internal static async Task Eligible(FieldAdvanceFixture f, Guid id)
    {
        await using var db = f.CreateContext();
        var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == id);
        var end = await Row<WorkCompletion>(f, db, r, RecipientId, RecipientPersonId);
        end.CompletedOn = new(2026, 10, 25); end.ClearingDays = 7;
        end.ClearingDueOn = end.RefundDueOn = new(2026, 11, 1);
        end.State = "Accepted"; end.AcceptedAtUtc = end.ReviewedAtUtc = f.Clock.GetUtcNow();
        end.ReviewedBy = RecipientId; end.ReviewedByPersonId = RecipientPersonId;
        db.Add(end); await db.SaveChangesAsync();
        var declaration = await Row<SubmissionDeclaration>(f, db, r);
        declaration.WorkCompletionId = end.Id; declaration.State = "Accepted";
        declaration.AcceptedAtUtc = declaration.ReviewedAtUtc = f.Clock.GetUtcNow();
        declaration.ReviewedBy = AuthorId; declaration.ReviewedByPersonId = AuthorPersonId;
        db.Add(declaration); await db.SaveChangesAsync();
    }

    internal static async Task Revoke(FieldAdvanceFixture f, Guid actor)
    {
        await using var db = f.CreateContext();
        await db.Set<GrantAssignment>().Where(x => x.RevokedAtUtc == null && db.Set<GrantProposal>().Any(p => p.Id == x.ProposalId && p.RecipientId == actor))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAtUtc, f.Clock.GetUtcNow()).SetProperty(x => x.RevokedBy, AuthorId).SetProperty(x => x.RevocationReason, "ถอนสิทธิ์ในฐานจำลอง Task18"));
    }
}
