using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Payments;

namespace TPR10.Api.FieldAdvances.Settlements;

public sealed class SettlementSubmissionService(Tpr10DbContext db, SettlementAccess access,
    SettlementCommandExecutor executor, ExpenseAllocationLedger ledger, SettlementRouteResolver routes, TimeProvider clock)
{
    public async Task<FinanceResult<SettlementPreview>> PreviewAsync(Guid actor, Guid requestId, Guid roundId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null) throw new InvalidOperationException("ต้องอ่านตัวอย่างในธุรกรรมแยก");
        db.ChangeTracker.Clear();
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await DuplicateDocumentService.LockRegistryAsync(db, requestId, ct);
            var request = await FamilyParticipation.LockRequestAsync(db, requestId, ct);
            if (request is null)
                return new(403, "finance-access-denied", null);
            var revision = await db.Set<SettlementRevision>().AsNoTracking().Where(x => x.RoundId == roundId && x.RequestId == requestId)
                .OrderByDescending(x => x.RevisionNumber).FirstOrDefaultAsync(ct);
            if (revision is null || !(await access.CheckAsync(actor, requestId, SettlementActions.Confirm, revision.Id, ct)).Allowed)
                return new(403, "finance-access-denied", null);
            if (revision.State is not ("Draft" or "AwaitingOwner")) return new(409, "settlement-round-not-editable", null);
            var preview = await BuildPreviewAsync(db, revision, ct);
            if (preview is null) return new(409, "settlement-approved-snapshot-changed", null);
            if (!(await access.CheckAsync(actor, requestId, SettlementActions.Confirm, revision.Id, ct)).Allowed)
                return new(403, "finance-access-denied", null);
            return new(200, "settlement-preview", preview);
        }
        finally { db.ChangeTracker.Clear(); }
    }

    public Task<FinanceResult<SettlementCommandResult>> SubmitAsync(Guid actor, Guid requestId, Guid roundId,
        SettlementCommand command, CancellationToken ct)
        => executor.ExecuteRoundAsync(actor, requestId, roundId, true, command,
            SettlementCommandExecutor.Hash(new { operation = "submit-settlement", roundId, command.ConfirmationHash }), async (revisionId, token) =>
        {
            var revision = await db.Set<SettlementRevision>().SingleAsync(x => x.Id == revisionId, token);
            var round = await db.Set<SettlementRound>().SingleAsync(x => x.Id == roundId, token);
            if (revision.State is not ("Draft" or "AwaitingOwner") || round.State is not ("Draft" or "AwaitingOwner")
                || revision.SubmittedAtUtc is not null || !await ExpenseAdjustmentService.ValidAsync(db, revision, token)) return Conflict("settlement-round-not-editable");
            var preview = await BuildPreviewAsync(db, revision, token);
            if (preview is null || command.ConfirmationHash is null || command.ConfirmationHash != preview.ConfirmationHash)
                return Conflict("settlement-confirmation-stale");
            var adjustment = await db.Set<SettlementExpenseAdjustment>().AsNoTracking().SingleOrDefaultAsync(x => x.RevisionId == revision.Id, token);
            if (adjustment?.Delta > 0)
            {
                var balance = await SettlementBalanceReader.ReadLockedAsync(db, requestId, token);
                if (balance.Value is null) return Conflict(balance.Code);
                var cap = await db.Set<AdvancePaymentAccount>().Where(x => x.RequestId == requestId).Select(x => x.Approved).SingleAsync(token);
                if (adjustment.Delta > cap - balance.Value.ApprovedExpense) return Conflict("settlement-budget-exceeded");
                if ((balance.Value.PendingRefund > 0 || balance.Value.ConfirmedRefund > 0)
                    && adjustment.Delta > balance.Value.OwnerOutstanding - balance.Value.PendingRefund) return Conflict("refund-capacity-exceeded");
            }
            var route = await routes.ResolveAsync(requestId, roundId, token);
            var choices = new[] { route.Supervisor, route.ProjectManager, route.FinanceChecker };
            if (choices.Any(x => x.PersonId is null || x.AccountId is null) || choices.Select(x => x.PersonId).Distinct().Count() != 3)
                return Conflict("settlement-route-unavailable");
            var lines = await db.Set<SettlementLine>().Where(x => x.RevisionId == revision.Id).ToArrayAsync(token);
            var planned = lines.SelectMany(x => JsonSerializer.Deserialize<SettlementDraftService.LineEvidence>(x.SnapshotJson)!.Allocations).ToArray();
            var reserved = await ledger.ReserveAsync(requestId, roundId, planned, token);
            if (!reserved.Allowed) return Conflict(reserved.Code);
            var owner = (await access.CheckAsync(actor, requestId, SettlementActions.Confirm, revision.Id, token)).PersonId;
            if (owner is null) return new(403, "finance-access-denied", null);
            var at = clock.GetUtcNow();
            var timing = await SettlementCompletionService.SubmissionTimingAsync(db, revision, at, token);
            revision.SnapshotJson = JsonSerializer.Serialize(new { Draft = JsonSerializer.Deserialize<JsonElement>(revision.SnapshotJson), preview.ConfirmationHash, ReviewedSnapshot = preview.SnapshotJson, Timing = timing });
            revision.ConfirmedBy = actor; revision.ConfirmedByPersonId = owner; revision.ConfirmedAtUtc = at;
            revision.SubmittedAtUtc = at; revision.State = "Submitted"; round.State = "Submitted"; round.Version++;
            string[] levels = ["Supervisor", "ProjectManager", "Finance"];
            for (var i = 0; i < choices.Length; i++)
                db.Add(new SettlementApprovalStep
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = revision.WorkspaceId,
                    ProjectId = revision.ProjectId,
                    FamilyId = revision.FamilyId,
                    RequestId = requestId,
                    RevisionId = revision.Id,
                    StepOrder = i + 1,
                    LevelCode = levels[i],
                    AssigneePersonId = choices[i].PersonId,
                    State = "Pending",
                    CommandId = command.CommandId,
                    ProposedBy = actor,
                    ProposedByPersonId = owner.Value,
                    CreatedAtUtc = at,
                    Reason = command.Reason,
                    SnapshotJson = JsonSerializer.Serialize(choices[i])
                });
            return new(200, "settlement-submitted", new(requestId, revision.Id, revision.State, 0, 0, revision.Version));
        }, ct);

    // Only called while holding registry -> identity -> family/request locks. The material
    // includes planned allocations, not the smaller retained ledger balance after an edit.
    internal static async Task<SettlementPreview?> BuildPreviewAsync(Tpr10DbContext db, SettlementRevision revision, CancellationToken ct)
    {
        try
        {
            using var stored = JsonDocument.Parse(revision.SnapshotJson);
            var draft = stored.RootElement.TryGetProperty("Draft", out var original) ? original : stored.RootElement;
            var approvedId = draft.GetProperty("ApprovedRequestRevisionId").GetGuid();
            var account = await db.Set<AdvancePaymentAccount>().AsNoTracking().SingleOrDefaultAsync(x => x.RequestId == revision.RequestId, ct);
            var approved = await db.Set<AdvanceRequestRevision>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == approvedId && x.RequestId == revision.RequestId, ct);
            if (account?.ApprovedRevisionId != approvedId || approved is null) return null;
            var frozen = draft.GetProperty("Policy").Deserialize<PolicySnapshot>();
            var policy = JsonSerializer.Deserialize<PolicySnapshot>(approved.PolicySnapshotJson ?? "null");
            if (policy is null || frozen is null || SettlementCommandExecutor.Hash(policy) != SettlementCommandExecutor.Hash(frozen)) return null;
            var lines = await db.Set<SettlementLine>().AsNoTracking().Where(x => x.RevisionId == revision.Id).OrderBy(x => x.Id)
                .Select(x => new { x.Id, x.SourceLineId, x.CategoryId, x.Description, x.IncurredOn, x.Quantity, x.Rate, x.LumpSum, x.Amount, x.SnapshotJson }).ToArrayAsync(ct);
            var ids = lines.SelectMany(x => JsonSerializer.Deserialize<SettlementDraftService.LineEvidence>(x.SnapshotJson)!.Allocations)
                .Select(x => x.DocumentId).Distinct().ToArray();
            var documents = await db.Set<ExpenseDocument>().AsNoTracking().Where(x => ids.Contains(x.Id) && x.RequestId == revision.RequestId)
                .OrderBy(x => x.Id).ToArrayAsync(ct);
            if (documents.Length != ids.Length) return null;
            var evidenceIds = documents.SelectMany(x => JsonSerializer.Deserialize<ExpenseDocumentService.DocumentEvidence>(x.SnapshotJson)?.EvidenceIds ?? [x.EvidenceId]).Distinct().ToArray();
            var evidence = await db.Set<AdvanceEvidence>().AsNoTracking().Where(x => evidenceIds.Contains(x.Id) && x.RequestId == revision.RequestId)
                .OrderBy(x => x.Id).Select(x => new { x.Id, x.State, x.Sha256, x.Length, x.ContentType }).ToArrayAsync(ct);
            if (evidence.Length != evidenceIds.Length || evidence.Any(x => x.State != "Ready")) return null;
            var material = new
            {
                operation = "settlement-owner-confirmation",
                revision.RequestId,
                revision.RoundId,
                RevisionId = revision.Id,
                Draft = draft.Clone(),
                revision.Total,
                ApprovedRequestRevisionId = approvedId,
                approved.ContentHash,
                approved.PolicyHash,
                Policy = policy,
                account.Approved,
                Lines = lines,
                Documents = documents,
                Evidence = evidence
            };
            var request = await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == revision.RequestId, ct);
            var family = await db.Set<AdvanceFamily>().AsNoTracking().SingleAsync(x => x.Id == revision.FamilyId, ct);
            var round = await db.Set<SettlementRound>().AsNoTracking().SingleAsync(x => x.Id == revision.RoundId, ct);
            return new(revision.Id, revision.Version, SettlementCommandExecutor.Hash(material), JsonSerializer.Serialize(material))
            { RequestId = request.Id, RoundId = round.Id, RequestVersion = request.Version, FamilyVersion = family.Version, RoundVersion = round.Version };
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }
    private static FinanceResult<SettlementCommandResult> Conflict(string code) => new(409, code, null);
}
