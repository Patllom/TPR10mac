using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Evidence;

namespace TPR10.Api.FieldAdvances.Settlements;

public sealed class RefundService(Tpr10DbContext db, SettlementAccess access, SettlementCommandExecutor executor,
    TimeProvider clock, FinanceEvidenceOptions evidenceOptions)
{
    public Task<FinanceResult<SettlementCommandResult>> ProposeAsync(Guid actor, Guid requestId, RefundInput input, SettlementCommand command, CancellationToken ct)
        => SaveAsync(actor, requestId, null, input, command, ct);

    public Task<FinanceResult<SettlementCommandResult>> UpdateAsync(Guid actor, Guid requestId, Guid refundId, RefundInput input, SettlementCommand command, CancellationToken ct)
        => SaveAsync(actor, requestId, refundId, input, command, ct);

    private Task<FinanceResult<SettlementCommandResult>> SaveAsync(Guid actor, Guid requestId, Guid? refundId,
        RefundInput input, SettlementCommand command, CancellationToken ct)
        => executor.ExecuteAsync(actor, requestId, refundId is null ? (input?.AdjustmentCaseId is null ? SettlementActions.RefundPropose : SettlementActions.CaseRefundCreate) : SettlementActions.RefundUpdate,
            refundId, command, SettlementCommandExecutor.Hash(new { operation = refundId is null ? "propose-refund" : "update-refund", refundId, input }), async token =>
        {
            var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == requestId, token);
            if (request.State == "Closed" && input?.AdjustmentCaseId is null) return Denied();
            if (input is null || !ValidInput(input, clock.GetUtcNow())) return new(422, "refund-invalid", null);
            if (input.AdjustmentCaseId is { } proposedCase && !await AdjustmentCaseService.ActiveAsync(db, request, proposedCase, token))
                return new(422, "refund-case-invalid", null);
            var links = await EvidenceAsync(db, request, input.EvidenceIds, evidenceOptions, token);
            if (links is null) return new(422, "refund-evidence-invalid", null);
            if (await IncidentAccounting.RecoveryProofUsedAsync(db, request.WorkspaceId, links, null, token)) return Conflict("refund-proof-used-in-incident");
            var person = (await access.CheckAsync(actor, requestId, refundId is null ? (input.AdjustmentCaseId is null ? SettlementActions.RefundPropose : SettlementActions.CaseRefundCreate) : SettlementActions.RefundUpdate, refundId, token)).PersonId;
            if (person is null) return Denied();
            Refund row; RefundSnapshot? before = null;
            if (refundId is { } id)
            {
                row = await db.Set<Refund>().SingleAsync(x => x.Id == id, token);
                if (!Editable(row) || row.AdjustmentCaseId != input.AdjustmentCaseId || (before = ReadSnapshot(row)) is null) return Conflict("refund-not-editable");
            }
            else
            {
                row = new Refund
                {
                    Id = Guid.NewGuid(),
                    AdjustmentCaseId = input.AdjustmentCaseId,
                    WorkspaceId = request.WorkspaceId,
                    ProjectId = request.ProjectId,
                    FamilyId = request.FamilyId,
                    RequestId = requestId,
                    CommandId = command.CommandId,
                    ProposedBy = actor,
                    ProposedByPersonId = person.Value,
                    CreatedAtUtc = clock.GetUtcNow()
                };
            }
            var balance = await SettlementBalanceReader.ReadLockedAsync(db, requestId, token);
            if (balance.Value is null) return Conflict(balance.Code);
            var capacity = await CapacityAsync(db, row.AdjustmentCaseId, balance.Value, token);
            var otherClaims = capacity.Pending - (refundId is null ? 0 : row.Amount);
            if (input.Amount > capacity.Outstanding - otherClaims) return Conflict("refund-capacity-exceeded");
            row.Amount = input.Amount; row.RefundedOn = input.ReturnedOn; row.Channel = input.Channel;
            row.EvidenceId = input.EvidenceIds[0]; row.DestinationSnapshotJson = JsonSerializer.Serialize(new { CompanyRecipient = input.CompanyRecipient.Trim() });
            row.State = "Draft"; row.Reason = command.Reason.Trim();
            // Every previous event carries its original content, evidence and time assessment.
            // Editing resets only the current confirmation, never the historical decisions.
            var snapshot = new RefundSnapshot(1, input with { CompanyRecipient = input.CompanyRecipient.Trim() }, links, null, null, before?.History ?? []);
            snapshot = Append(snapshot, refundId is null ? "Propose" : "Update", row, actor, person.Value, command, clock.GetUtcNow());
            row.SnapshotJson = JsonSerializer.Serialize(snapshot);
            if (refundId is null) db.Add(row);
            return Result(row, refundId is null ? 201 : 200, "refund-draft-saved");
        }, ct);

    public Task<FinanceResult<SettlementCommandResult>> ConfirmAsync(Guid actor, Guid requestId, Guid refundId, SettlementCommand command, CancellationToken ct)
        => executor.ExecuteAsync(actor, requestId, SettlementActions.RefundConfirm, refundId, command,
            SettlementCommandExecutor.Hash(new { operation = "confirm-refund", refundId }), async token =>
        {
            var row = await db.Set<Refund>().SingleAsync(x => x.Id == refundId, token);
            var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == requestId, token);
            if (request.State == "Closed" && row.AdjustmentCaseId is null) return Denied();
            if (row.AdjustmentCaseId is { } caseId && !await AdjustmentCaseService.ActiveAsync(db, request, caseId, token)) return Conflict("case-closed");
            var snapshot = ReadSnapshot(row);
            if (row.AcceptedAtUtc is not null || row.State is not ("Draft" or "Returned") || snapshot is null) return Conflict("refund-confirmation-stale");
            var person = (await access.CheckAsync(actor, requestId, SettlementActions.RefundConfirm, refundId, token)).PersonId;
            if (person != request.OwnerPersonId) return Denied();
            var links = await EvidenceAsync(db, request, snapshot.Input.EvidenceIds, evidenceOptions, token);
            if (links is null || !links.SequenceEqual(snapshot.Evidence)) return Conflict("refund-evidence-changed");
            var balance = await SettlementBalanceReader.ReadLockedAsync(db, requestId, token);
            if (balance.Value is null) return Conflict(balance.Code);
            var capacity = await CapacityAsync(db, row.AdjustmentCaseId, balance.Value, token);
            if (capacity.Pending > capacity.Outstanding) return Conflict("refund-capacity-exceeded");
            var at = clock.GetUtcNow();
            var first = snapshot.History.FirstOrDefault(x => x.Operation == "Confirm");
            var original = await db.Set<WorkCompletion>().AsNoTracking().SingleOrDefaultAsync(x => x.RequestId == requestId && x.AcceptedAtUtc != null && x.SupersedesId == null, token);
            var current = await SettlementCompletionService.CurrentEndAsync(db, requestId, token);
            var firstAt = first?.AtUtc ?? at; var firstReturned = first?.Input.ReturnedOn ?? row.RefundedOn;
            var timing = new RefundSubmissionTiming(firstAt, firstReturned, original?.Id, original?.RefundDueOn,
                at, row.RefundedOn, current?.Id, current?.RefundDueOn,
                original is null ? null : Timely(firstReturned, firstAt, original.RefundDueOn),
                current is null ? null : Timely(row.RefundedOn, at, current.RefundDueOn), "Asia/Bangkok");
            snapshot = snapshot with { Confirmation = new(actor, person.Value, at, SettlementCommandExecutor.Hash(new { snapshot.Input, snapshot.Evidence })), Timing = timing };
            row.State = "Pending"; row.Reason = command.Reason.Trim();
            row.SnapshotJson = JsonSerializer.Serialize(Append(snapshot, "Confirm", row, actor, person.Value, command, at));
            return Result(row, 200, "refund-owner-confirmed");
        }, ct);

    public Task<FinanceResult<SettlementCommandResult>> CancelAsync(Guid actor, Guid requestId, Guid refundId, SettlementCommand command, CancellationToken ct)
        => executor.ExecuteAsync(actor, requestId, SettlementActions.RefundCancel, refundId, command,
            SettlementCommandExecutor.Hash(new { operation = "cancel-refund", refundId }), async token =>
        {
            var row = await db.Set<Refund>().SingleAsync(x => x.Id == refundId, token);
            var snapshot = ReadSnapshot(row);
            if (!Editable(row) || snapshot is null) return Conflict("refund-not-editable");
            var person = (await access.CheckAsync(actor, requestId, SettlementActions.RefundCancel, refundId, token)).PersonId;
            if (person is null) return Denied();
            row.State = "Cancelled"; row.Reason = command.Reason.Trim();
            row.SnapshotJson = JsonSerializer.Serialize(Append(snapshot with { Confirmation = null }, "Cancel", row, actor, person.Value, command, clock.GetUtcNow()));
            return Result(row, 200, "refund-cancelled");
        }, ct);

    internal static async Task<(decimal Outstanding, decimal Pending)> CapacityAsync(Tpr10DbContext db, Guid? caseId, SettlementTotals totals, CancellationToken ct)
    {
        if (caseId is null) return (totals.OwnerOutstanding, totals.PendingRefund);
        var outstanding = await AdjustmentCaseService.OutstandingAsync(db, caseId.Value, ct);
        var pending = await db.Set<Refund>().Where(x => x.AdjustmentCaseId == caseId && x.AcceptedAtUtc == null
            && (x.State == "Draft" || x.State == "Pending" || x.State == "Returned")).SumAsync(x => x.Amount, ct);
        // A case cannot create owner liability when an incident replaces the original
        // payment fact. Reserve other case/ordinary claims against the current global net.
        var ownerCapacity = totals.OwnerOutstanding - (totals.PendingRefund - pending);
        return (Math.Min(outstanding, ownerCapacity), pending);
    }

    internal static bool Editable(Refund row) => row.AcceptedAtUtc is null && row.State is "Draft" or "Pending" or "Returned";
    private static bool ValidInput(RefundInput? input, DateTimeOffset at) => input is not null
        && input.Amount > 0 && input.Amount <= 9999999999999999.99m && decimal.Round(input.Amount, 2) == input.Amount
        && input.ReturnedOn != default && input.ReturnedOn <= SettlementDeadlineCalculator.LocalDate(at)
        && input.Channel is "Transfer" or "Cash" && !string.IsNullOrWhiteSpace(input.CompanyRecipient) && input.CompanyRecipient.Length <= 500
        ;
    private static bool Timely(DateOnly actual, DateTimeOffset submitted, DateOnly due) => actual <= due && SettlementDeadlineCalculator.OnTime(submitted, due);

    internal static async Task<RefundEvidenceSnapshot[]?> EvidenceAsync(Tpr10DbContext db, AdvanceRequest request, Guid[]? ids,
        FinanceEvidenceOptions options, CancellationToken ct)
    {
        try { options.Validate(); } catch (InvalidOperationException) { return null; }
        if (ids is null || ids.Length == 0 || ids.Length > options.MaxFiles || ids.Any(x => x == Guid.Empty) || ids.Distinct().Count() != ids.Length) return null;
        var rows = await db.Set<AdvanceEvidence>().FromSqlInterpolated(
            $"SELECT * FROM field_advance_evidence WHERE request_id={request.Id} AND id=ANY({ids}) ORDER BY id FOR UPDATE").AsNoTracking().ToArrayAsync(ct);
        if (rows.Length != ids.Length || rows.Any(x => x.State != "Ready" || x.WorkspaceId != request.WorkspaceId || x.ProjectId != request.ProjectId
                || x.FamilyId != request.FamilyId || x.Length <= 0 || x.Length > options.MaxFileBytes || x.Sha256.Length != 64)
            || rows.Sum(x => x.Length) > options.MaxTotalBytes) return null;
        // Same order as the complete input, including the first evidence's legacy FK.
        return ids.Select(id => rows.Single(x => x.Id == id)).Select(x => new RefundEvidenceSnapshot(x.Id, x.Sha256, x.Length, x.ContentType)).ToArray();
    }

    internal static RefundSnapshot? ReadSnapshot(Refund row)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize<RefundSnapshot>(row.SnapshotJson);
            if (snapshot?.SchemaVersion != 1 || snapshot.Input is null || snapshot.Evidence is null || snapshot.History is null
                || snapshot.Input.EvidenceIds is null || snapshot.Input.EvidenceIds.Length == 0
                || snapshot.Input.Amount != row.Amount || snapshot.Input.ReturnedOn != row.RefundedOn || snapshot.Input.Channel != row.Channel
                || snapshot.Input.AdjustmentCaseId != row.AdjustmentCaseId
                || snapshot.Input.EvidenceIds[0] != row.EvidenceId || !snapshot.Input.EvidenceIds.SequenceEqual(snapshot.Evidence.Select(x => x.Id))) return null;
            using var destination = JsonDocument.Parse(row.DestinationSnapshotJson);
            return destination.RootElement.TryGetProperty("CompanyRecipient", out var recipient) && recipient.GetString() == snapshot.Input.CompanyRecipient ? snapshot : null;
        }
        catch (JsonException) { return null; }
    }

    internal static RefundSnapshot Append(RefundSnapshot snapshot, string operation, Refund row, Guid actor, Guid person,
        SettlementCommand command, DateTimeOffset at, bool companyVerified = false, string? reason = null)
        => snapshot with
        {
            History = [.. snapshot.History, new(command.CommandId, operation, row.State, actor, person, at,
            reason ?? command.Reason.Trim(), snapshot.Input, snapshot.Evidence, snapshot.Confirmation, snapshot.Timing, companyVerified)]
        };
    internal static FinanceResult<SettlementCommandResult> Result(Refund row, int status, string code)
        => new(status, code, new(row.RequestId, row.Id, row.State, 0, 0, row.Version));
    internal static FinanceResult<SettlementCommandResult> Conflict(string code) => new(409, code, null);
    internal static FinanceResult<SettlementCommandResult> Denied() => new(403, "finance-access-denied", null);
}

public sealed record RefundEvidenceSnapshot(Guid Id, string Sha256, long Length, string ContentType);
public sealed record RefundConfirmation(Guid AccountId, Guid PersonId, DateTimeOffset AtUtc, string ContentHash);
public sealed record RefundSubmissionTiming(DateTimeOffset InitialSubmittedAtUtc, DateOnly InitialReturnedOn,
    Guid? OriginalWorkCompletionId, DateOnly? OriginalDueOn, DateTimeOffset SubmittedAtUtc, DateOnly ReturnedOn,
    Guid? WorkCompletionId, DateOnly? EffectiveDueOn, bool? InitialSubmissionOnTime, bool? SubmissionOnTime, string TimeZone);
public sealed record RefundHistoryEntry(Guid CommandId, string Operation, string State, Guid ActorId, Guid PersonId,
    DateTimeOffset AtUtc, string Reason, RefundInput Input, RefundEvidenceSnapshot[] Evidence, RefundConfirmation? Confirmation,
    RefundSubmissionTiming? Timing, bool CompanyDestinationAndEvidenceVerified);
public sealed record RefundSnapshot(int SchemaVersion, RefundInput Input, RefundEvidenceSnapshot[] Evidence,
    RefundConfirmation? Confirmation, RefundSubmissionTiming? Timing, RefundHistoryEntry[] History);
