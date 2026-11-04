using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
namespace TPR10.Api.FieldAdvances.Settlements;

public sealed record DocumentInput(string Issuer, string? Number, bool HasNoNumber, DateOnly IssuedOn, decimal Amount, Guid[] EvidenceIds)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EvidenceType { get; init; }
}

public sealed class ExpenseDocumentService(Tpr10DbContext db, SettlementAccess access, SettlementCommandExecutor executor,
    SettlementEvidenceService evidence, TimeProvider clock)
{
    public Task<FinanceResult<SettlementCommandResult>> RegisterAsync(Guid actor, Guid requestId, DocumentInput input, SettlementCommand command, CancellationToken ct)
        => SaveAsync(actor, requestId, null, input, command, ct);
    public Task<FinanceResult<SettlementCommandResult>> RegisterAdjustmentAsync(Guid actor, Guid requestId, Guid sourceRoundId,
        DocumentInput input, SettlementCommand command, CancellationToken ct)
        => SaveAsync(actor, requestId, null, input, command, ct, sourceRoundId);
    public Task<FinanceResult<SettlementCommandResult>> UpdateAsync(Guid actor, Guid requestId, Guid documentId, DocumentInput input, SettlementCommand command, CancellationToken ct)
        => SaveAsync(actor, requestId, documentId, input, command, ct);

    private async Task<FinanceResult<SettlementCommandResult>> SaveAsync(Guid actor, Guid requestId, Guid? documentId,
        DocumentInput input, SettlementCommand command, CancellationToken ct, Guid? sourceRoundId = null)
    {
        var action = sourceRoundId is null ? SettlementActions.Document : SettlementActions.ExpensePropose;
        if (db.Database.CurrentTransaction is not null) throw new InvalidOperationException("เตรียมหลักฐานก่อนเริ่มธุรกรรม");
        // Authorization precedes validation and replay, including malformed requests.
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            if (!(await access.CheckAsync(actor, requestId, action, documentId, ct)).Allowed)
                return new(403, "finance-access-denied", null);
        }
        db.ChangeTracker.Clear();
        if (input is null || string.IsNullOrWhiteSpace(input.Issuer) || input.Issuer.Length > 500
            || input.Number?.Length > 200 || (input.HasNoNumber ? input.Number is not null : string.IsNullOrWhiteSpace(input.Number))
            || input.Amount <= 0 || input.Amount > 9999999999999999.99m || decimal.Round(input.Amount, 2) != input.Amount
            || input.EvidenceType is { } kind && (string.IsNullOrWhiteSpace(kind) || kind.Length > 200)
            || input.IssuedOn == default)
            return new(422, "document-invalid", null);
        var prepared = await evidence.PrepareLinksAsync(actor, requestId, input.EvidenceIds, ct, sourceRoundId is { } source ? new ExpenseEvidenceTarget(source) : null);
        if (prepared is null) return new(422, "document-invalid", null);
        var payload = sourceRoundId is null ? SettlementCommandExecutor.Hash(new { operation = documentId is null ? "register-document" : "update-document", documentId, input })
            : SettlementCommandExecutor.Hash(new { operation = "register-adjustment-document", sourceRoundId, input });
        return await executor.ExecuteAsync(actor, requestId, action, documentId, command, payload, async token =>
        {
            if (sourceRoundId is { } source && !await db.Set<SettlementRound>().AnyAsync(x => x.Id == source && x.RequestId == requestId && x.AcceptedAtUtc != null, token))
                return new(409, "expense-source-invalid", null);
            var links = await evidence.LockedLinksAsync(requestId, prepared, token);
            if (links is null) return new(422, "document-invalid", null);
            var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == requestId, token);
            if (links.Any(x => x.WorkspaceId != request.WorkspaceId || x.ProjectId != request.ProjectId || x.FamilyId != request.FamilyId))
                return new(422, "document-invalid", null);
            var person = (await access.CheckAsync(actor, requestId, action, documentId, token)).PersonId;
            if (person is null) return new(403, "finance-access-denied", null);
            ExpenseDocument doc;
            if (documentId is { } id)
            {
                doc = await db.Set<ExpenseDocument>().SingleAsync(x => x.Id == id, token);
                // Repair draft metadata only while EVERY reference is still an untouched draft.
                // Historical references remain authoritative even if removed from the latest draft.
                if (doc.AcceptedAtUtc is not null || await (
                    from allocation in db.Set<ExpenseAllocation>()
                    join revision in db.Set<SettlementRevision>() on allocation.RevisionId equals revision.Id
                    where allocation.DocumentId == id && (allocation.State != "Draft" || allocation.AcceptedAtUtc != null
                        || revision.State != "Draft" || revision.SubmittedAtUtc != null || revision.ConfirmedAtUtc != null
                        || revision.AcceptedAtUtc != null)
                    select allocation.Id).AnyAsync(token))
                    return new(409, "document-in-use", null);
                // The executor sets the same version after mutation; cases must bind the new version now.
                doc.Version = checked(command.EntityVersion!.Value + 1);
            }
            else
            {
                doc = new()
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = request.WorkspaceId,
                    ProjectId = request.ProjectId,
                    FamilyId = request.FamilyId,
                    RequestId = requestId,
                    CommandId = command.CommandId,
                    ProposedBy = actor,
                    ProposedByPersonId = person.Value,
                    CreatedAtUtc = clock.GetUtcNow()
                };
                db.Add(doc);
            }
            doc.Issuer = input.Issuer.Trim(); doc.DocumentNumber = input.Number?.Trim(); doc.HasNoNumber = input.HasNoNumber;
            doc.IssuedOn = input.IssuedOn; doc.Amount = input.Amount; doc.EvidenceId = input.EvidenceIds[0]; doc.Reason = command.Reason;
            doc.Fingerprint = SettlementCommandExecutor.Hash(new { Issuer = doc.Issuer.ToUpperInvariant(), Number = doc.DocumentNumber?.ToUpperInvariant(), doc.HasNoNumber, doc.IssuedOn, Amount = doc.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) });
            doc.SnapshotJson = JsonSerializer.Serialize(new DocumentEvidence(input.EvidenceIds, links.Select(x => x.Sha256).Distinct().Order().ToArray())
            { EvidenceType = input.EvidenceType?.Trim() });
            var others = await db.Set<ExpenseDocument>().AsNoTracking().Where(x => x.WorkspaceId == request.WorkspaceId && x.Id != doc.Id).ToArrayAsync(token);
            // Keep prior suspicions when important content changes, even if the replacement
            // no longer matches automatically. A fresh human review must resolve that pair.
            var priorPairs = await db.Set<ExpenseDuplicateCase>().AsNoTracking()
                .Where(x => x.DocumentId == doc.Id || x.OtherDocumentId == doc.Id)
                .Select(x => x.DocumentId == doc.Id ? x.OtherDocumentId : x.DocumentId).ToArrayAsync(token);
            var suspicious = false;
            foreach (var other in others)
            {
                var hashes = await HashesAsync(db, other, token);
                if (!priorPairs.Contains(other.Id) && doc.Fingerprint != other.Fingerprint && !links.Any(x => hashes.Contains(x.Sha256))) continue;
                // Same-request reuse must refer to the existing document, never multiply its budget.
                if (other.RequestId == requestId) return new(409, "document-already-registered", null);
                suspicious = true;
                db.Add(new ExpenseDuplicateCase
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = doc.WorkspaceId,
                    ProjectId = doc.ProjectId,
                    FamilyId = doc.FamilyId,
                    RequestId = doc.RequestId,
                    CommandId = command.CommandId,
                    ProposedBy = actor,
                    ProposedByPersonId = person.Value,
                    CreatedAtUtc = clock.GetUtcNow(),
                    DocumentId = doc.Id,
                    DocumentVersion = doc.Version,
                    OtherWorkspaceId = other.WorkspaceId,
                    OtherProjectId = other.ProjectId,
                    OtherFamilyId = other.FamilyId,
                    OtherRequestId = other.RequestId,
                    OtherDocumentId = other.Id,
                    OtherDocumentVersion = other.Version,
                    State = "Pending",
                    Reason = "รอตรวจเอกสาร"
                });
            }
            doc.State = suspicious ? "PendingDuplicate" : "Ready";
            return new(documentId is null ? 201 : 200, "document-saved", new(requestId, doc.Id, doc.State, 0, 0, doc.Version));
        }, ct);
    }
    internal sealed record DocumentEvidence(Guid[] EvidenceIds, string[] Hashes)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? EvidenceType { get; init; }
    }
    internal static async Task<string[]> HashesAsync(Tpr10DbContext db, ExpenseDocument doc, CancellationToken ct)
    {
        var snapshot = JsonSerializer.Deserialize<DocumentEvidence>(doc.SnapshotJson);
        if (snapshot?.Hashes is { Length: > 0 }) return snapshot.Hashes;
        return await db.Set<AdvanceEvidence>().Where(x => x.Id == doc.EvidenceId).Select(x => x.Sha256).ToArrayAsync(ct);
    }
}
