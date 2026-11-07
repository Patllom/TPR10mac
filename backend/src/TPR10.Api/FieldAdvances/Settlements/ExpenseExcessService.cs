using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Evidence;
using static TPR10.Api.FieldAdvances.Settlements.RefundService;
namespace TPR10.Api.FieldAdvances.Settlements;

public sealed class ExpenseExcessService(Tpr10DbContext db, SettlementAccess access, SettlementCommandExecutor executor,
    TimeProvider clock, FinanceEvidenceOptions evidenceOptions)
{
    public Task<FinanceResult<SettlementCommandResult>> TrackAsync(Guid actor, Guid requestId, Guid sourceRoundId,
        Guid documentId, SettlementCommand command, CancellationToken ct)
        => executor.ExecuteAsync(actor, requestId, SettlementActions.ExcessTrack, command,
            SettlementCommandExecutor.Hash(new { operation = "excess-track", sourceRoundId, documentId }), async token =>
        {
            var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == requestId, token);
            var revision = await db.Set<SettlementRevision>().Where(x => x.RoundId == sourceRoundId && x.RequestId == requestId)
                .OrderByDescending(x => x.RevisionNumber).FirstOrDefaultAsync(token);
            var doc = await db.Set<ExpenseDocument>().SingleOrDefaultAsync(x => x.Id == documentId && x.RequestId == requestId, token);
            if (revision is null || doc is null || revision.AcceptedAtUtc != null || revision.State is not ("Draft" or "AwaitingOwner")
                || !await db.Set<SettlementRound>().AnyAsync(x => x.Id == sourceRoundId && x.RoundKind == "Ordinary", token)
                || await db.Set<SettlementExcessCase>().AnyAsync(x => x.DocumentId == documentId, token)) return Conflict("excess-source-invalid");
            if (await DuplicateDocumentService.IsBlockedLockedAsync(db, requestId, documentId, token)) return Conflict("document-duplicate-pending");
            // Returned drafts retain only the old reservation. The dispute uses the full
            // replacement claim frozen in line snapshots, never that smaller ledger amount.
            Dictionary<Guid, decimal> claims;
            try
            {
                var lines = await db.Set<SettlementLine>().AsNoTracking().Where(x => x.RevisionId == revision.Id).ToArrayAsync(token);
                claims = new();
                foreach (var line in lines)
                {
                    var allocations = JsonSerializer.Deserialize<SettlementDraftService.LineEvidence>(line.SnapshotJson)?.Allocations;
                    if (allocations is null || allocations.Any(x => x is null || x.Amount <= 0)
                        || allocations.Select(x => x.DocumentId).Distinct().Count() != allocations.Length
                        || allocations.Sum(x => x.Amount) != line.Amount) return Conflict("excess-claim-incomplete");
                    foreach (var allocation in allocations)
                        claims[allocation.DocumentId] = checked(claims.GetValueOrDefault(allocation.DocumentId) + allocation.Amount);
                }
                if (lines.Sum(x => x.Amount) != revision.Total) return Conflict("excess-claim-invalid");
            }
            catch (Exception e) when (e is JsonException or NullReferenceException or OverflowException) { return Conflict("excess-claim-invalid"); }
            var documents = await db.Set<ExpenseDocument>().AsNoTracking()
                .Where(x => x.RequestId == requestId && claims.Keys.Contains(x.Id)).ToArrayAsync(token);
            if (documents.Length != claims.Count || documents.Any(x => claims[x.Id] > x.Amount)) return Conflict("excess-claim-invalid");
            var approved = await db.Set<AdvancePaymentAccount>().Where(x => x.RequestId == requestId).Select(x => x.Approved).SingleAsync(token);
            var expense = await ExpenseAdjustmentService.ExpenseTotalAsync(db, requestId, token);
            var claim = claims.GetValueOrDefault(documentId);
            if (expense is null || claim <= 0) return Conflict("excess-claim-invalid");
            var available = Math.Max(0, approved - expense.Value);
            var envelope = Math.Max(0, revision.Total - available);
            // Canonical order only identifies the immutable basis; the explicitly selected
            // document receives the remainder, not a priority derived from document GUIDs.
            var basis = new
            {
                SourceRound = sourceRoundId,
                SourceRevision = revision.Id,
                FullClaim = revision.Total,
                ApprovedCap = approved,
                ApprovedExpense = expense.Value,
                AvailableCap = available,
                ExcessEnvelope = envelope,
                DocumentClaims = documents.OrderBy(x => x.Id).Select(x => new { Document = x.Id, Claim = claims[x.Id], DocumentAmount = x.Amount }).ToArray()
            };
            var basisHash = SettlementCommandExecutor.Hash(basis);
            // The executor holds the existing registry/family/request locks. Include every
            // revision and resolved case: neither editing nor resolving releases attribution.
            var attributed = await (from excess in db.Set<SettlementExcessCase>().AsNoTracking()
                                    join source in db.Set<SettlementRevision>() on excess.SourceRevisionId equals source.Id
                                    where excess.RequestId == requestId && source.RoundId == sourceRoundId
                                    select excess).ToArrayAsync(token);
            foreach (var previous in attributed)
            {
                try
                {
                    using var snapshot = JsonDocument.Parse(previous.SnapshotJson);
                    if (previous.SourceRevisionId != revision.Id
                        || !snapshot.RootElement.TryGetProperty("AttributionBasisHash", out var hash)
                        || hash.ValueKind != JsonValueKind.String || hash.GetString() != basisHash)
                        return Conflict("excess-basis-changed");
                }
                catch (JsonException) { return Conflict("excess-basis-changed"); }
            }
            var allocated = attributed.Sum(x => x.Amount);
            var disputed = Math.Min(claim, Math.Max(0, envelope - allocated));
            if (disputed <= 0) return Conflict("excess-not-required");
            var cap = claim - disputed;
            var person = (await access.CheckAsync(actor, requestId, SettlementActions.ExcessTrack, null, token)).PersonId;
            if (person is null) return Denied();
            var row = New<SettlementExcessCase>(request, actor, person.Value, command);
            row.DocumentId = documentId; row.SourceRevisionId = revision.Id; row.Amount = disputed;
            row.ClaimedAmount = claim; row.CapAmount = cap; row.State = "Pending";
            row.SnapshotJson = JsonSerializer.Serialize(new { Claim = claim, AvailableCap = cap, ApprovedCap = approved, ApprovedExpense = expense, SourceRevision = revision.Id, Document = doc.Id, AttributionBasis = basis, AttributionBasisHash = basisHash, PreviouslyAttributed = allocated });
            db.Add(row); return new(201, "excess-tracked", new(requestId, row.Id, row.State, 0, 0, row.Version));
        }, ct);

    public Task<FinanceResult<SettlementCommandResult>> RecordDispositionAsync(Guid actor, Guid requestId, Guid excessId,
        string disposition, Guid[] evidenceIds, SettlementCommand command, CancellationToken ct)
        => RecordDispositionAsync(actor, requestId, excessId, new DispositionInput(disposition, evidenceIds, []), command, ct);

    public Task<FinanceResult<SettlementCommandResult>> RecordDispositionAsync(Guid actor, Guid requestId, Guid excessId,
        DispositionInput input, SettlementCommand command, CancellationToken ct)
        => executor.ExecuteAsync(actor, requestId, SettlementActions.ExcessDisposition, excessId, command,
            SettlementCommandExecutor.Hash(new { operation = "excess-disposition", excessId, input }), async token =>
        {
            var row = await db.Set<SettlementExcessCase>().SingleAsync(x => x.Id == excessId, token);
            if (row.State == "Resolved") return Conflict("excess-resolved");
            if (input is null || input.Disposition is not ("AwaitingExternalReview" or "NoAdditionalPaymentRequired" or "FinancialSettlementRecorded")
                || input.PaymentIds is null || input.PaymentIds.Distinct().Count() != input.PaymentIds.Length) return new(422, "excess-disposition-invalid", null);
            var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == requestId, token);
            var evidence = await EvidenceAsync(db, request, input.EvidenceIds, evidenceOptions, token);
            if (evidence is null) return new(422, "excess-evidence-invalid", null);
            var pending = await PendingPaymentsAsync(db, excessId, token);
            var paid = await db.Set<SettlementExcessPayment>().Where(x => x.ExcessId == excessId && x.AcceptedAtUtc != null).ToArrayAsync(token);
            if (input.Disposition != "AwaitingExternalReview" && pending != 0) return Conflict("excess-payment-pending");
            if (input.Disposition == "FinancialSettlementRecorded")
            {
                if (input.PaymentIds.Length == 0 || paid.Length != input.PaymentIds.Length || paid.Any(x => !input.PaymentIds.Contains(x.Id))
                    || paid.Sum(x => x.Amount) != row.Amount) return Conflict("excess-financial-proof-required");
            }
            else if (input.PaymentIds.Length != 0 || input.Disposition == "NoAdditionalPaymentRequired" && paid.Length != 0)
                return Conflict("excess-disposition-inconsistent");
            var person = (await access.CheckAsync(actor, requestId, SettlementActions.ExcessDisposition, row.Id, token)).PersonId;
            if (person is null) return Denied();
            var history = New<SettlementExcessDisposition>(request, actor, person.Value, command);
            history.ExcessId = excessId; history.Code = input.Disposition; history.EvidenceId = input.EvidenceIds[0];
            history.State = "Accepted"; history.ReviewedBy = actor; history.ReviewedByPersonId = person;
            history.AcceptedAtUtc = history.ReviewedAtUtc = clock.GetUtcNow();
            history.SnapshotJson = JsonSerializer.Serialize(new
            {
                Input = input,
                Evidence = evidence,
                DisputedAmount = row.Amount,
                RecipientPersonId = request.OwnerPersonId,
                PaidAmount = paid.Sum(x => x.Amount)
            });
            db.Add(history);
            if (input.Disposition != "AwaitingExternalReview") row.State = "Resolved";
            return new(200, "excess-disposition-recorded", new(requestId, row.Id, row.State, 0, 0, row.Version));
        }, ct);

    public Task<FinanceResult<SettlementCommandResult>> ProposeExternalPaymentAsync(Guid actor, Guid requestId, ExcessPaymentInput input, SettlementCommand command, CancellationToken ct)
        => executor.ExecuteAsync(actor, requestId, SettlementActions.ExcessPaymentCreate, command,
            SettlementCommandExecutor.Hash(new { operation = "excess-payment-create", input }), async token =>
        {
            if (input is null || input.Amount <= 0 || input.Amount > 9999999999999999.99m || decimal.Round(input.Amount, 2) != input.Amount
                || input.PaidOn == default || input.PaidOn > SettlementDeadlineCalculator.LocalDate(clock.GetUtcNow())) return new(422, "excess-payment-invalid", null);
            var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == requestId, token);
            if (input.RecipientPersonId != request.OwnerPersonId || !await ActiveAsync(db, request, input.ExcessId, token)) return Conflict("case-closed");
            var evidence = await EvidenceAsync(db, request, input.EvidenceIds, evidenceOptions, token);
            if (evidence is null) return new(422, "refund-evidence-invalid", null);
            var person = (await access.CheckAsync(actor, requestId, SettlementActions.ExcessPaymentCreate, null, token)).PersonId;
            if (person is null) return Denied();
            SettlementExcessPayment? previous = null;
            if (input.SupersedesId is { } previousId)
            {
                previous = await db.Set<SettlementExcessPayment>().SingleOrDefaultAsync(x => x.Id == previousId && x.ExcessId == input.ExcessId && x.RequestId == requestId, token);
                if (previous is null || previous.State != "Returned" || previous.AcceptedAtUtc is not null || previous.ProposedByPersonId != person
                    || await db.Set<SettlementExcessPayment>().AnyAsync(x => x.SupersedesId == previousId, token)) return Conflict("excess-payment-supersedes-invalid");
            }
            if (await SettlementPaymentProof.DuplicateAsync(db, request.WorkspaceId, evidence, previous?.Id, token)) return Conflict("excess-payment-proof-duplicate");
            var pending = await PendingPaymentsAsync(db, input.ExcessId, token) - (previous?.Amount ?? 0);
            if (input.Amount + pending > await OutstandingAsync(db, input.ExcessId, token)) return Conflict("excess-payment-capacity-exceeded");
            var row = new SettlementExcessPayment
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
                ExcessId = input.ExcessId,
                Amount = input.Amount,
                RecipientPersonId = input.RecipientPersonId,
                PaidOn = input.PaidOn,
                EvidenceId = input.EvidenceIds[0],
                SupersedesId = input.SupersedesId,
                SnapshotJson = JsonSerializer.Serialize(new ExcessPaymentSnapshot(1, input, evidence, []))
            };
            if (previous is not null)
            {
                var old = ReadPayment(previous);
                if (old is null) return Conflict("excess-payment-snapshot-invalid");
                previous.State = "Superseded"; previous.Version++;
                previous.SnapshotJson = JsonSerializer.Serialize(old with { History = [.. old.History, new(command.CommandId, "Superseded", actor, person.Value, clock.GetUtcNow(), command.Reason.Trim())] });
            }
            db.Add(row); return PaymentResult(row, 201, "excess-payment-proposed");
        }, ct);

    public Task<FinanceResult<SettlementCommandResult>> ConfirmExternalPaymentAsync(Guid actor, Guid requestId, Guid proposalId, SettlementCommand command, CancellationToken ct)
        => executor.ExecuteAsync(actor, requestId, SettlementActions.ExcessPaymentReview, proposalId, command,
            SettlementCommandExecutor.Hash(new { operation = "excess-payment-confirm", proposalId }), async token =>
        {
            var row = await db.Set<SettlementExcessPayment>().SingleAsync(x => x.Id == proposalId, token);
            if (row.AcceptedAtUtc is not null || row.State != "Pending") return Conflict("excess-payment-not-pending");
            var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == requestId, token);
            if (row.RecipientPersonId != request.OwnerPersonId || !await ActiveAsync(db, request, row.ExcessId, token)) return Conflict("case-closed");
            var snapshot = ReadPayment(row);
            if (snapshot is null) return Conflict("excess-payment-snapshot-invalid");
            var evidence = await EvidenceAsync(db, request, snapshot.Input.EvidenceIds, evidenceOptions, token);
            if (evidence is null || !evidence.SequenceEqual(snapshot.Evidence)) return Conflict("refund-evidence-changed");
            if (await SettlementPaymentProof.DuplicateAsync(db, request.WorkspaceId, evidence, row.Id, token)) return Conflict("excess-payment-proof-duplicate");
            if (await PendingPaymentsAsync(db, row.ExcessId, token) > await OutstandingAsync(db, row.ExcessId, token)) return Conflict("excess-payment-capacity-exceeded");
            var person = (await access.CheckAsync(actor, requestId, SettlementActions.ExcessPaymentReview, row.Id, token)).PersonId;
            if (person is null || person == row.ProposedByPersonId) return Denied();
            row.State = "Accepted"; row.ReviewedBy = actor; row.ReviewedByPersonId = person;
            row.AcceptedAtUtc = row.ReviewedAtUtc = clock.GetUtcNow();
            row.SnapshotJson = JsonSerializer.Serialize(snapshot with { History = [.. snapshot.History, new(command.CommandId, "Accept", actor, person.Value, clock.GetUtcNow(), command.Reason.Trim())] });
            return PaymentResult(row, 200, "excess-payment-accepted");
        }, ct);

    public Task<FinanceResult<SettlementCommandResult>> ReturnExternalPaymentAsync(Guid actor, Guid requestId, Guid proposalId, SettlementCommand command, CancellationToken ct)
        => TransitionPaymentAsync(actor, requestId, proposalId, "Return", command, ct);
    public Task<FinanceResult<SettlementCommandResult>> RejectExternalPaymentAsync(Guid actor, Guid requestId, Guid proposalId, SettlementCommand command, CancellationToken ct)
        => TransitionPaymentAsync(actor, requestId, proposalId, "Reject", command, ct);
    public Task<FinanceResult<SettlementCommandResult>> CancelExternalPaymentAsync(Guid actor, Guid requestId, Guid proposalId, SettlementCommand command, CancellationToken ct)
        => TransitionPaymentAsync(actor, requestId, proposalId, "Cancel", command, ct);
    private Task<FinanceResult<SettlementCommandResult>> TransitionPaymentAsync(Guid actor, Guid requestId, Guid proposalId, string operation, SettlementCommand command, CancellationToken ct)
        => executor.ExecuteAsync(actor, requestId, operation == "Cancel" ? SettlementActions.ExcessPaymentCancel : SettlementActions.ExcessPaymentReview,
            proposalId, command, SettlementCommandExecutor.Hash(new { operation, proposalId }),
            async token =>
            {
                var row = await db.Set<SettlementExcessPayment>().SingleAsync(x => x.Id == proposalId, token);
                if (row.AcceptedAtUtc is not null || row.State is not ("Pending" or "Returned")) return Conflict("excess-payment-not-editable");
                var person = (await access.CheckAsync(actor, requestId, operation == "Cancel" ? SettlementActions.ExcessPaymentCancel : SettlementActions.ExcessPaymentReview, row.Id, token)).PersonId;
                if (person is null || operation == "Cancel" && person != row.ProposedByPersonId) return Denied();
                var snapshot = ReadPayment(row);
                if (snapshot is null) return Conflict("excess-payment-snapshot-invalid");
                row.State = operation switch { "Return" => "Returned", "Reject" => "Rejected", _ => "Cancelled" };
                if (operation != "Cancel") { row.ReviewedBy = actor; row.ReviewedByPersonId = person; row.ReviewedAtUtc = clock.GetUtcNow(); }
                row.SnapshotJson = JsonSerializer.Serialize(snapshot with { History = [.. snapshot.History, new(command.CommandId, operation, actor, person.Value, clock.GetUtcNow(), command.Reason.Trim())] });
                return PaymentResult(row, 200, "excess-payment-transitioned");
            }, ct);

    internal static Task<bool> ActiveAsync(Tpr10DbContext db, AdvanceRequest request, Guid excess, CancellationToken ct)
        => db.Set<SettlementExcessCase>().AnyAsync(x => x.Id == excess && x.RequestId == request.Id && x.WorkspaceId == request.WorkspaceId
            && x.ProjectId == request.ProjectId && x.FamilyId == request.FamilyId && x.State == "Pending", ct);
    private static Task<decimal> PendingPaymentsAsync(Tpr10DbContext db, Guid excess, CancellationToken ct)
        => db.Set<SettlementExcessPayment>().Where(x => x.ExcessId == excess && x.AcceptedAtUtc == null && (x.State == "Pending" || x.State == "Returned")).SumAsync(x => x.Amount, ct);
    private static async Task<decimal> OutstandingAsync(Tpr10DbContext db, Guid excess, CancellationToken ct)
        => await db.Set<SettlementExcessCase>().Where(x => x.Id == excess).Select(x => x.Amount).SingleAsync(ct)
            - await db.Set<SettlementExcessPayment>().Where(x => x.ExcessId == excess && x.AcceptedAtUtc != null).SumAsync(x => x.Amount, ct);
    private static FinanceResult<SettlementCommandResult> PaymentResult(SettlementExcessPayment row, int status, string code)
        => new(status, code, new(row.RequestId, row.Id, row.State, 0, 0, row.Version));
    internal static ExcessPaymentSnapshot? ReadPayment(SettlementExcessPayment row)
    {
        try
        {
            var s = JsonSerializer.Deserialize<ExcessPaymentSnapshot>(row.SnapshotJson);
            return s is not null && s.SchemaVersion == 1 && s.Input is not null && s.Evidence is not null && s.History is not null
                && s.Input.ExcessId == row.ExcessId && s.Input.RecipientPersonId == row.RecipientPersonId && s.Input.Amount == row.Amount && s.Input.PaidOn == row.PaidOn
                && s.Input.EvidenceIds is { Length: > 0 } && s.Input.EvidenceIds[0] == row.EvidenceId
                && s.Input.EvidenceIds.SequenceEqual(s.Evidence.Select(x => x.Id)) ? s : null;
        }
        catch (JsonException) { return null; }
    }
    private T New<T>(AdvanceRequest request, Guid actor, Guid person, SettlementCommand command) where T : class, new()
    {
        var row = new T(); var e = db.Entry(row);
        e.Property("Id").CurrentValue = Guid.NewGuid(); e.Property("WorkspaceId").CurrentValue = request.WorkspaceId;
        e.Property("ProjectId").CurrentValue = request.ProjectId; e.Property("FamilyId").CurrentValue = request.FamilyId;
        e.Property("RequestId").CurrentValue = request.Id; e.Property("CommandId").CurrentValue = command.CommandId;
        e.Property("ProposedBy").CurrentValue = actor; e.Property("ProposedByPersonId").CurrentValue = person;
        e.Property("CreatedAtUtc").CurrentValue = clock.GetUtcNow(); e.Property("Reason").CurrentValue = command.Reason.Trim();
        return row;
    }
}
public sealed record ExcessPaymentSnapshot(int SchemaVersion, ExcessPaymentInput Input, RefundEvidenceSnapshot[] Evidence, CorrectionHistory[] History);
