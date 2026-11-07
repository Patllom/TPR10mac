using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Evidence;
using static TPR10.Api.FieldAdvances.Settlements.RefundService;

namespace TPR10.Api.FieldAdvances.Settlements;

public sealed class RefundReviewService(Tpr10DbContext db, SettlementAccess access, SettlementCommandExecutor executor,
    TimeProvider clock, FinanceEvidenceOptions evidenceOptions)
{
    // Approve explicitly attests company destination/recipient and the complete evidence set.
    // This is a human financial decision, never inferred from a slip or a destination string.
    public Task<FinanceResult<SettlementCommandResult>> ReviewAsync(Guid actor, Guid requestId, Guid refundId,
        ReviewInput input, SettlementCommand command, CancellationToken ct)
        => executor.ExecuteAsync(actor, requestId, SettlementActions.RefundReview, refundId, command,
            SettlementCommandExecutor.Hash(new { operation = "review-refund", refundId, input }), async token =>
        {
            if (input is null || input.Decision is not ("Approve" or "Return") || string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Length > 2000)
                return new(422, "refund-review-invalid", null);
            var row = await db.Set<Refund>().SingleAsync(x => x.Id == refundId, token);
            var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == requestId, token);
            if (request.State == "Closed" && row.AdjustmentCaseId is null) return Denied();
            if (row.AdjustmentCaseId is { } caseId && !await AdjustmentCaseService.ActiveAsync(db, request, caseId, token)) return Conflict("case-closed");
            var snapshot = ReadSnapshot(row);
            if (!Editable(row) || row.State != "Pending" || snapshot?.Confirmation is null || snapshot.Timing is null
                || snapshot.Confirmation.PersonId != request.OwnerPersonId
                || snapshot.Confirmation.ContentHash != SettlementCommandExecutor.Hash(new { snapshot.Input, snapshot.Evidence })) return Conflict("refund-review-stale");
            var person = (await access.CheckAsync(actor, requestId, SettlementActions.RefundReview, refundId, token)).PersonId;
            if (person is null) return Denied();
            if (input.Decision == "Approve")
            {
                var links = await EvidenceAsync(db, request, snapshot.Input.EvidenceIds, evidenceOptions, token);
                if (links is null || !links.SequenceEqual(snapshot.Evidence)) return Conflict("refund-evidence-changed");
                if (await IncidentAccounting.RecoveryProofUsedAsync(db, request.WorkspaceId, links, null, token)) return Conflict("refund-proof-used-in-incident");
                var balance = await SettlementBalanceReader.ReadLockedAsync(db, requestId, token);
                if (balance.Value is null) return Conflict(balance.Code);
                var capacity = await CapacityAsync(db, row.AdjustmentCaseId, balance.Value, token);
                if (capacity.Pending > capacity.Outstanding) return Conflict("refund-capacity-exceeded");
            }
            var at = clock.GetUtcNow(); row.State = input.Decision == "Approve" ? "Accepted" : "Returned";
            row.ReviewedBy = actor; row.ReviewedByPersonId = person; row.ReviewedAtUtc = at; row.Reason = input.Reason.Trim();
            if (input.Decision == "Approve") row.AcceptedAtUtc = at;
            row.SnapshotJson = JsonSerializer.Serialize(Append(snapshot, input.Decision, row, actor, person.Value, command, at,
                input.Decision == "Approve", input.Reason.Trim()));
            return Result(row, 200, "refund-reviewed");
        }, ct);
}
