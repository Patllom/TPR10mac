using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;

namespace TPR10.Api.FieldAdvances.Settlements;

public sealed class SettlementReviewService(Tpr10DbContext db, SettlementAccess access,
    SettlementCommandExecutor executor, SettlementRouteResolver routes, SettlementPolicyResolver policies, TimeProvider clock)
{
    public Task<FinanceResult<SettlementCommandResult>> ReviewAsync(Guid actor, Guid requestId, Guid roundId,
        ReviewInput input, SettlementCommand command, CancellationToken ct)
        => executor.ExecuteRoundAsync(actor, requestId, roundId, false, command,
            SettlementCommandExecutor.Hash(new { operation = "review-settlement", roundId, input }), async (stepId, token) =>
        {
            if (input is null || input.Decision is not ("Approve" or "Return") || string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Length > 2000)
                return new(422, "settlement-review-invalid", null);
            var step = await db.Set<SettlementApprovalStep>().SingleAsync(x => x.Id == stepId, token);
            var revision = await db.Set<SettlementRevision>().SingleAsync(x => x.Id == step.RevisionId, token);
            var round = await db.Set<SettlementRound>().SingleAsync(x => x.Id == roundId, token);
            if (step.State != "Pending" || revision.State != "Submitted" || round.State != "Submitted"
                || revision.AcceptedAtUtc is not null || step.ReviewedAtUtc is not null) return Conflict("settlement-review-stale");
            var route = await routes.CurrentStepAsync(step, token);
            var permission = await access.CheckAsync(actor, requestId, step.LevelCode == "Finance" ? SettlementActions.Check : SettlementActions.Approve, stepId, token);
            if (!permission.Allowed || permission.PersonId is null || route.PersonId != permission.PersonId || route.AccountId != actor)
                return new(403, "finance-access-denied", null);
            var allocations = await db.Set<ExpenseAllocation>().Where(x => x.RevisionId == revision.Id).ToArrayAsync(token);
            var planned = await ExpenseAllocationLedger.PlannedAsync(db, revision.Id, allocations, token);
            if (input.Decision == "Approve")
            {
                var preview = await SettlementSubmissionService.BuildPreviewAsync(db, revision, token);
                using var snapshot = JsonDocument.Parse(revision.SnapshotJson);
                if (preview is null || !snapshot.RootElement.TryGetProperty("ConfirmationHash", out var hash) || hash.GetString() != preview.ConfirmationHash)
                    return Conflict("settlement-confirmation-stale");
                foreach (var doc in allocations.Select(x => x.DocumentId).Distinct())
                    if (await DuplicateDocumentService.IsBlockedLockedAsync(db, requestId, doc, token)) return Conflict("document-duplicate-pending");
                if (!await ExpenseAdjustmentService.ValidAsync(db, revision, token) || planned is null || planned.Values.Sum() != revision.Total
                    || !await ExpenseAllocationLedger.ValidReservationAsync(db, revision.Id, allocations, planned, token))
                    return Conflict("document-allocation-invalid");
            }
            // Returning stale content is the recovery path to a new draft/owner confirmation;
            // it neither certifies the content nor releases its retained document reservations.
            var at = clock.GetUtcNow();
            if (input.Decision == "Return")
            {
                var correction = await policies.ResolveCorrectionAsync(requestId, token);
                if (correction.Value is null) return new(correction.Status, correction.Code, null);
                var local = TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById("Asia/Bangkok"));
                DateOnly due;
                try { due = DateOnly.FromDateTime(local.DateTime).AddDays(correction.Value.Days); }
                catch (ArgumentOutOfRangeException) { return Conflict("correction-window-out-of-range"); }
                // Return applies to the entire round. Retain every affected line ID and the
                // approved time basis here; Task 7 can read this immutable decision history.
                var lineIds = await db.Set<SettlementLine>().Where(x => x.RevisionId == revision.Id).OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync(token);
                step.SnapshotJson = JsonSerializer.Serialize(new { Route = route, LineIds = lineIds, CorrectionWindow = correction.Value, ReturnedAtUtc = at, TimeZone = "Asia/Bangkok", CorrectionDueOn = due });
                step.State = "Returned"; revision.State = "Returned"; revision.CorrectionDueOn = due;
                revision.Version++; round.State = "Returned"; round.Version++;
            }
            else
            {
                if (step.StepOrder == 3)
                {
                    var approved = await db.Set<AdvancePaymentAccount>().Where(x => x.RequestId == requestId).Select(x => x.Approved).SingleAsync(token);
                    var used = await ExpenseAdjustmentService.ExpenseTotalAsync(db, requestId, token);
                    var adjustment = await db.Set<SettlementExpenseAdjustment>().SingleOrDefaultAsync(x => x.RevisionId == revision.Id, token);
                    var delta = adjustment?.Delta ?? revision.Total;
                    if (used is null || delta > approved - used) return Conflict("settlement-budget-exceeded");
                    var balance = await SettlementBalanceReader.ReadLockedAsync(db, requestId, token);
                    if (balance.Value is null) return Conflict(balance.Code);
                    // Expense-only negative balances remain visible. Refund claims/receipts,
                    // however, cannot be spent a second time by a concurrent final approval.
                    if ((balance.Value.PendingRefund > 0 || balance.Value.ConfirmedRefund > 0)
                        && delta > 0 && delta > balance.Value.OwnerOutstanding - balance.Value.PendingRefund)
                        return Conflict("refund-capacity-exceeded");
                }
                step.State = "Accepted"; step.AcceptedAtUtc = at; step.SnapshotJson = JsonSerializer.Serialize(route);
            }
            step.AssigneePersonId = permission.PersonId; step.ReviewedBy = actor; step.ReviewedByPersonId = permission.PersonId;
            step.ReviewedAtUtc = at; step.Reason = input.Reason.Trim();
            if (input.Decision == "Approve" && step.StepOrder == 3)
            {
                foreach (var row in allocations)
                {
                    row.Amount = planned![row.Id];
                    row.State = "Accepted"; row.AcceptedAtUtc = at; row.ReviewedAtUtc = at; row.ReviewedBy = actor;
                    row.ReviewedByPersonId = permission.PersonId; row.Version++;
                }
                var adjustment = await db.Set<SettlementExpenseAdjustment>().SingleOrDefaultAsync(x => x.RevisionId == revision.Id, token);
                if (adjustment is not null)
                {
                    adjustment.State = "Accepted"; adjustment.AcceptedAtUtc = adjustment.ReviewedAtUtc = at;
                    adjustment.ReviewedBy = actor; adjustment.ReviewedByPersonId = permission.PersonId; adjustment.Version++;
                    if (adjustment.AdjustmentCaseId is { } caseId)
                    {
                        var acceptedCase = await db.Set<SettlementAdjustmentCase>().SingleAsync(x => x.Id == caseId, token);
                        if (acceptedCase.AcceptedAtUtc is null)
                        {
                            acceptedCase.State = "Accepted"; acceptedCase.ReviewedBy = actor; acceptedCase.ReviewedByPersonId = permission.PersonId;
                            acceptedCase.AcceptedAtUtc = acceptedCase.ReviewedAtUtc = at; acceptedCase.Version++;
                            acceptedCase.SnapshotJson = JsonSerializer.Serialize(new { FirstExpenseAdjustmentId = adjustment.Id, acceptedCase.RootExpenseRoundId, acceptedCase.BaselineVersion, acceptedCase.BaselineAmount });
                        }
                    }
                }
                // Persist children before freezing their revision: the database forbids any
                // child UPDATE once the parent is accepted. All saves remain one transaction.
                await db.SaveChangesAsync(token);
                revision.State = "Accepted"; revision.AcceptedAtUtc = at; revision.ReviewedAtUtc = at;
                revision.ReviewedBy = actor; revision.ReviewedByPersonId = permission.PersonId; revision.Version++;
                round.State = "Accepted"; round.AcceptedAtUtc = at; round.ReviewedAtUtc = at;
                round.ReviewedBy = actor; round.ReviewedByPersonId = permission.PersonId; round.Version++;
            }
            return new(200, "settlement-reviewed", new(requestId, step.Id, round.State, 0, 0, step.Version));
        }, ct);

    private static FinanceResult<SettlementCommandResult> Conflict(string code) => new(409, code, null);
}
