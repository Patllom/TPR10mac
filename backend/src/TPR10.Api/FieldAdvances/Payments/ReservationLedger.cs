using TPR10.Api.Data;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.FieldAdvances.Data;

namespace TPR10.Api.FieldAdvances.Payments;

public sealed class ReservationLedger(Tpr10DbContext db, TimeProvider clock)
{
    public Task<BalanceSnapshot> ReserveAsync(Guid requestId, Guid installmentId, decimal amount, Guid commandId, CancellationToken ct)
        => ApplyAsync(requestId, installmentId, amount, commandId, "Reserve", ct);
    public Task<BalanceSnapshot> ReleaseAsync(Guid requestId, Guid installmentId, decimal amount, Guid commandId, CancellationToken ct)
        => ApplyAsync(requestId, installmentId, amount, commandId, "Release", ct);
    public Task<BalanceSnapshot> ConsumeAsync(Guid requestId, Guid installmentId, decimal amount, Guid commandId, CancellationToken ct)
        => ApplyAsync(requestId, installmentId, amount, commandId, "Consume", ct);

    private async Task<BalanceSnapshot> ApplyAsync(Guid requestId, Guid installmentId, decimal amount, Guid commandId,
        string action, CancellationToken ct)
    {
        try { return await ApplyCoreAsync(requestId, installmentId, amount, commandId, action, ct); }
        catch { PaymentCommandExecutor.FailScope(db); throw; }
    }

    private async Task<BalanceSnapshot> ApplyCoreAsync(Guid requestId, Guid installmentId, decimal amount, Guid commandId,
        string action, CancellationToken ct)
    {
        PaymentCommandExecutor.RequireScope(db, requestId, installmentId, commandId);
        if (amount <= 0 || amount > 9999999999999999.99m || decimal.Round(amount, 2) != amount)
            throw new ReservationException(invalid: true);
        var installment = await db.Set<AdvanceInstallment>().SingleAsync(x => x.Id == installmentId && x.RequestId == requestId, ct);
        var account = await db.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == requestId, ct);
        BalanceSnapshot Snapshot() => new(account.Approved, account.Spendable, account.Reserved, account.Paid);
        var available = BalanceMath.Available(Snapshot());
        var entries = (await db.Set<AdvanceReservationEntry>().AsNoTracking().Where(x => x.InstallmentId == installmentId).ToArrayAsync(ct))
            .Concat(db.ChangeTracker.Entries<AdvanceReservationEntry>().Where(x => x.State == EntityState.Added
                && x.Entity.InstallmentId == installmentId).Select(x => x.Entity)).ToArray();
        var replay = entries.SingleOrDefault(x => x.CommandId == commandId && x.Action == action);
        if (replay is not null)
        {
            if (replay.Amount != amount) throw new ReservationException();
            return Snapshot();
        }
        decimal reserved = 0;
        foreach (var entry in entries)
            reserved = checked(reserved + (entry.Action == "Reserve" ? entry.Amount : -entry.Amount));
        if (entries.Any(x => x.Action == "Consume") || reserved < 0) throw new ReservationException();
        if (action == "Reserve")
        {
            if (available < amount) throw new ReservationException();
            account.Reserved = checked(account.Reserved + amount);
        }
        else
        {
            if (reserved < amount || action == "Consume" && reserved != amount) throw new ReservationException();
            account.Reserved = checked(account.Reserved - amount);
            if (action == "Consume")
            {
                account.Paid = checked(account.Paid + amount); account.EverPaid = true;
            }
        }
        account.Version = checked(account.Version + 1);
        _ = BalanceMath.Available(Snapshot());
        db.Add(new AdvanceReservationEntry
        {
            Id = Guid.NewGuid(),
            WorkspaceId = installment.WorkspaceId,
            ProjectId = installment.ProjectId,
            FamilyId = installment.FamilyId,
            RequestId = requestId,
            InstallmentId = installmentId,
            CommandId = commandId,
            Action = action,
            Amount = amount,
            CreatedAtUtc = clock.GetUtcNow()
        });
        return Snapshot();
    }
}

internal sealed class ReservationException(bool invalid = false) : Exception("ไม่สามารถเปลี่ยนยอดกันวงเงินได้")
{
    internal bool Invalid { get; } = invalid;
}
