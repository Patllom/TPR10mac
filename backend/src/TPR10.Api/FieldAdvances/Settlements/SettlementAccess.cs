using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Payments;
using TPR10.Api.FieldAdvances.Requests;

namespace TPR10.Api.FieldAdvances.Settlements;

// Authorization is transaction-bound. Workflow transitions remain the command service's responsibility.
public sealed class SettlementAccess(Tpr10DbContext db, FinanceAccess access, FinancePersonResolver people, TimeProvider clock)
{
    public async Task<SettlementAccessDecision> CheckAsync(Guid actor, Guid requestId, string action, Guid? entityId, CancellationToken ct)
    {
        var capability = Capability(action);
        if (db.Database.CurrentTransaction is null || actor == Guid.Empty || capability is null) return Denied();
        if (action == SettlementActions.DuplicateReview) await DuplicateDocumentService.LockRegistryAsync(db, requestId, ct);
        var request = await FamilyParticipation.LockRequestAsync(db, requestId, ct);
        if (request is null) return Denied();
        // Always read current committed/persisted values, including on a reused tracked context.
        request = await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == requestId, ct);
        var person = await people.ResolveAsync(actor, request.WorkspaceId, ct);
        if (person is null) return Denied();
        var entity = entityId is { } id ? await LoadEntityAsync(action, id, request, ct) : null;
        if (entityId is not null && (entity is null || !InScope(entity, request))
            || NeedsEntity(action) && entity is null) return Denied();
        if (request.State == "Closed" && !AllowsClosed(action, entity))
        {
            Guid? roundId = entity?.Entity switch
            {
                SettlementRound r => r.Id,
                SettlementRevision r => r.RoundId,
                SettlementApprovalStep step => await db.Set<SettlementRevision>().Where(x => x.Id == step.RevisionId).Select(x => (Guid?)x.RoundId).SingleOrDefaultAsync(ct),
                _ => null
            };
            if (roundId is null || !await ExpenseAdjustmentService.ClosedRoundAsync(db, requestId, roundId.Value, ct)) return Denied();
        }
        var scope = new FinanceScope(request.WorkspaceId, request.ProjectId);
        if (capability is "author" or "owner")
        {
            var owner = person == request.OwnerPersonId && (await access.CheckAsync(actor, scope, "request.create", ct)).Allowed;
            var delegated = capability == "author" && person == request.DelegatePersonId
                && (await access.CheckAsync(actor, scope, "request.delegate", ct)).Allowed;
            if (!owner && !delegated) return Denied();
            if (delegated)
            {
                var owners = await FinanceGrantQueries.Current(db, scope, "request.create", clock.GetUtcNow())
                    .Where(x => x.RecipientPersonId == request.OwnerPersonId).Select(x => x.RecipientId).Distinct().ToArrayAsync(ct);
                var eligible = false;
                foreach (var account in owners)
                    if (await people.ResolveAsync(account, request.WorkspaceId, ct) == request.OwnerPersonId) { eligible = true; break; }
                if (!eligible) return Denied();
            }
            return new(true, person, "allowed");
        }
        if (action == SettlementActions.Read)
            return (await new RequestAccess(db, access, people, clock).CheckAsync(actor, requestId, "read", ct)).Allowed
                ? new(true, person, "allowed") : Denied();
        if (!(await access.CheckAsync(actor, scope, capability, ct)).Allowed) return Denied();
        var history = await FamilyParticipation.ReadHistoryAsync(db, request.FamilyId, ct);
        if (!RequestSeparation.Check(person.Value, capability, history, []).Allowed) return Denied();
        if (IsReview(action) && entity is not null && (Guid)entity.Property("ProposedByPersonId").CurrentValue! == person) return Denied();
        if (action == SettlementActions.DuplicateReview)
        {
            var duplicate = (ExpenseDuplicateCase)entity!.Entity;
            if (!(await CheckAsync(actor, duplicate.OtherRequestId, SettlementActions.DuplicateScope, null, ct)).Allowed) return Denied();
            var document = await db.Set<ExpenseDocument>().AsNoTracking().SingleAsync(x => x.Id == duplicate.DocumentId, ct);
            var other = await db.Set<ExpenseDocument>().AsNoTracking().SingleAsync(x => x.Id == duplicate.OtherDocumentId, ct);
            if (document.Version != duplicate.DocumentVersion || other.Version != duplicate.OtherDocumentVersion
                || document.ProposedByPersonId == person || other.ProposedByPersonId == person) return Denied();
        }
        if (action is SettlementActions.Approve or SettlementActions.Check)
        {
            var step = (SettlementApprovalStep)entity!.Entity;
            var level = step.LevelCode switch { "Supervisor" => "supervisor", "ProjectManager" => "project-manager", _ => null };
            if (action == SettlementActions.Check ? step.LevelCode != "Finance" : level is null) return Denied();
            // Only a currently eligible route account may read an undecided assignment.
            // Decided assignments retain their persisted person requirement below.

            var revision = await db.Set<SettlementRevision>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == step.RevisionId
                && x.RequestId == requestId && x.FamilyId == request.FamilyId && x.WorkspaceId == request.WorkspaceId && x.ProjectId == request.ProjectId, ct);
            if (revision?.SubmittedAtUtc is null || revision.ConfirmedByPersonId != request.OwnerPersonId || revision.Total < 0 || revision.Total == 0 && !await db.Set<SettlementRound>().AnyAsync(x => x.Id == revision.RoundId && x.RoundKind == "Adjustment", ct)) return Denied();
            var steps = await db.Set<SettlementApprovalStep>().AsNoTracking().Where(x => x.RevisionId == revision.Id).OrderBy(x => x.StepOrder).ToArrayAsync(ct);
            var previous = steps.Where(x => x.StepOrder < step.StepOrder).ToArray();
            if (steps.Any(x => x.Id != step.Id && x.ReviewedByPersonId == person)
                || previous.Length != step.StepOrder - 1 || previous.Any(x => x.State != "Accepted" || x.ReviewedByPersonId is null)
                || previous.Select(x => x.ReviewedByPersonId).Distinct().Count() != previous.Length) return Denied();
            var route = await new SettlementRouteResolver(db, people, clock).CurrentStepAsync(step, ct);
            if (route.PersonId != person || route.AccountId != actor) return Denied();
            if (step.AssigneePersonId != person)
            {
                if (step.State != "Pending" || step.ReviewedAtUtc is not null) return Denied();
                // A stale original route may fall back; an explicitly changed assignment
                // must still revoke the former assignee's prepared reads and proof access.
                TPR10.Api.FieldAdvances.Requests.RouteChoice? original;
                try { original = System.Text.Json.JsonSerializer.Deserialize<TPR10.Api.FieldAdvances.Requests.RouteChoice>(step.SnapshotJson); }
                catch (System.Text.Json.JsonException) { return Denied(); }
                if (original?.PersonId is null || original.PersonId != step.AssigneePersonId) return Denied();
            }
        }
        if (action == SettlementActions.CompletionPropose
            && !await CurrentApprover(actor, person.Value, request, "project-manager", 0m, history, ct)) return Denied();
        return new(true, person, "allowed");
    }

    private async Task<bool> CurrentApprover(Guid actor, Guid person, AdvanceRequest request, string level, decimal total,
        RequestParticipationHistory history, CancellationToken ct)
    {
        var scope = new FinanceScope(request.WorkspaceId, request.ProjectId);
        var candidates = new ApprovalCandidateReader(db, people, clock);
        var next = level == "supervisor" ? await candidates.SupervisorAsync(request.OwnerPersonId, request.WorkspaceId, ct)
            : await candidates.PrimaryAsync(scope, level, ct);
        var seen = new HashSet<Guid>();
        while (next.PersonId is { } candidate && seen.Add(candidate))
        {
            if (RequestSeparation.Check(candidate, "request.approve", history, []).Allowed)
                return candidate == person && await FinanceGrantQueries.Current(db, scope, "request.approve", clock.GetUtcNow())
                    .AnyAsync(x => x.RecipientId == actor && x.RecipientPersonId == person && x.ApprovalLevels.Contains(level)
                        && x.ApprovalLimit != null && x.ApprovalLimit >= total, ct);
            next = await candidates.SupervisorAsync(candidate, request.WorkspaceId, ct);
        }
        return false;
    }

    internal static string? Capability(string action) => action switch
    {
        SettlementActions.ExcessTrack or SettlementActions.ExpensePropose or SettlementActions.Draft or SettlementActions.Document or SettlementActions.CaseRefundCreate or SettlementActions.RefundPropose or SettlementActions.RefundUpdate or SettlementActions.RefundCancel => "author",
        SettlementActions.Confirm or SettlementActions.Declare or SettlementActions.RefundConfirm => "owner",
        SettlementActions.Approve or SettlementActions.CompletionPropose => "request.approve",
        SettlementActions.ExcessDisposition or SettlementActions.ExcessPaymentReview or SettlementActions.CorrectionCancel or SettlementActions.CaseClose or SettlementActions.CasePaymentReview or SettlementActions.Check or SettlementActions.RefundReview or SettlementActions.CorrectionCreate or SettlementActions.CorrectionPropose
            or SettlementActions.CorrectionReview or SettlementActions.CasePropose or SettlementActions.CaseReview
            or SettlementActions.CompletionReview or SettlementActions.IncidentReview or SettlementActions.IncidentRelease or SettlementActions.RecoveryReview
            or SettlementActions.Close or SettlementActions.DuplicateReview or SettlementActions.DuplicateScope => "finance.check",
        SettlementActions.ExcessPaymentCancel or SettlementActions.ExcessPaymentCreate or SettlementActions.CasePaymentCancel or SettlementActions.CasePaymentCreate or SettlementActions.IncidentPropose or SettlementActions.RecoveryPropose or SettlementActions.ShortfallPrepare => "payment.record",
        SettlementActions.Read => "report.read",
        _ => null
    };
    internal static string? Role(string action) => Capability(action) switch
    {
        "request.approve" => "Approver",
        "finance.check" => "Checker",
        "payment.record" => "Payer",
        _ => null
    };
    private static bool IsReview(string action) => action is SettlementActions.ExcessDisposition or SettlementActions.ExcessPaymentReview or SettlementActions.CasePaymentReview or SettlementActions.DuplicateReview or SettlementActions.Approve or SettlementActions.Check
        or SettlementActions.RefundReview or SettlementActions.CorrectionReview or SettlementActions.CaseReview
        or SettlementActions.CompletionReview or SettlementActions.IncidentReview or SettlementActions.IncidentRelease or SettlementActions.RecoveryReview;
    private static bool NeedsEntity(string action) => IsReview(action) || action is SettlementActions.ExcessPaymentCancel or SettlementActions.CorrectionCancel or SettlementActions.CasePaymentCancel or SettlementActions.RefundConfirm or SettlementActions.RefundUpdate or SettlementActions.RefundCancel;
    private static bool AllowsClosed(string action, EntityEntry? entity) => action is SettlementActions.ExpensePropose or SettlementActions.Read or SettlementActions.Close or SettlementActions.DuplicateScope
        or SettlementActions.CasePropose or SettlementActions.CaseReview or SettlementActions.CorrectionCreate or SettlementActions.CaseRefundCreate
        or SettlementActions.CasePaymentCreate or SettlementActions.CasePaymentReview or SettlementActions.CasePaymentCancel or SettlementActions.CaseClose
        || action is SettlementActions.RefundConfirm or SettlementActions.RefundUpdate or SettlementActions.RefundCancel or SettlementActions.RefundReview
            && entity?.Metadata.FindProperty("AdjustmentCaseId") is not null && entity.Property("AdjustmentCaseId").CurrentValue is Guid
        || action is SettlementActions.CorrectionCancel or SettlementActions.CorrectionPropose or SettlementActions.CorrectionReview
            && entity?.Metadata.FindProperty("AdjustmentCaseId") is not null && entity.Property("AdjustmentCaseId").CurrentValue is Guid;
    internal static bool InScope(EntityEntry entity, AdvanceRequest r) =>
        (Guid)entity.Property("RequestId").CurrentValue! == r.Id && (Guid)entity.Property("FamilyId").CurrentValue! == r.FamilyId
        && (Guid)entity.Property("WorkspaceId").CurrentValue! == r.WorkspaceId && (Guid)entity.Property("ProjectId").CurrentValue! == r.ProjectId;

    // Tables/types are selected by a closed server-owned action map; no SQL identifiers come from clients.
    internal Task<EntityEntry?> LoadEntityAsync(string action, Guid id, AdvanceRequest request, CancellationToken ct) => action switch
    {
        SettlementActions.ExcessDisposition => Load<SettlementExcessCase>(id, request, ct),
        SettlementActions.ExcessPaymentReview or SettlementActions.ExcessPaymentCancel => Load<SettlementExcessPayment>(id, request, ct),
        SettlementActions.Draft => Load<SettlementRound>(id, request, ct),
        SettlementActions.Confirm => Load<SettlementRevision>(id, request, ct),
        SettlementActions.Approve or SettlementActions.Check => Load<SettlementApprovalStep>(id, request, ct),
        SettlementActions.Document => Load<ExpenseDocument>(id, request, ct),
        SettlementActions.DuplicateReview => Load<ExpenseDuplicateCase>(id, request, ct),
        SettlementActions.Declare => Load<SubmissionDeclaration>(id, request, ct),
        SettlementActions.RefundPropose or SettlementActions.RefundReview or SettlementActions.RefundConfirm or SettlementActions.RefundUpdate or SettlementActions.RefundCancel => Load<Refund>(id, request, ct),
        SettlementActions.CorrectionCancel or SettlementActions.CorrectionPropose or SettlementActions.CorrectionReview => Load<RefundCorrection>(id, request, ct),
        SettlementActions.CasePaymentCancel or SettlementActions.CasePaymentReview => Load<SettlementCasePayment>(id, request, ct),
        SettlementActions.CasePropose or SettlementActions.CaseReview => Load<SettlementAdjustmentCase>(id, request, ct),
        SettlementActions.CompletionPropose or SettlementActions.CompletionReview => Load<WorkCompletion>(id, request, ct),
        SettlementActions.IncidentPropose or SettlementActions.IncidentReview or SettlementActions.IncidentRelease => Load<PaymentIncidentResolution>(id, request, ct),
        SettlementActions.RecoveryPropose or SettlementActions.RecoveryReview => Load<PaymentIncidentRecovery>(id, request, ct),
        _ => Task.FromResult<EntityEntry?>(null)
    };
    private async Task<EntityEntry?> Load<T>(Guid id, AdvanceRequest request, CancellationToken ct) where T : class
    {
        var table = db.Model.FindEntityType(typeof(T))!.GetTableName()!;
        var sql = "SELECT * FROM \"" + table + "\" WHERE id={0} AND request_id={1} AND family_id={2} AND workspace_id={3} AND project_id={4} FOR UPDATE";
        var entity = await db.Set<T>().FromSqlRaw(sql, id, request.Id, request.FamilyId, request.WorkspaceId, request.ProjectId).AsNoTracking().SingleOrDefaultAsync(ct);
        return entity is null ? null : db.Entry(entity);
    }
    private static SettlementAccessDecision Denied() => new(false, null, "finance-access-denied");
}
