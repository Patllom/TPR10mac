using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Evidence;
using static TPR10.Api.FieldAdvances.Settlements.RefundService;
namespace TPR10.Api.FieldAdvances.Settlements;

public sealed class RefundCorrectionService(Tpr10DbContext db, SettlementAccess access, SettlementCommandExecutor executor,
    TimeProvider clock, FinanceEvidenceOptions evidenceOptions)
{
    public Task<FinanceResult<SettlementCommandResult>> ProposeAsync(Guid actor, Guid requestId, RefundCorrectionInput input, SettlementCommand command, CancellationToken ct)
        => executor.ExecuteAsync(actor, requestId, SettlementActions.CorrectionCreate, command,
            SettlementCommandExecutor.Hash(new { operation = "propose-correction", input }), async token =>
        {
            if (input is null || input.CorrectAmount < 0 || input.CorrectAmount > 9999999999999999.99m
                || decimal.Round(input.CorrectAmount, 2) != input.CorrectAmount) return new(422, "correction-invalid", null);
            var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == requestId, token);
            if (request.State == "Closed" && !await db.Set<ClosureRecord>().AnyAsync(x => x.RequestId == requestId
                && x.WorkspaceId == request.WorkspaceId && x.ProjectId == request.ProjectId && x.FamilyId == request.FamilyId && x.AcceptedAtUtc != null, token))
                return Conflict("correction-closure-missing");
            var source = await db.Set<Refund>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.RefundId
                && x.RequestId == requestId && x.WorkspaceId == request.WorkspaceId && x.ProjectId == request.ProjectId && x.FamilyId == request.FamilyId, token);
            if (source?.AcceptedAtUtc is null || source.State != "Accepted") return Conflict("correction-source-invalid");
            var net = await NetAsync(db, source, token);
            if (net is null || net.Value.Amount == input.CorrectAmount) return Conflict("correction-net-invalid");
            var evidence = await EvidenceAsync(db, request, input.EvidenceIds, evidenceOptions, token);
            if (evidence is null) return new(422, "refund-evidence-invalid", null);
            if (await IncidentAccounting.RecoveryProofUsedAsync(db, request.WorkspaceId, evidence, null, token)) return Conflict("refund-proof-used-in-incident");
            var person = (await access.CheckAsync(actor, requestId, SettlementActions.CorrectionCreate, null, token)).PersonId;
            if (person is null) return Denied();
            RefundCorrection? previous = null;
            if (input.SupersedesId is { } previousId)
            {
                previous = await db.Set<RefundCorrection>().SingleOrDefaultAsync(x => x.Id == previousId && x.RequestId == requestId && x.RefundId == source.Id, token);
                if (previous is null || previous.State != "Returned" || previous.AcceptedAtUtc is not null
                    || previous.ProposedByPersonId != person || await db.Set<RefundCorrection>().AnyAsync(x => x.SupersedesId == previousId, token))
                    return Conflict("correction-supersedes-invalid");
            }
            var row = new RefundCorrection
            {
                Id = Guid.NewGuid(),
                WorkspaceId = request.WorkspaceId,
                ProjectId = request.ProjectId,
                FamilyId = request.FamilyId,
                RequestId = requestId,
                CommandId = command.CommandId,
                ProposedBy = actor,
                ProposedByPersonId = person.Value,
                CreatedAtUtc = clock.GetUtcNow(),
                State = "Pending",
                Reason = command.Reason.Trim(),
                RefundId = source.Id,
                BaseVersion = net.Value.Version,
                BeforeAmount = net.Value.Amount,
                AfterAmount = input.CorrectAmount,
                Delta = input.CorrectAmount - net.Value.Amount,
                EvidenceId = input.EvidenceIds[0],
                SupersedesId = input.SupersedesId,
                SnapshotJson = JsonSerializer.Serialize(new CorrectionSnapshot(1, input, net.Value.Version, net.Value.Amount, evidence, []))
            };
            db.Add(row);
            if (request.State == "Closed")
                row.AdjustmentCaseId = await new AdjustmentCaseService(db, access, executor, clock, evidenceOptions).OpenAsync(requestId, "RefundCorrection", row.Id, token);
            if (previous is not null)
            {
                if (previous.AdjustmentCaseId != row.AdjustmentCaseId) return Conflict("correction-supersedes-case-invalid");
                var old = Read(previous);
                if (old is null) return Conflict("correction-snapshot-invalid");
                previous.State = "Superseded"; previous.Version++;
                previous.SnapshotJson = JsonSerializer.Serialize(old with { History = [.. old.History, new(command.CommandId, "Superseded", actor, person.Value, clock.GetUtcNow(), command.Reason.Trim())] });
            }
            return Result(row, 201, "correction-proposed");
        }, ct);

    public Task<FinanceResult<SettlementCommandResult>> ConfirmAsync(Guid actor, Guid requestId, Guid correctionId, SettlementCommand command, CancellationToken ct)
        => executor.ExecuteAsync(actor, requestId, SettlementActions.CorrectionReview, correctionId, command,
            SettlementCommandExecutor.Hash(new { operation = "confirm-correction", correctionId }), async token =>
        {
            var row = await db.Set<RefundCorrection>().SingleAsync(x => x.Id == correctionId, token);
            if (row.AcceptedAtUtc is not null || row.State != "Pending") return Conflict("correction-not-pending");
            var source = await db.Set<Refund>().AsNoTracking().SingleAsync(x => x.Id == row.RefundId, token);
            var net = await NetAsync(db, source, token);
            if (net is null || row.BaseVersion != net.Value.Version || row.BeforeAmount != net.Value.Amount) return Conflict("correction-net-stale");
            var snapshot = Read(row);
            if (snapshot is null) return Conflict("correction-snapshot-invalid");
            var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == requestId, token);
            var evidence = await EvidenceAsync(db, request, snapshot.Input.EvidenceIds, evidenceOptions, token);
            if (evidence is null || !evidence.SequenceEqual(snapshot.Evidence)) return Conflict("refund-evidence-changed");
            if (await IncidentAccounting.RecoveryProofUsedAsync(db, request.WorkspaceId, evidence, null, token)) return Conflict("refund-proof-used-in-incident");
            if (row.AdjustmentCaseId is { } caseId && !await AdjustmentCaseService.ActiveAsync(db, request, caseId, token)) return Conflict("case-closed");
            var person = (await access.CheckAsync(actor, requestId, SettlementActions.CorrectionReview, row.Id, token)).PersonId;
            if (person is null || person == row.ProposedByPersonId) return Denied();
            row.State = "Accepted"; row.ReviewedBy = actor; row.ReviewedByPersonId = person;
            row.AcceptedAtUtc = row.ReviewedAtUtc = clock.GetUtcNow();
            row.SnapshotJson = JsonSerializer.Serialize(snapshot with { History = [.. snapshot.History, new(command.CommandId, "Accept", actor, person.Value, clock.GetUtcNow(), command.Reason.Trim())] });
            if (row.AdjustmentCaseId is { } acceptedCaseId)
            {
                var acceptedCase = await db.Set<SettlementAdjustmentCase>().SingleAsync(x => x.Id == acceptedCaseId, token);
                if (acceptedCase.AcceptedAtUtc is null)
                {
                    acceptedCase.State = "Accepted"; acceptedCase.ReviewedBy = actor; acceptedCase.ReviewedByPersonId = person;
                    acceptedCase.AcceptedAtUtc = acceptedCase.ReviewedAtUtc = clock.GetUtcNow(); acceptedCase.Version++;
                    acceptedCase.SnapshotJson = JsonSerializer.Serialize(new { FirstCorrectionId = row.Id, acceptedCase.RootRefundId, acceptedCase.BaselineVersion, acceptedCase.BaselineAmount });
                }
            }
            return Result(row, 200, "correction-accepted");
        }, ct);

    public Task<FinanceResult<SettlementCommandResult>> ReturnAsync(Guid actor, Guid requestId, Guid correctionId, SettlementCommand command, CancellationToken ct)
        => TransitionAsync(actor, requestId, correctionId, "Return", command, ct);
    public Task<FinanceResult<SettlementCommandResult>> RejectAsync(Guid actor, Guid requestId, Guid correctionId, SettlementCommand command, CancellationToken ct)
        => TransitionAsync(actor, requestId, correctionId, "Reject", command, ct);
    public Task<FinanceResult<SettlementCommandResult>> CancelAsync(Guid actor, Guid requestId, Guid correctionId, SettlementCommand command, CancellationToken ct)
        => TransitionAsync(actor, requestId, correctionId, "Cancel", command, ct);
    private Task<FinanceResult<SettlementCommandResult>> TransitionAsync(Guid actor, Guid requestId, Guid correctionId, string operation, SettlementCommand command, CancellationToken ct)
        => executor.ExecuteAsync(actor, requestId, operation == "Cancel" ? SettlementActions.CorrectionCancel : SettlementActions.CorrectionReview,
            correctionId, command, SettlementCommandExecutor.Hash(new { operation, correctionId }),
            async token =>
            {
                var row = await db.Set<RefundCorrection>().SingleAsync(x => x.Id == correctionId, token);
                if (row.AcceptedAtUtc is not null || row.State is not ("Pending" or "Returned")) return Conflict("correction-not-editable");
                var person = (await access.CheckAsync(actor, requestId, operation == "Cancel" ? SettlementActions.CorrectionCancel : SettlementActions.CorrectionReview, row.Id, token)).PersonId;
                if (person is null || operation == "Cancel" && person != row.ProposedByPersonId) return Denied();
                var snapshot = Read(row);
                if (snapshot is null) return Conflict("correction-snapshot-invalid");
                row.State = operation switch { "Return" => "Returned", "Reject" => "Rejected", _ => "Cancelled" };
                if (operation != "Cancel") { row.ReviewedBy = actor; row.ReviewedByPersonId = person; row.ReviewedAtUtc = clock.GetUtcNow(); }
                row.SnapshotJson = JsonSerializer.Serialize(snapshot with { History = [.. snapshot.History, new(command.CommandId, operation, actor, person.Value, clock.GetUtcNow(), command.Reason.Trim())] });
                return Result(row, 200, "correction-transitioned");
            }, ct);

    internal static async Task<(long Version, decimal Amount)?> NetAsync(Tpr10DbContext db, Refund source, CancellationToken ct)
        => SettlementBalanceReader.RefundNet(source, await db.Set<RefundCorrection>().AsNoTracking()
            .Where(x => x.RefundId == source.Id && x.AcceptedAtUtc != null).ToArrayAsync(ct));
    internal static CorrectionSnapshot? Read(RefundCorrection row)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize<CorrectionSnapshot>(row.SnapshotJson);
            return snapshot is not null && snapshot.SchemaVersion == 1 && snapshot.Input is not null && snapshot.Evidence is not null && snapshot.History is not null
                && snapshot.Input.RefundId == row.RefundId && snapshot.Input.CorrectAmount == row.AfterAmount
                && snapshot.BaseVersion == row.BaseVersion && snapshot.BeforeAmount == row.BeforeAmount
                && snapshot.Input.EvidenceIds is { Length: > 0 } && snapshot.Input.EvidenceIds[0] == row.EvidenceId
                && snapshot.Input.EvidenceIds.SequenceEqual(snapshot.Evidence.Select(x => x.Id)) ? snapshot : null;
        }
        catch (JsonException) { return null; }
    }
    private static FinanceResult<SettlementCommandResult> Result(RefundCorrection row, int status, string code)
        => new(status, code, new(row.RequestId, row.Id, row.State, 0, 0, row.Version));
}
public sealed record CorrectionHistory(Guid CommandId, string Operation, Guid Actor, Guid Person, DateTimeOffset AtUtc, string Reason);
public sealed record CorrectionSnapshot(int SchemaVersion, RefundCorrectionInput Input, long BaseVersion, decimal BeforeAmount,
    RefundEvidenceSnapshot[] Evidence, CorrectionHistory[] History);
