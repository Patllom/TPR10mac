using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Evidence;
using static TPR10.Api.FieldAdvances.Payments.InstallmentService;

namespace TPR10.Api.FieldAdvances.Payments;

public sealed class PaymentExecutionService(Tpr10DbContext db, PaymentCommandExecutor executor, ReservationLedger ledger,
    FinancePersonResolver people, FinanceEvidenceService evidence, FinanceEvidenceOptions options, TimeProvider clock)
{
    public async Task<FinanceResult<PaymentView>> StartAsync(Guid actor, Guid installmentId, PaymentCommand meta, CancellationToken ct)
    {
        var requestId = await RequestIdAsync(db, installmentId, ct);
        if (requestId is null) return Denied();
        return await executor.ExecuteAsync(actor, requestId.Value, installmentId, "start", meta,
            PaymentCommandExecutor.Hash(new { Operation = "payment.start" }), async token =>
            {
                var installment = await db.Set<AdvanceInstallment>().SingleAsync(x => x.Id == installmentId, token);
                if (installment.State != "Reviewed" || !await EligibleAsync(db, requestId.Value, installmentId, token)) return Conflict();
                var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == requestId, token);
                var revision = await db.Set<AdvanceInstallmentRevision>().SingleOrDefaultAsync(x => x.InstallmentId == installmentId && x.IsCurrent, token);
                if (!ReviewedRevision(revision, request.OwnerPersonId)
                    || !await ReservationMatchesAsync(db, installment, revision!.Amount, token)
                    || await db.Set<AdvancePaymentAttempt>().AnyAsync(x => x.InstallmentId == installmentId && x.State != "Unpaid", token)
                    || await db.Set<AdvancePaymentResult>().AnyAsync(x => x.InstallmentId == installmentId, token)) return Conflict();
                var person = await people.ResolveAsync(actor, request.WorkspaceId, token);
                if (person is null) return Denied();
                db.Add(new AdvancePaymentAttempt
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = installment.WorkspaceId,
                    ProjectId = installment.ProjectId,
                    FamilyId = installment.FamilyId,
                    RequestId = requestId.Value,
                    InstallmentId = installmentId,
                    RevisionId = revision!.Id,
                    CommandId = meta.CommandId,
                    PayerPersonId = person.Value,
                    PayerId = actor,
                    State = "Open",
                    CreatedAtUtc = clock.GetUtcNow()
                });
                installment.State = "Paying";
                return Success(requestId.Value, installmentId);
            }, ct);
    }

    public async Task<FinanceResult<PaymentView>> RecordAsync(Guid actor, Guid installmentId, ActualPaymentInput input,
        PaymentCommand meta, CancellationToken ct)
    {
        var requestId = await RequestIdAsync(db, installmentId, ct);
        if (requestId is null) return Denied();
        // Receipt existence only selects preparation; the executor alone authenticates and validates replay.
        // A committed command must not depend on whether its files are still available today.
        var hasReceipt = await db.Set<FinanceCommandReceipt>().AsNoTracking().AnyAsync(x => x.CommandId == meta.CommandId, ct);
        AdvanceEvidence[] prepared = [];
        var ready = false;
        if (!hasReceipt && ValidEvidenceIds(input?.EvidenceIds))
        {
            // Capture before I/O, so the final locked comparison covers the exact verified metadata.
            prepared = await db.Set<AdvanceEvidence>().AsNoTracking().Where(x => input!.EvidenceIds.Contains(x.Id)).ToArrayAsync(ct);
            ready = await evidence.ValidateLinksAsync(actor, requestId.Value, input!.EvidenceIds, ct);
        }
        return await executor.ExecuteAsync(actor, requestId.Value, installmentId, "record", meta,
            PaymentCommandExecutor.Hash(new { Operation = "payment.record", Input = input }), async token =>
            {
                var installment = await db.Set<AdvanceInstallment>().SingleAsync(x => x.Id == installmentId, token);
                if (installment.State != "Paying") return Conflict();
                var attempt = await db.Set<AdvancePaymentAttempt>().SingleOrDefaultAsync(x => x.InstallmentId == installmentId && x.State == "Open", token);
                if (attempt is null) return Conflict();
                if ((await PaymentRecovery.OwnerAsync(db, attempt, token)).TakeoverId is not null) return Conflict();
                var person = await people.ResolveAsync(actor, installment.WorkspaceId, token);
                if (person is null || attempt.PayerId != actor || attempt.PayerPersonId != person) return Denied();
                var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == requestId, token);
                var revision = await db.Set<AdvanceInstallmentRevision>().SingleOrDefaultAsync(x => x.Id == attempt.RevisionId && x.InstallmentId == installmentId && x.IsCurrent, token);
                if (!ReviewedRevision(revision, request.OwnerPersonId)
                    || !await ReservationMatchesAsync(db, installment, revision!.Amount, token)
                    || await db.Set<AdvancePaymentResult>().AnyAsync(x => x.InstallmentId == installmentId, token)) return Conflict();
                if (input is null || !TryAmount(input.Amount, out var amount) || input.PaidOn == default
                    || input.PaidOn == DateOnly.MaxValue) return Invalid();
                // Mismatches keep the open attempt and reservation for the separate incident workflow.
                if (amount != revision!.Amount || input.RecipientPersonId != revision.RecipientPersonId) return Conflict();
                if (!ready || !ValidEvidenceIds(input.EvidenceIds)) return Invalid();
                var rows = await db.Set<AdvanceEvidence>().FromSqlInterpolated(
                    $"SELECT * FROM field_advance_evidence WHERE id = ANY({input.EvidenceIds}) ORDER BY id FOR UPDATE").ToArrayAsync(token);
                if (rows.Length != input.EvidenceIds.Length || prepared.Length != rows.Length
                    || rows.Any(x => x.State != "Ready" || x.RequestId != requestId || x.WorkspaceId != installment.WorkspaceId
                        || x.ProjectId != installment.ProjectId || x.FamilyId != installment.FamilyId
                        || x.InstallmentId != null && x.InstallmentId != installmentId
                        || x.Length <= 0 || x.Length > options.MaxFileBytes
                        || !prepared.Any(before => before.Id == x.Id && before.RequestId == x.RequestId
                            && before.State == "Ready" && before.Sha256 == x.Sha256 && before.Length == x.Length))
                    || rows.Sum(x => x.Length) > options.MaxTotalBytes) return Conflict();
                // Holds stop new starts, not reconciliation of an already-started external payment.
                await ledger.ConsumeAsync(requestId.Value, installmentId, amount, meta.CommandId, token);
                db.Add(new AdvancePaymentResult
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = installment.WorkspaceId,
                    ProjectId = installment.ProjectId,
                    FamilyId = installment.FamilyId,
                    RequestId = requestId.Value,
                    InstallmentId = installmentId,
                    AttemptId = attempt.Id,
                    CommandId = meta.CommandId,
                    Amount = amount,
                    RecipientPersonId = input.RecipientPersonId,
                    PaidOn = input.PaidOn,
                    Reference = input.Reference,
                    EvidenceIdsJson = JsonSerializer.Serialize(input.EvidenceIds),
                    RecordedBy = actor,
                    RecordedByPersonId = person.Value,
                    CreatedAtUtc = clock.GetUtcNow()
                });
                attempt.State = "Matched"; installment.State = "Paid";
                return Success(requestId.Value, installmentId);
            }, ct);
    }

    private bool ValidEvidenceIds(Guid[]? ids) => ids is { Length: > 0 } && ids.Length <= options.MaxFiles
        && ids.All(x => x != Guid.Empty) && ids.Distinct().Count() == ids.Length;

    private static bool ReviewedRevision(AdvanceInstallmentRevision? revision, Guid owner)
        => revision is not null && revision.ReviewedAtUtc is not null && revision.ReviewedBy is not null
            && revision.ReviewedByPersonId is not null && ValidRevision(revision, owner);

    private static bool TryAmount(string? text, out decimal amount)
    {
        amount = 0;
        if (string.IsNullOrEmpty(text)) return false;
        var parts = text.Split('.');
        return parts.Length <= 2 && parts.All(x => x.Length > 0 && x.All(c => c is >= '0' and <= '9'))
            && (parts.Length == 1 || parts[1].Length <= 2)
            && decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount)
            && amount > 0 && amount <= 9999999999999999.99m;
    }
}
