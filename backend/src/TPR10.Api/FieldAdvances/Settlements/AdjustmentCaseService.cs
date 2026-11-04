using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using TPR10.Api.FieldAdvances.Evidence;
using static TPR10.Api.FieldAdvances.Settlements.RefundService;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
namespace TPR10.Api.FieldAdvances.Settlements;

public sealed class AdjustmentCaseService(Tpr10DbContext db, SettlementAccess access, SettlementCommandExecutor executor, TimeProvider clock, FinanceEvidenceOptions evidenceOptions)
{
    public Task<FinanceResult<SettlementCommandResult>> ProposeCasePaymentAsync(Guid actor, Guid requestId, CasePaymentInput input, SettlementCommand command, CancellationToken ct)
        => executor.ExecuteAsync(actor, requestId, SettlementActions.CasePaymentCreate, command,
            SettlementCommandExecutor.Hash(new { operation = "case-payment-create", input }), async token =>
        {
            if (input is null || input.Amount <= 0 || input.Amount > 9999999999999999.99m || decimal.Round(input.Amount, 2) != input.Amount
                || input.PaidOn == default || input.PaidOn > SettlementDeadlineCalculator.LocalDate(clock.GetUtcNow())) return new(422, "case-payment-invalid", null);
            var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == requestId, token);
            if (!await ActiveAsync(db, request, input.CaseId, token)) return Conflict("case-closed");
            var evidence = await EvidenceAsync(db, request, input.EvidenceIds, evidenceOptions, token);
            if (evidence is null) return new(422, "refund-evidence-invalid", null);
            var person = (await access.CheckAsync(actor, requestId, SettlementActions.CasePaymentCreate, null, token)).PersonId;
            if (person is null) return Denied();
            SettlementCasePayment? previous = null;
            if (input.SupersedesId is { } previousId)
            {
                previous = await db.Set<SettlementCasePayment>().SingleOrDefaultAsync(x => x.Id == previousId && x.CaseId == input.CaseId && x.RequestId == requestId, token);
                if (previous is null || previous.State != "Returned" || previous.AcceptedAtUtc is not null || previous.ProposedByPersonId != person
                    || await db.Set<SettlementCasePayment>().AnyAsync(x => x.SupersedesId == previousId, token)) return Conflict("case-payment-supersedes-invalid");
            }
            if (await SettlementPaymentProof.DuplicateAsync(db, request.WorkspaceId, evidence, previous?.Id, token)) return Conflict("case-payment-proof-duplicate");
            var pending = await PendingPaymentsAsync(db, input.CaseId, token) - (previous?.Amount ?? 0);
            if (input.Amount + pending > -await OutstandingAsync(db, input.CaseId, token)) return Conflict("case-payment-capacity-exceeded");
            var row = new SettlementCasePayment
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
                CaseId = input.CaseId,
                Amount = input.Amount,
                PaidOn = input.PaidOn,
                EvidenceId = input.EvidenceIds[0],
                SupersedesId = input.SupersedesId,
                SnapshotJson = JsonSerializer.Serialize(new CasePaymentSnapshot(1, input, evidence, []))
            };
            if (previous is not null)
            {
                var old = ReadPayment(previous);
                if (old is null) return Conflict("case-payment-snapshot-invalid");
                previous.State = "Superseded"; previous.Version++;
                previous.SnapshotJson = JsonSerializer.Serialize(old with { History = [.. old.History, new(command.CommandId, "Superseded", actor, person.Value, clock.GetUtcNow(), command.Reason.Trim())] });
            }
            db.Add(row); return PaymentResult(row, 201, "case-payment-proposed");
        }, ct);

    public Task<FinanceResult<SettlementCommandResult>> ConfirmCasePaymentAsync(Guid actor, Guid requestId, Guid proposalId, SettlementCommand command, CancellationToken ct)
        => executor.ExecuteAsync(actor, requestId, SettlementActions.CasePaymentReview, proposalId, command,
            SettlementCommandExecutor.Hash(new { operation = "case-payment-confirm", proposalId }), async token =>
        {
            var row = await db.Set<SettlementCasePayment>().SingleAsync(x => x.Id == proposalId, token);
            if (row.AcceptedAtUtc is not null || row.State != "Pending") return Conflict("case-payment-not-pending");
            var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == requestId, token);
            if (!await ActiveAsync(db, request, row.CaseId, token)) return Conflict("case-closed");
            var snapshot = ReadPayment(row);
            if (snapshot is null) return Conflict("case-payment-snapshot-invalid");
            var evidence = await EvidenceAsync(db, request, snapshot.Input.EvidenceIds, evidenceOptions, token);
            if (evidence is null || !evidence.SequenceEqual(snapshot.Evidence)) return Conflict("refund-evidence-changed");
            if (await SettlementPaymentProof.DuplicateAsync(db, request.WorkspaceId, evidence, row.Id, token)) return Conflict("case-payment-proof-duplicate");
            if (await PendingPaymentsAsync(db, row.CaseId, token) > -await OutstandingAsync(db, row.CaseId, token)) return Conflict("case-payment-capacity-exceeded");
            var person = (await access.CheckAsync(actor, requestId, SettlementActions.CasePaymentReview, row.Id, token)).PersonId;
            if (person is null || person == row.ProposedByPersonId) return Denied();
            row.State = "Accepted"; row.ReviewedBy = actor; row.ReviewedByPersonId = person;
            row.AcceptedAtUtc = row.ReviewedAtUtc = clock.GetUtcNow();
            row.SnapshotJson = JsonSerializer.Serialize(snapshot with { History = [.. snapshot.History, new(command.CommandId, "Accept", actor, person.Value, clock.GetUtcNow(), command.Reason.Trim())] });
            return PaymentResult(row, 200, "case-payment-accepted");
        }, ct);

    public Task<FinanceResult<SettlementCommandResult>> CloseCaseAsync(Guid actor, Guid requestId, Guid caseId, SettlementCommand command, CancellationToken ct)
        => executor.ExecuteAsync(actor, requestId, SettlementActions.CaseClose, command,
            SettlementCommandExecutor.Hash(new { operation = "case-close", caseId }), async token =>
        {
            var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == requestId, token);
            if (!await ActiveAsync(db, request, caseId, token)) return Conflict("case-closed");
            var outstanding = await OutstandingAsync(db, caseId, token);
            if (outstanding != 0 || await db.Set<SettlementRound>().AnyAsync(x => x.AdjustmentCaseId == caseId && x.AcceptedAtUtc == null, token) || await db.Set<RefundCorrection>().AnyAsync(x => x.AdjustmentCaseId == caseId && x.AcceptedAtUtc == null
                    && (x.State == "Pending" || x.State == "Returned" || x.State == "Draft"), token)
                || await db.Set<Refund>().AnyAsync(x => x.AdjustmentCaseId == caseId && x.AcceptedAtUtc == null
                    && (x.State == "Pending" || x.State == "Returned" || x.State == "Draft"), token)
                || await db.Set<SettlementCasePayment>().AnyAsync(x => x.CaseId == caseId && x.AcceptedAtUtc == null
                    && (x.State == "Pending" || x.State == "Returned" || x.State == "Draft"), token)) return Conflict("case-unsettled");
            var person = (await access.CheckAsync(actor, requestId, SettlementActions.CaseClose, null, token)).PersonId;
            if (person is null) return Denied();
            var row = new SettlementCaseClosure
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
                State = "Accepted",
                Reason = command.Reason.Trim(),
                CaseId = caseId,
                Outstanding = outstanding,
                ReviewedBy = actor,
                ReviewedByPersonId = person,
                ReviewedAtUtc = clock.GetUtcNow(),
                AcceptedAtUtc = clock.GetUtcNow(),
                SnapshotJson = JsonSerializer.Serialize(new
                {
                    Outstanding = outstanding,
                    ExpenseAdjustments = await db.Set<SettlementExpenseAdjustment>().AsNoTracking().Where(x => x.AdjustmentCaseId == caseId).OrderBy(x => x.Id).ToArrayAsync(token),
                    Corrections = await db.Set<RefundCorrection>().AsNoTracking().Where(x => x.AdjustmentCaseId == caseId).OrderBy(x => x.Id).ToArrayAsync(token),
                    Refunds = await db.Set<Refund>().AsNoTracking().Where(x => x.AdjustmentCaseId == caseId).OrderBy(x => x.Id).ToArrayAsync(token),
                    Payments = await db.Set<SettlementCasePayment>().AsNoTracking().Where(x => x.CaseId == caseId).OrderBy(x => x.Id).ToArrayAsync(token)
                })
            };
            db.Add(row); return new(200, "case-closed", new(requestId, row.Id, "Closed", 0, 0, row.Version));
        }, ct);

    public Task<FinanceResult<SettlementCommandResult>> ReturnCasePaymentAsync(Guid actor, Guid requestId, Guid proposalId, SettlementCommand command, CancellationToken ct)
        => TransitionPaymentAsync(actor, requestId, proposalId, "Return", command, ct);
    public Task<FinanceResult<SettlementCommandResult>> RejectCasePaymentAsync(Guid actor, Guid requestId, Guid proposalId, SettlementCommand command, CancellationToken ct)
        => TransitionPaymentAsync(actor, requestId, proposalId, "Reject", command, ct);
    public Task<FinanceResult<SettlementCommandResult>> CancelCasePaymentAsync(Guid actor, Guid requestId, Guid proposalId, SettlementCommand command, CancellationToken ct)
        => TransitionPaymentAsync(actor, requestId, proposalId, "Cancel", command, ct);
    private Task<FinanceResult<SettlementCommandResult>> TransitionPaymentAsync(Guid actor, Guid requestId, Guid proposalId, string operation, SettlementCommand command, CancellationToken ct)
        => executor.ExecuteAsync(actor, requestId, operation == "Cancel" ? SettlementActions.CasePaymentCancel : SettlementActions.CasePaymentReview,
            proposalId, command, SettlementCommandExecutor.Hash(new { operation, proposalId }),
            async token =>
            {
                var row = await db.Set<SettlementCasePayment>().SingleAsync(x => x.Id == proposalId, token);
                if (row.AcceptedAtUtc is not null || row.State is not ("Pending" or "Returned")) return Conflict("case-payment-not-editable");
                var person = (await access.CheckAsync(actor, requestId, operation == "Cancel" ? SettlementActions.CasePaymentCancel : SettlementActions.CasePaymentReview, row.Id, token)).PersonId;
                if (person is null || operation == "Cancel" && person != row.ProposedByPersonId) return Denied();
                var snapshot = ReadPayment(row);
                if (snapshot is null) return Conflict("case-payment-snapshot-invalid");
                row.State = operation switch { "Return" => "Returned", "Reject" => "Rejected", _ => "Cancelled" };
                if (operation != "Cancel") { row.ReviewedBy = actor; row.ReviewedByPersonId = person; row.ReviewedAtUtc = clock.GetUtcNow(); }
                row.SnapshotJson = JsonSerializer.Serialize(snapshot with { History = [.. snapshot.History, new(command.CommandId, operation, actor, person.Value, clock.GetUtcNow(), command.Reason.Trim())] });
                return PaymentResult(row, 200, "case-payment-transitioned");
            }, ct);

    private static Task<decimal> PendingPaymentsAsync(Tpr10DbContext db, Guid caseId, CancellationToken ct)
        => db.Set<SettlementCasePayment>().Where(x => x.CaseId == caseId && x.AcceptedAtUtc == null
            && (x.State == "Pending" || x.State == "Returned" || x.State == "Draft")).SumAsync(x => x.Amount, ct);
    private static FinanceResult<SettlementCommandResult> PaymentResult(SettlementCasePayment row, int status, string code)
        => new(status, code, new(row.RequestId, row.Id, row.State, 0, 0, row.Version));
    internal static CasePaymentSnapshot? ReadPayment(SettlementCasePayment row)
    {
        try
        {
            var s = JsonSerializer.Deserialize<CasePaymentSnapshot>(row.SnapshotJson);
            return s is not null && s.SchemaVersion == 1 && s.Input is not null && s.Evidence is not null && s.History is not null
                && s.Input.CaseId == row.CaseId && s.Input.Amount == row.Amount && s.Input.PaidOn == row.PaidOn
                && s.Input.EvidenceIds is { Length: > 0 } && s.Input.EvidenceIds[0] == row.EvidenceId
                && s.Input.EvidenceIds.SequenceEqual(s.Evidence.Select(x => x.Id)) ? s : null;
        }
        catch (JsonException) { return null; }
    }

    internal async Task<Guid> OpenAsync(Guid requestId, string kind, Guid sourceId, CancellationToken ct)
    {
        var source = db.Set<RefundCorrection>().Local.SingleOrDefault(x => x.Id == sourceId && x.RequestId == requestId);
        if (source is null || kind != "RefundCorrection" || db.Entry(source).State != EntityState.Added)
            throw new InvalidOperationException("ต้องเปิดเคสจากคำร้องใหม่ในคำสั่งแก้เงินคืน");
        executor.RequireCaseContext(requestId, source.CommandId);
        var closure = await db.Set<ClosureRecord>().AsNoTracking().SingleOrDefaultAsync(x => x.RequestId == requestId
            && x.WorkspaceId == source.WorkspaceId && x.ProjectId == source.ProjectId && x.FamilyId == source.FamilyId && x.AcceptedAtUtc != null, ct);
        if (closure is null) throw new InvalidOperationException("คำขอปิดต้องมีหลักฐานปิดฉบับเดิม");
        var existing = await db.Set<SettlementAdjustmentCase>().Where(x => x.RequestId == requestId && x.RootRefundId == source.RefundId
            && !db.Set<SettlementCaseClosure>().Any(c => c.CaseId == x.Id)).SingleOrDefaultAsync(ct);
        if (existing is not null) return existing.Id;
        var row = new SettlementAdjustmentCase
        {
            Id = Guid.NewGuid(),
            WorkspaceId = source.WorkspaceId,
            ProjectId = source.ProjectId,
            FamilyId = source.FamilyId,
            RequestId = requestId,
            CommandId = source.CommandId,
            ProposedBy = source.ProposedBy,
            ProposedByPersonId = source.ProposedByPersonId,
            CreatedAtUtc = clock.GetUtcNow(),
            State = "Pending",
            Reason = source.Reason,
            ClosureRecordId = closure.Id,
            Kind = kind,
            RootRefundId = source.RefundId,
            BaselineVersion = source.BaseVersion,
            BaselineAmount = source.BeforeAmount
        };
        db.Add(row); return row.Id;
    }

    internal static Task<bool> ActiveAsync(Tpr10DbContext db, AdvanceRequest request, Guid caseId, CancellationToken ct)
        => db.Set<SettlementAdjustmentCase>().AnyAsync(x => x.Id == caseId && x.RequestId == request.Id
            && x.WorkspaceId == request.WorkspaceId && x.ProjectId == request.ProjectId && x.FamilyId == request.FamilyId
            && (x.RootRefundId != null || x.RootExpenseRoundId != null) && !db.Set<SettlementCaseClosure>().Any(c => c.CaseId == x.Id), ct);

    // Each case starts at zero relative to its captured net baseline. Only its own linked
    // facts contribute; previous closed cases remain in the global request net exactly once.
    internal static async Task<decimal> OutstandingAsync(Tpr10DbContext db, Guid caseId, CancellationToken ct)
        => -await db.Set<SettlementExpenseAdjustment>().Where(x => x.AdjustmentCaseId == caseId && x.AcceptedAtUtc != null).SumAsync(x => x.Delta, ct)
            - await db.Set<RefundCorrection>().Where(x => x.AdjustmentCaseId == caseId && x.AcceptedAtUtc != null).SumAsync(x => x.Delta, ct)
            + await db.Set<SettlementCasePayment>().Where(x => x.CaseId == caseId && x.AcceptedAtUtc != null).SumAsync(x => x.Amount, ct)
            - await db.Set<Refund>().Where(x => x.AdjustmentCaseId == caseId && x.AcceptedAtUtc != null).SumAsync(x => x.Amount, ct);
}

public sealed record CasePaymentSnapshot(int SchemaVersion, CasePaymentInput Input, RefundEvidenceSnapshot[] Evidence, CorrectionHistory[] History);
