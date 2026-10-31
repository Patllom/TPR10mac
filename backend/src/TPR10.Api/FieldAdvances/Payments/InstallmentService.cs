using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;

namespace TPR10.Api.FieldAdvances.Payments;

public sealed class InstallmentService(Tpr10DbContext db, PaymentCommandExecutor executor, ReservationLedger ledger, TimeProvider clock)
{
    public Task<FinanceResult<PaymentView>> CreateAsync(Guid actor, Guid requestId, InstallmentInput input, PaymentCommand meta, CancellationToken ct)
        => executor.ExecuteAsync(actor, requestId, null, "prepare", meta,
            PaymentCommandExecutor.Hash(new { Operation = "installment.create", Input = input }), async token =>
            {
                var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == requestId, token);
                if (!ValidInput(input, request.OwnerPersonId, out var amount)) return Invalid();
                if (!await EligibleAsync(db, requestId, null, token)) return Conflict();
                var installment = new AdvanceInstallment
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = request.WorkspaceId,
                    ProjectId = request.ProjectId,
                    FamilyId = request.FamilyId,
                    RequestId = requestId,
                    CreatedBy = actor,
                    CreatedAtUtc = clock.GetUtcNow()
                };
                db.Add(installment);
                db.Add(Revision(installment, input, amount, 1, actor, meta.Reason));
                // The executor must see this as Added, and owns the generated-ID receipt and all versions.
                return Success(requestId, installment.Id);
            }, ct);

    public async Task<FinanceResult<PaymentView>> UpdateAsync(Guid actor, Guid installmentId, InstallmentInput input, PaymentCommand meta, CancellationToken ct)
    {
        var requestId = await RequestIdAsync(db, installmentId, ct);
        if (requestId is null) return Denied();
        return await executor.ExecuteAsync(actor, requestId.Value, installmentId, "prepare", meta,
            PaymentCommandExecutor.Hash(new { Operation = "installment.update", Input = input }), async token =>
            {
                var installment = await db.Set<AdvanceInstallment>().SingleAsync(x => x.Id == installmentId, token);
                var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == requestId, token);
                if (!ValidInput(input, request.OwnerPersonId, out var amount)) return Invalid();
                if (installment.State is not ("Draft" or "PendingReview" or "Returned" or "Reviewed")
                    || !await EligibleAsync(db, requestId.Value, installmentId, token)) return Conflict();
                var current = await db.Set<AdvanceInstallmentRevision>().SingleOrDefaultAsync(x => x.InstallmentId == installmentId && x.IsCurrent, token);
                if (current is null || !ValidRevision(current, request.OwnerPersonId) || current.RevisionNumber == int.MaxValue
                    || !await ReservationMatchesAsync(db, installment, current.Amount, token)) return Conflict();
                if (installment.State != "Draft")
                {
                    var difference = amount - current.Amount;
                    if (difference > 0) await ledger.ReserveAsync(requestId.Value, installmentId, difference, meta.CommandId, token);
                    else if (difference < 0) await ledger.ReleaseAsync(requestId.Value, installmentId, -difference, meta.CommandId, token);
                    installment.State = "Returned";
                }
                // Flush the old current flag before inserting: PostgreSQL's partial unique index is immediate.
                current.IsCurrent = false;
                await db.SaveChangesAsync(token);
                db.Add(Revision(installment, input, amount, current.RevisionNumber + 1, actor, meta.Reason));
                return Success(requestId.Value, installmentId);
            }, ct);
    }

    public async Task<FinanceResult<PaymentView>> SubmitAsync(Guid actor, Guid installmentId, PaymentCommand meta, CancellationToken ct)
    {
        var requestId = await RequestIdAsync(db, installmentId, ct);
        if (requestId is null) return Denied();
        return await executor.ExecuteAsync(actor, requestId.Value, installmentId, "prepare", meta,
            PaymentCommandExecutor.Hash(new { Operation = "installment.submit" }), async token =>
            {
                var installment = await db.Set<AdvanceInstallment>().SingleAsync(x => x.Id == installmentId, token);
                if (installment.State is not ("Draft" or "Returned")
                    || !await EligibleAsync(db, requestId.Value, installmentId, token)) return Conflict();
                var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == requestId, token);
                var current = await db.Set<AdvanceInstallmentRevision>().SingleOrDefaultAsync(x => x.InstallmentId == installmentId && x.IsCurrent, token);
                if (current is null || !ValidRevision(current, request.OwnerPersonId) || current.ReviewedAtUtc is not null
                    || !await ReservationMatchesAsync(db, installment, current.Amount, token)) return Conflict();
                if (installment.State == "Draft") await ledger.ReserveAsync(requestId.Value, installmentId, current.Amount, meta.CommandId, token);
                installment.State = "PendingReview";
                return Success(requestId.Value, installmentId);
            }, ct);
    }

    internal static Task<Guid?> RequestIdAsync(Tpr10DbContext db, Guid installmentId, CancellationToken ct)
        => db.Set<AdvanceInstallment>().AsNoTracking().Where(x => x.Id == installmentId).Select(x => (Guid?)x.RequestId).SingleOrDefaultAsync(ct);

    internal static async Task<bool> EligibleAsync(Tpr10DbContext db, Guid requestId, Guid? installmentId, CancellationToken ct)
    {
        var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == requestId, ct);
        var account = await db.Set<AdvancePaymentAccount>().SingleOrDefaultAsync(x => x.RequestId == requestId, ct);
        if (request.State != "Approved" || request.HoldCode is not null || account?.ApprovedRevisionId is null
            || account.Approved <= 0 || account.Spendable <= 0) return false;
        var approved = await db.Set<AdvanceRequestRevision>().SingleOrDefaultAsync(x => x.Id == account.ApprovedRevisionId
            && x.RequestId == requestId, ct);
        if (approved is null || !approved.IsCurrent || approved.Total != account.Approved || approved.SubmittedAtUtc is null || approved.ConfirmedByPersonId != request.OwnerPersonId
            || !await db.Set<AdvanceApprovalStep>().AnyAsync(x => x.RevisionId == approved.Id, ct)
            || await db.Set<AdvanceApprovalStep>().AnyAsync(x => x.RevisionId == approved.Id && x.Outcome != "Approved", ct)) return false;
        return !await db.Set<AdvancePaymentHold>().AnyAsync(x => x.FamilyId == request.FamilyId && x.ReleasedAtUtc == null
            && (x.RequestId == null || x.RequestId == requestId)
            && (x.InstallmentId == null || x.InstallmentId == installmentId), ct);
    }

    internal static async Task<bool> ReservationMatchesAsync(Tpr10DbContext db, AdvanceInstallment installment, decimal amount, CancellationToken ct)
    {
        var entries = await db.Set<AdvanceReservationEntry>().AsNoTracking().Where(x => x.InstallmentId == installment.Id).ToArrayAsync(ct);
        if (entries.Any(x => x.Action is not ("Reserve" or "Release"))) return false;
        var reserved = entries.Sum(x => x.Action == "Reserve" ? x.Amount : -x.Amount);
        return reserved == (installment.State == "Draft" ? 0 : amount);
    }

    internal static bool ValidRevision(AdvanceInstallmentRevision revision, Guid owner)
    {
        var input = new InstallmentInput(revision.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            new(revision.Channel, revision.RecipientPersonId, revision.BankCode, revision.AccountNumber, revision.AccountHolder));
        return ValidInput(input, owner, out _) && revision.ContentHash == PaymentCommandExecutor.Hash(input);
    }

    private static bool ValidInput(InstallmentInput? input, Guid owner, out decimal amount)
    {
        amount = 0;
        if (input?.Amount is not { Length: > 0 } text || input.Destination is not { } destination) return false;
        var parts = text.Split('.');
        if (parts.Length > 2 || parts.Any(x => x.Length == 0 || x.Any(c => c is < '0' or > '9'))
            || parts.Length == 2 && parts[1].Length > 2
            || !decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount)
            || amount <= 0 || amount > 9999999999999999.99m || destination.RecipientPersonId != owner) return false;
        return destination.Channel switch
        {
            "Cash" => destination.BankCode is null && destination.AccountNumber is null && destination.AccountHolder is null,
            "Transfer" => !string.IsNullOrWhiteSpace(destination.BankCode) && !string.IsNullOrWhiteSpace(destination.AccountNumber)
                && !string.IsNullOrWhiteSpace(destination.AccountHolder),
            _ => false
        };
    }

    private AdvanceInstallmentRevision Revision(AdvanceInstallment installment, InstallmentInput input, decimal amount, int number, Guid actor, string reason)
        => new()
        {
            Id = Guid.NewGuid(),
            WorkspaceId = installment.WorkspaceId,
            ProjectId = installment.ProjectId,
            FamilyId = installment.FamilyId,
            RequestId = installment.RequestId,
            InstallmentId = installment.Id,
            RevisionNumber = number,
            IsCurrent = true,
            Amount = amount,
            Channel = input.Destination.Channel,
            RecipientPersonId = input.Destination.RecipientPersonId,
            BankCode = input.Destination.BankCode,
            AccountNumber = input.Destination.AccountNumber,
            AccountHolder = input.Destination.AccountHolder,
            ContentHash = PaymentCommandExecutor.Hash(input with { Amount = amount.ToString("0.00", CultureInfo.InvariantCulture) }),
            Reason = reason,
            CreatedBy = actor,
            CreatedAtUtc = clock.GetUtcNow()
        };

    internal static FinanceResult<PaymentView> Success(Guid request, Guid installment)
        => new(200, "payment-success", new(request, installment, 0, 0, null, "", 0, 0, 0, 0, 0, []));
    internal static FinanceResult<PaymentView> Invalid() => new(400, "invalid-payment", null);
    internal static FinanceResult<PaymentView> Conflict() => new(409, "payment-conflict", null);
    internal static FinanceResult<PaymentView> Denied() => new(403, "finance-access-denied", null);
}
