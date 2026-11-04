using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Payments;

namespace TPR10.Api.FieldAdvances.Settlements;

public sealed class SettlementClosureService(Tpr10DbContext db, IAuditEventWriter audit, TimeProvider clock)
{
    // Server operation only. No actor or client-supplied financial facts authorize closure.
    internal async Task<bool> TryCloseAsync(Guid requestId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null) throw new InvalidOperationException("การปิดต้องเป็นเจ้าของธุรกรรม");
        db.ChangeTracker.Clear();
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var request = await FamilyParticipation.LockRequestAsync(db, requestId, ct);
            if (request is null || request.State != "Approved" || request.HoldCode is not null
                || await db.Set<ClosureRecord>().AnyAsync(x => x.RequestId == requestId, ct)) return false;
            var end = await SettlementCompletionService.CurrentEndAsync(db, requestId, ct);
            var declaration = await SettlementCompletionService.CurrentDeclarationAsync(db, requestId, ct);
            if (end is null || declaration is null || declaration.IsWithdrawal
                || declaration.ProposedByPersonId != request.OwnerPersonId) return false;
            var balance = await SettlementBalanceReader.ReadLockedAsync(db, requestId, ct);
            var totals = balance.Value;
            if (balance.Status != 200 || totals is null || totals.OwnerOutstanding != 0 || totals.WrongRecipientOutstanding != 0
                || totals.PendingRefund != 0 || totals.HasOpenCase || await HasUnfinishedAsync(db, requestId, ct)) return false;
            var account = await db.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == requestId, ct);
            if (account.Reserved != 0 || account.Spendable != PaymentCapacity.Usage(account) || totals.ApprovedExpense > account.Approved) return false;
            var family = await db.Set<AdvanceFamily>().SingleAsync(x => x.Id == request.FamilyId, ct);
            request.State = "Closed"; request.Version = checked(request.Version + 1);
            family.Version = checked(family.Version + 1);
            var closure = new ClosureRecord
            {
                Id = Guid.NewGuid(),
                WorkspaceId = request.WorkspaceId,
                ProjectId = request.ProjectId,
                FamilyId = request.FamilyId,
                RequestId = requestId,
                ActorKind = "System",
                State = "Accepted",
                CreatedAtUtc = clock.GetUtcNow(),
                AcceptedAtUtc = clock.GetUtcNow(),
                Reason = "ระบบปิดเมื่อสะสางครบ",
                WorkCompletionId = end.Id,
                DeclarationId = declaration.Id,
                RequestVersion = request.Version,
                FamilyVersion = family.Version,
                SnapshotJson = JsonSerializer.Serialize(new
                {
                    Totals = totals,
                    WorkCompletionId = end.Id,
                    DeclarationId = declaration.Id,
                    AccountId = account.Id,
                    AccountVersion = account.Version,
                    account.ApprovedRevisionId,
                    account.Approved,
                    account.Spendable,
                    account.Reserved,
                    account.Paid,
                    account.IncidentReservationUsage,
                    account.ActivatedShortfall,
                    Payments = await db.Set<AdvancePaymentResult>().Where(x => x.RequestId == requestId).OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync(ct),
                    Expenses = await db.Set<SettlementRevision>().Where(x => x.RequestId == requestId && x.AcceptedAtUtc != null).OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync(ct),
                    Refunds = await db.Set<Refund>().Where(x => x.RequestId == requestId && x.AcceptedAtUtc != null).OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync(ct),
                    Corrections = await db.Set<RefundCorrection>().Where(x => x.RequestId == requestId && x.AcceptedAtUtc != null).OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync(ct),
                    Incidents = await db.Set<PaymentIncidentResolution>().Where(x => x.RequestId == requestId && x.AcceptedAtUtc != null).OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync(ct),
                    Recoveries = await db.Set<PaymentIncidentRecovery>().Where(x => x.RequestId == requestId && x.AcceptedAtUtc != null).OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync(ct),
                    Dispositions = await db.Set<AdvancePaymentProposal>().Where(x => x.RequestId == requestId && x.Kind == "PaymentDisposition" && x.State == "Approved").OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync(ct)
                })
            };
            db.Add(closure);
            await audit.WriteAsync(new(null, null, request.WorkspaceId, request.ProjectId, null,
                "field-advance.settlement.close", "field-advance-request", requestId, "success",
                new Dictionary<string, string>
                {
                    ["actor-type"] = "system",
                    ["source"] = "settlement-closure-worker",
                    ["evidence-reference"] = $"{closure.Id:D},{end.Id:D},{declaration.Id:D}"
                }), ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        }
        finally { db.ChangeTracker.Clear(); }
    }

    // Draft rounds block above their historical revisions; only live Reserved allocations
    // consume document capacity. Consumed/Settled claims and superseded proposals are history.
    internal static async Task<bool> HasUnfinishedAsync(Tpr10DbContext db, Guid requestId, CancellationToken ct)
        => await db.Set<SettlementRound>().AnyAsync(x => x.RequestId == requestId && x.AcceptedAtUtc == null, ct)
        || await db.Set<Refund>().AnyAsync(x => x.RequestId == requestId && x.AcceptedAtUtc == null && x.State != "Cancelled", ct)
        || await db.Set<RefundCorrection>().AnyAsync(x => x.RequestId == requestId && x.AcceptedAtUtc == null && x.State != "Cancelled" && x.State != "Rejected" && x.State != "Superseded", ct)
        || await db.Set<WorkCompletion>().AnyAsync(x => x.RequestId == requestId && x.AcceptedAtUtc == null && x.State != "Rejected" && x.State != "Cancelled", ct)
        || await db.Set<PaymentIncidentResolution>().AnyAsync(x => x.RequestId == requestId && x.AcceptedAtUtc == null && x.State != "Rejected" && x.State != "Cancelled", ct)
        || await db.Set<PaymentIncidentRecovery>().AnyAsync(x => x.RequestId == requestId && x.AcceptedAtUtc == null && x.State != "Rejected" && x.State != "Cancelled", ct)
        || await db.Set<SettlementCasePayment>().AnyAsync(x => x.RequestId == requestId && x.AcceptedAtUtc == null && x.State != "Rejected" && x.State != "Cancelled" && x.State != "Superseded", ct)
        || await db.Set<SettlementExcessPayment>().AnyAsync(x => x.RequestId == requestId && x.AcceptedAtUtc == null && x.State != "Rejected" && x.State != "Cancelled" && x.State != "Superseded", ct)
        || await db.Set<ExpenseAllocation>().AnyAsync(x => x.RequestId == requestId && x.AcceptedAtUtc == null && x.State == "Reserved" && x.Amount > 0
            && !db.Set<SettlementRevision>().Any(r => r.Id == x.RevisionId && r.AcceptedAtUtc != null), ct)
        || await db.Set<AdvancePaymentProposal>().AnyAsync(x => x.RequestId == requestId && x.State == "Pending"
            && !(x.Kind == "PaymentMismatch" && db.Set<AdvancePaymentHold>().Any(h => h.SourceProposalId == x.Id && h.ReleasedAtUtc != null
                && db.Set<PaymentIncidentResolution>().Any(i => i.HoldId == h.Id && i.AcceptedAtUtc != null
                    && db.Set<IncidentPaymentAdjustment>().Any(a => a.IncidentId == i.Id)))), ct)
        || await db.Set<AdvanceInstallment>().AnyAsync(x => x.RequestId == requestId && x.State != "Paid" && x.State != "Cancelled" && x.State != "IncidentResolved", ct)
        || await db.Set<AdvancePaymentAttempt>().AnyAsync(x => x.RequestId == requestId && x.State != "Matched" && x.State != "Unpaid"
            && !db.Set<IncidentPaymentAdjustment>().Any(a => a.AttemptId == x.Id), ct)
        || await db.Set<PaymentShortfallClaim>().AnyAsync(x => x.RequestId == requestId && x.State != "Settled" && x.State != "Consumed" && x.State != "Cancelled", ct);
}
