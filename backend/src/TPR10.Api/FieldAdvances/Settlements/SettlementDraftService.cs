using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;

namespace TPR10.Api.FieldAdvances.Settlements;

public sealed class SettlementDraftService(Tpr10DbContext db, SettlementAccess access,
    SettlementCommandExecutor executor, ExpenseAllocationLedger ledger, TimeProvider clock)
{
    public Task<FinanceResult<SettlementCommandResult>> SaveAsync(Guid actor, Guid requestId, Guid? roundId,
        RoundInput input, SettlementCommand command, CancellationToken ct)
        => SaveCoreAsync(actor, requestId, roundId, null, input, command, ct);

    internal Task<FinanceResult<SettlementCommandResult>> SaveCoreAsync(Guid actor, Guid requestId, Guid? roundId,
        Guid? sourceRoundId, RoundInput input, SettlementCommand command, CancellationToken ct)
        => executor.ExecuteAsync(actor, requestId, sourceRoundId is null ? SettlementActions.Draft : SettlementActions.ExpensePropose, roundId, command,
            sourceRoundId is null ? SettlementCommandExecutor.Hash(new { operation = "save-settlement-draft", roundId, input })
                : SettlementCommandExecutor.Hash(new { operation = "save-settlement-draft", roundId, sourceRoundId, input }), async token =>
        {
            if (input?.Lines is null || input.Lines.Length > 1000) return Invalid();
            var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == requestId, token);
            var person = (await access.CheckAsync(actor, requestId, sourceRoundId is null ? SettlementActions.Draft : SettlementActions.ExpensePropose, roundId, token)).PersonId;
            if (person is null) return new(403, "finance-access-denied", null);
            var approvedId = await db.Set<AdvancePaymentAccount>().Where(x => x.RequestId == requestId)
                .Select(x => x.ApprovedRevisionId).SingleOrDefaultAsync(token);
            var approved = await db.Set<AdvanceRequestRevision>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == approvedId && x.RequestId == requestId, token);
            PolicySnapshot? policy;
            try { policy = JsonSerializer.Deserialize<PolicySnapshot>(approved?.PolicySnapshotJson ?? "null"); }
            catch (JsonException) { return Invalid(); }
            if (policy?.Content?.Categories is null) return new(409, "settlement-approved-snapshot-required", null);
            SettlementRound round;
            SettlementRevision? previous = null;
            if (roundId is { } id)
            {
                round = await db.Set<SettlementRound>().SingleAsync(x => x.Id == id, token);
                previous = await db.Set<SettlementRevision>().Where(x => x.RoundId == id)
                    .OrderByDescending(x => x.RevisionNumber).FirstOrDefaultAsync(token);
                if (round.AcceptedAtUtc is not null || round.State is not ("Draft" or "Returned" or "AwaitingOwner")
                    || previous is null || previous.AcceptedAtUtc is not null
                    || previous.State is not ("Draft" or "Returned" or "AwaitingOwner")) return new(409, "settlement-round-not-editable", null);
            }
            else round = New<SettlementRound>(request, actor, person.Value, command);
            SettlementRevision? sourceRevision = null;
            if (sourceRoundId is not null || round.RoundKind == "Adjustment")
            {
                var sourceRound = await db.Set<SettlementRound>().SingleOrDefaultAsync(x => x.Id == (sourceRoundId ?? round.SourceRoundId) && x.RequestId == requestId, token);
                if (sourceRound?.AcceptedAtUtc is null) return new(409, "expense-source-invalid", null);
                sourceRevision = await ExpenseAdjustmentService.LatestAsync(db, requestId, sourceRound.Id, token);
                if (sourceRevision is null || sourceRevision.RoundId != sourceRound.Id) return new(409, "expense-source-stale", null);
                if (await db.Set<SettlementRound>().AnyAsync(x => x.RequestId == requestId && x.SourceRoundId == sourceRound.Id && x.Id != round.Id && x.AcceptedAtUtc == null, token))
                    return new(409, "expense-adjustment-pending", null);
                round.RoundKind = "Adjustment"; round.SourceRoundId = sourceRound.Id;
            }
            // A returned round may be corrected without discarding its owner's declaration.
            // Adding expense content otherwise requires an explicit immutable withdrawal.
            if (sourceRevision is null && previous?.State is not ("Returned" or "AwaitingOwner")
                && await SettlementCompletionService.CurrentDeclarationAsync(db, requestId, token) is { IsWithdrawal: false })
                return new(409, "settlement-declaration-withdrawal-required", null);
            var lines = new List<SettlementLine>();
            var allocations = new List<ExpenseAllocation>();
            var revision = New<SettlementRevision>(request, actor, person.Value, command);
            revision.RoundId = round.Id; revision.RevisionNumber = (previous?.RevisionNumber ?? 0) + 1;
            revision.SourceRevisionId = previous?.Id;
            var sourceIds = new HashSet<Guid>();
            try
            {
                foreach (var item in input.Lines)
                {
                    if (item is null || string.IsNullOrWhiteSpace(item.Description) || item.Description.Length > 2000
                        || item.IncurredOn == default || item.Allocations is null || item.Allocations.Length > 100
                        || item.Allocations.Any(x => x is null || x.DocumentId == Guid.Empty || x.Amount <= 0
                            || x.Amount > 9999999999999999.99m || decimal.Round(x.Amount, 2) != x.Amount)
                        || item.Allocations.Select(x => x.DocumentId).Distinct().Count() != item.Allocations.Length) return Invalid();
                    var category = policy.Content.Categories.SingleOrDefault(x => x.Id == item.CategoryId && x.Active);
                    if (category is null) return Invalid();
                    if (sourceRevision is not null && item.SourceLineId is null) return Invalid();
                    var sourceBasis = sourceRevision ?? previous;
                    if (item.SourceLineId is { } source && (sourceBasis is null || !sourceIds.Add(source)
                        || !await db.Set<SettlementLine>().AnyAsync(x => x.Id == source && x.RevisionId == sourceBasis.Id
                            && x.RequestId == requestId, token))) return Invalid();
                    var rate = item.Rate;
                    if (category.StandardRate is { } fixedRate)
                    {
                        if (item.LumpSum is not null || item.Quantity is null || rate is not null && rate != fixedRate) return Invalid();
                        rate = fixedRate;
                    }
                    var zero = sourceRevision is not null && (item.LumpSum == 0 && item.Quantity is null && rate is null
                        || item.Quantity == 0 && rate > 0 && item.LumpSum is null);
                    if (zero)
                    {
                        var original = await db.Set<SettlementLine>().SingleAsync(x => x.Id == item.SourceLineId, token);
                        if ((original.LumpSum is null) != (item.LumpSum is null) || original.Rate != rate || original.CategoryId != item.CategoryId) return Invalid();
                        if (rate is not null) SettlementMath.Line(1, rate, null);
                    }
                    var amount = zero ? 0 : SettlementMath.Line(item.Quantity, rate, item.LumpSum);
                    if (item.Allocations.Sum(x => x.Amount) > amount) return Invalid();
                    var line = New<SettlementLine>(request, actor, person.Value, command);
                    line.RevisionId = revision.Id; line.SourceLineId = item.SourceLineId; line.CategoryId = item.CategoryId;
                    line.Description = item.Description.Trim(); line.IncurredOn = item.IncurredOn;
                    line.Quantity = item.Quantity; line.Rate = rate; line.LumpSum = item.LumpSum; line.Amount = amount;
                    line.SnapshotJson = JsonSerializer.Serialize(new LineEvidence(item.Allocations, category.EvidenceTypes));
                    lines.Add(line); revision.Total = checked(revision.Total + amount);
                    if (revision.Total > 9999999999999999.99m) return Invalid();
                    foreach (var allocation in item.Allocations)
                    {
                        if (!await db.Set<ExpenseDocument>().AnyAsync(x => x.Id == allocation.DocumentId && x.RequestId == requestId
                            && x.WorkspaceId == request.WorkspaceId && x.ProjectId == request.ProjectId && x.FamilyId == request.FamilyId, token)) return Invalid();
                        var row = New<ExpenseAllocation>(request, actor, person.Value, command);
                        row.LineId = line.Id; row.RevisionId = revision.Id; row.DocumentId = allocation.DocumentId; row.Amount = allocation.Amount;
                        allocations.Add(row);
                    }
                }
            }
            catch (Exception e) when (e is ArgumentException or OverflowException) { return Invalid(); }
            if (sourceRevision is not null && sourceIds.Count != await db.Set<SettlementLine>().CountAsync(x => x.RevisionId == sourceRevision.Id, token)) return Invalid();
            revision.SnapshotJson = JsonSerializer.Serialize(new
            {
                ApprovedRequestRevisionId = approvedId,
                Policy = policy,
                Input = input,
                Lines = lines.Select(x => new { x.Id, x.CategoryId, x.Quantity, x.Rate, x.LumpSum, x.Amount }),
                revision.Total
            });
            if (sourceRoundId is null) await ledger.RetainAsync(requestId, round.Id, allocations, token);
            if (sourceRevision is not null)
            {
                if (request.State == "Closed" && round.AdjustmentCaseId is null)
                {
                    if (!await db.Set<ClosureRecord>().AnyAsync(x => x.RequestId == requestId && x.AcceptedAtUtc != null, token))
                        return new(409, "expense-closure-missing", null);
                    round.AdjustmentCaseId = await ExpenseAdjustmentService.OpenCaseAsync(db, round, sourceRevision, token);
                }
                var adjustment = New<SettlementExpenseAdjustment>(request, actor, person.Value, command);
                adjustment.SourceRevisionId = sourceRevision.Id; adjustment.RevisionId = revision.Id;
                adjustment.BaseVersion = sourceRevision.Version; adjustment.BeforeAmount = sourceRevision.Total;
                adjustment.AfterAmount = revision.Total; adjustment.Delta = revision.Total - sourceRevision.Total;
                adjustment.AdjustmentCaseId = round.AdjustmentCaseId;
                db.Add(adjustment);
            }
            round.State = previous?.State is "Returned" or "AwaitingOwner" ? "AwaitingOwner" : "Draft";
            revision.State = round.State;
            revision.CorrectionDueOn = previous?.CorrectionDueOn;
            if (roundId is null) db.Add(round);
            db.Add(revision); db.AddRange(lines); db.AddRange(allocations);
            return new(roundId is null ? 201 : 200, "settlement-draft-saved", new(requestId, round.Id, round.State, 0, 0, round.Version));
        }, ct);

    internal sealed record LineEvidence(EvidenceAllocation[] Allocations, string[] EvidenceTypes);
    private T New<T>(AdvanceRequest r, Guid actor, Guid person, SettlementCommand command) where T : class, new()
    {
        var row = new T(); var entry = db.Entry(row);
        entry.Property("Id").CurrentValue = Guid.NewGuid(); entry.Property("WorkspaceId").CurrentValue = r.WorkspaceId;
        entry.Property("ProjectId").CurrentValue = r.ProjectId; entry.Property("FamilyId").CurrentValue = r.FamilyId;
        entry.Property("RequestId").CurrentValue = r.Id; entry.Property("CommandId").CurrentValue = command.CommandId;
        entry.Property("ProposedBy").CurrentValue = actor; entry.Property("ProposedByPersonId").CurrentValue = person;
        entry.Property("CreatedAtUtc").CurrentValue = clock.GetUtcNow(); entry.Property("Reason").CurrentValue = command.Reason;
        return row;
    }
    private static FinanceResult<SettlementCommandResult> Invalid() => new(422, "settlement-draft-invalid", null);
}
