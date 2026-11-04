using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Settlements;

namespace TPR10.Api.FieldAdvances.Payments;

internal static class PaymentCapacity
{
    // Use tracked components: the generated column is refreshed only after SaveChanges.
    internal static decimal Usage(AdvancePaymentAccount account)
        => checked(account.Paid + account.IncidentReservationUsage - account.ActivatedShortfall);
    internal static decimal Available(AdvancePaymentAccount account)
        => BalanceMath.Available(new(account.Approved, account.Spendable, account.Reserved, Usage(account)));
    internal static Task<AdvancePaymentAccount> LockAsync(Tpr10DbContext db, Guid request, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("ต้องล็อกบัญชีภายในธุรกรรม");
        return db.Set<AdvancePaymentAccount>().FromSqlInterpolated($"SELECT * FROM field_advance_payment_accounts WHERE request_id={request} FOR UPDATE").SingleAsync(ct);
    }
    internal static async Task<bool> ClaimFitsAsync(Tpr10DbContext db, Guid incidentId, decimal amount, Guid? exclude, CancellationToken ct)
    {
        var incident = await db.Set<PaymentIncidentResolution>().SingleAsync(x => x.Id == incidentId, ct);
        var snapshot = IncidentAccounting.Read(incident);
        if (incident.State != "Accepted" || snapshot is null) return false;
        var claimed = await db.Set<PaymentShortfallClaim>().Where(x => x.IncidentId == incidentId && x.Id != exclude && x.State != "Cancelled").SumAsync(x => x.Amount, ct);
        return amount <= snapshot.Source.InstallmentAmount - incident.OwnerReceived - claimed;
    }
}
