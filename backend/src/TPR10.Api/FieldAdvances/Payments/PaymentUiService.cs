using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Evidence;
using TPR10.Api.FieldAdvances.Requests;
using TPR10.Api.Scopes;
using static TPR10.Api.FieldAdvances.Payments.InstallmentService;

namespace TPR10.Api.FieldAdvances.Payments;

// Read-only hints. Mutation services remain the authority for every command.
public sealed class PaymentUiService(Tpr10DbContext db, PaymentAccess access, FinanceAccess finance,
    FinancePersonResolver people, ScopeAccess sessions, FinanceEvidenceService evidence, FinanceEvidenceOptions options, TimeProvider clock, RequestDraftService drafts)
{
    private static readonly string[] Capabilities = ["finance.check", "payment.record"];
    public Task<IResult> ContextAsync(Guid actor, Guid request, CancellationToken ct) => Prepare(actor, [request],
        token => ContextCore(actor, request, token), ct);
    public Task<IResult> ScopesAsync(Guid actor, int page, int size, CancellationToken ct) =>
        page < 1 || size is < 1 or > 100 || (long)(page - 1) * size > int.MaxValue
        ? Task.FromResult(PaymentHttp.Error(400, "invalid-payment-page"))
        : Prepare(actor, [], token => ScopesCore(actor, page, size, token), ct);

    private async Task<IResult> Prepare<T>(Guid actor, Guid[] requests, Func<CancellationToken, Task<T?>> load, CancellationToken ct) where T : class
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct);
        var identity = await sessions.ValidateSessionAsync(true, ct);
        if (identity.Status is not null || identity.ActorId != actor || actor == Guid.Empty) return PaymentHttp.Error(403, "finance-access-denied");
        var prepared = await load(ct);
        if (prepared is null) return PaymentHttp.Error(403, "finance-access-denied");
        var fingerprint = JsonSerializer.Serialize(prepared);
        // The final delivery wrapper clears tracking, reacquires identity/family locks and compares the
        // whole projection after serialization. Surviving report.read cannot deliver stale action hints.
        return PaymentHttp.ProtectProjection(actor, requests, Results.Json(prepared), async token =>
        {
            var current = await load(token);
            if (current is null) return PaymentHttp.ProjectionOutcome.Denied;
            if (fingerprint == JsonSerializer.Serialize(current)) return PaymentHttp.ProjectionOutcome.Current;
            // เปลี่ยนเฉพาะรุ่นคำขอไม่ใช่การถอนสิทธิ์ แต่ห้ามส่ง projection เก่า
            // ส่วนอื่นต้องตรงทั้งหมด รวมสิทธิ์/action hints, family, owner และ workflow
            if (prepared is PaymentUiContext before && current is PaymentUiContext after
                && before.RequestVersion != after.RequestVersion
                && fingerprint == JsonSerializer.Serialize(after with { RequestVersion = before.RequestVersion }))
                return PaymentHttp.ProjectionOutcome.Stale;
            return PaymentHttp.ProjectionOutcome.Denied;
        }, prepared is PaymentDispositionContext ? "disposition-read" : "read");
    }
    private async Task<RequestScopeChoice[]?> ScopesCore(Guid actor, int page, int size, CancellationToken ct)
    {
        var candidates = FinanceScopeQueries.Candidates(db, actor, projectsOnly: true);
        var rows = new List<RequestScopeChoice>(); long seen = 0, skip = (long)(page - 1) * size;
        for (var offset = 0; rows.Count < size; offset = checked(offset + 100))
        {
            var batch = await candidates.Skip(offset).Take(100).ToArrayAsync(ct);
            var allowed = await finance.CheckManyAsync(actor, batch.Select(x => new FinanceScope(x.WorkspaceId, x.ProjectId)).ToArray(), Capabilities, ct);
            foreach (var c in batch)
            {
                var scope = new FinanceScope(c.WorkspaceId, c.ProjectId); var caps = allowed[scope];
                if (caps.Length == 0 || seen++ < skip) continue;
                rows.Add(new(scope, c.WorkspaceName, c.ProjectName!, caps)); if (rows.Count == size) break;
            }
            if (batch.Length < 100) break;
        }
        return rows.ToArray();
    }
    private async Task<object?> ContextCore(Guid actor, Guid id, CancellationToken ct)
    {
        if (!(await access.CheckAsync(actor, id, "read", ct)).Allowed)
        {
            if (!await PaymentHttp.AssignedDisposition(db, access, actor, id, ct)) return null;
            var limitedRequest = await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == id, ct);
            var limitedFamily = await db.Set<AdvanceFamily>().AsNoTracking().SingleAsync(x => x.Id == limitedRequest.FamilyId, ct);
            var assigned = await db.Set<AdvancePaymentProposal>().AsNoTracking().Where(x => x.RequestId == id && x.Kind == "PaymentDisposition").OrderBy(x => x.Id).ToArrayAsync(ct);
            var hints = new List<PaymentUiProposal>();
            foreach (var proposal in assigned) hints.Add(new(proposal.Id, await DispositionActions(actor, proposal, limitedFamily.Version, limitedRequest.Version, ct)));
            return new PaymentDispositionContext("disposition", id, limitedFamily.Version, limitedRequest.Version, hints.ToArray());
        }
        var request = await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == id, ct);
        var family = await db.Set<AdvanceFamily>().AsNoTracking().SingleAsync(x => x.Id == request.FamilyId, ct);
        var prepare = (await access.CheckAsync(actor, id, "prepare", ct)).Allowed;
        var check = (await access.CheckAsync(actor, id, "check", ct)).Allowed;
        var person = await people.ResolveAsync(actor, request.WorkspaceId, ct);
        var recovery = new PaymentRecovery(db, people, evidence, options, clock);
        var requestActions = new List<string>();
        if (prepare && await EligibleAsync(db, id, null, ct)) requestActions.Add("installments");
        var workflow = await Workflow(actor, request, requestActions, ct);
        var installments = await db.Set<AdvanceInstallment>().AsNoTracking().Where(x => x.RequestId == id).OrderBy(x => x.Id).ToArrayAsync(ct);
        var rows = new List<PaymentUiInstallment>();
        foreach (var installment in installments)
        {
            var actions = new List<string>();
            var revision = await db.Set<AdvanceInstallmentRevision>().AsNoTracking().SingleOrDefaultAsync(x => x.InstallmentId == installment.Id && x.IsCurrent, ct);
            var valid = revision is not null && ValidRevision(revision, request.OwnerPersonId);
            var eligible = valid && await EligibleAsync(db, id, installment.Id, ct);
            var reserved = valid && await ReservationMatchesAsync(db, installment, revision!.Amount, ct);
            var pending = await recovery.PendingAttemptAsync(installment, ct);
            var paid = await db.Set<AdvancePaymentResult>().AsNoTracking().AnyAsync(x => x.InstallmentId == installment.Id, ct);
            var activeAttempt = await db.Set<AdvancePaymentAttempt>().AsNoTracking().AnyAsync(x => x.InstallmentId == installment.Id && x.State != "Unpaid", ct);
            if (prepare && eligible && reserved && installment.State is "Draft" or "PendingReview" or "Returned" or "Reviewed") actions.Add("update");
            if (prepare && eligible && reserved && revision!.ReviewedAtUtc is null && installment.State is "Draft" or "Returned") actions.Add("submit");
            if (check && eligible && reserved && installment.State == "PendingReview" && revision!.ReviewedAtUtc is null) actions.Add("review");
            if (prepare && eligible && reserved && installment.State == "Reviewed" && revision!.ReviewedAtUtc is not null
                && revision.ReviewedBy is not null && revision.ReviewedByPersonId is not null && !activeAttempt && !paid) actions.Add("start");
            if (prepare && valid && reserved && !activeAttempt && !paid && installment.State is "Draft" or "PendingReview" or "Returned" or "Reviewed" or "CancelPending") actions.Add("cancel");
            if (prepare && pending is not null && await recovery.RevisionAsync(pending, ct) is not null)
            {
                var owner = await PaymentRecovery.OwnerAsync(db, pending, ct);
                if (owner.Actor == actor && owner.Person == person)
                {
                    actions.Add("outcome");
                    if (owner.TakeoverId is null && installment.State == "Paying" && pending.State == "Open") actions.Add("record");
                }
                else if (owner.Actor != Guid.Empty && owner.Person != person) actions.Add("takeover");
                actions.Add("mismatch");
            }
            if (prepare && installment.State == "Paid" && paid) { actions.Add("metadata"); actions.Add("mismatch"); }
            rows.Add(new(installment.Id, installment.Version, actions.ToArray()));
        }
        var proposals = await db.Set<AdvancePaymentProposal>().AsNoTracking().Where(x => x.RequestId == id).OrderBy(x => x.Id).ToArrayAsync(ct);
        var proposalRows = new List<PaymentUiProposal>();
        foreach (var proposal in proposals)
        {
            var installment = installments.SingleOrDefault(x => x.Id == proposal.InstallmentId);
            var allowed = check && installment is not null && proposal.ProposedByPersonId != person
                && PaymentRecovery.CurrentProposal(proposal, new(Guid.Empty, family.Version, request.Version, installment.Version, "context"))
                && await recovery.CurrentPayerAsync(proposal, ct);
            if (allowed)
            {
                allowed = proposal.Kind switch
                {
                    "PaymentCancellation" => installment!.State == "CancelPending",
                    "PaymentMetadata" => installment!.State == "Paid",
                    "PaymentOutcome" or "PaymentTakeover" => await RecoveryProposalCurrent(proposal, installment!, recovery, ct),
                    _ => false
                };
            }
            proposalRows.Add(new(proposal.Id, proposal.Kind == "PaymentDisposition" ? await DispositionActions(actor, proposal, family.Version, request.Version, ct) : allowed ? ["confirm"] : []));
        }
        return new PaymentUiContext(id, family.Version, request.Version, requestActions.ToArray(), rows.ToArray(), proposalRows.ToArray()) { Workflow = workflow };
    }
    private async Task<bool> FullyApproved(Guid revision, CancellationToken ct) =>
        await db.Set<AdvanceApprovalStep>().AnyAsync(x => x.RevisionId == revision, ct)
        && !await db.Set<AdvanceApprovalStep>().AnyAsync(x => x.RevisionId == revision && x.Outcome != "Approved", ct);

    private async Task<PaymentWorkflowContext> Workflow(Guid actor, AdvanceRequest request, List<string> actions, CancellationToken ct)
    {
        var account = await db.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == request.Id, ct);
        var revision = await db.Set<AdvanceRequestRevision>().SingleAsync(x => x.RequestId == request.Id && x.IsCurrent, ct);
        var hold = await CeilingAmendmentService.ActiveHoldAsync(db, request.Id, ct);
        var unpaid = await CeilingAmendmentService.UnpaidAsync(db, account, ct);
        var cleared = await CeilingAmendmentService.ClearedAsync(db, account, ct);
        var approved = revision.SubmittedAtUtc is not null && revision.ConfirmedByPersonId == request.OwnerPersonId && await FullyApproved(revision.Id, ct);
        var author = (await access.CheckAsync(actor, request.Id, "ceiling-amend", ct)).Allowed && await drafts.AuthorizeAuthorAsync(actor, request, ct);
        if (author && request.RelationKind == "Normal" && request.State == "Approved" && account.Spendable > 0
            && account.ApprovedRevisionId == revision.Id && revision.Total == account.Approved && approved && hold is null && unpaid)
            actions.Add("ceiling-amendment");
        if (author && request.State == "Approved" && request.HoldCode is null && hold is not null && account.ApprovedRevisionId is not null
            && revision.Id != account.ApprovedRevisionId && approved && cleared) actions.Add("ceiling-activate");
        var family = await db.Set<AdvanceFamily>().SingleAsync(x => x.Id == request.FamilyId, ct);
        // The pending request's contents and identity are never exposed through this family status hint.
        var pendingState = family.PendingSupplementId is null ? null : await db.Set<AdvanceRequest>().Where(x => x.Id == family.PendingSupplementId).Select(x => x.State).SingleOrDefaultAsync(ct);
        var person = await drafts.AuthorizeCreateAsync(actor, new(request.WorkspaceId, request.ProjectId), request.OwnerPersonId, ct);
        if (person is not null && family.PendingSupplementId is null && await SupplementService.EligibleAsync(db, request.FamilyId, ct))
        {
            var history = await FamilyParticipation.ReadHistoryAsync(db, request.FamilyId, ct);
            if (!history.ApprovedPersonIds.Contains(person.Value) && !history.CheckerPersonIds.Contains(person.Value) && !history.PayerPersonIds.Contains(person.Value)) actions.Add("supplements");
        }
        if ((await access.CheckAsync(actor, request.Id, "terminate-propose", ct)).Allowed)
        {
            if (request.State == "Approved" && account.ApprovedRevisionId == revision.Id && approved && revision.Total == account.Approved
                && revision.ApprovalBasisAmount >= account.Approved && account.Spendable > account.Paid) actions.Add("terminate-remaining");
            else if (request.State is "Rejected" or "Withdrawn" && account.ApprovedRevisionId is not null && revision.Id != account.ApprovedRevisionId && hold is not null && unpaid)
            { actions.Add("resume-original"); actions.Add("terminate-remaining"); }
        }
        return new(account.EverPaid, await db.Set<AdvancePaymentAttempt>().AnyAsync(x => x.RequestId == request.Id && x.State != "Matched" && x.State != "Unpaid", ct),
            request.RelationKind, pendingState, hold is null ? null : request.State, approved && request.State == "Approved", cleared,
            request.OwnerPersonId, new(request.WorkspaceId, request.ProjectId));
    }

    // Read-only mirrors of the disposition service's snapshots and guards. Commands remain authoritative.
    private sealed record DispositionSnapshot(string Outcome, Guid ApprovedRevision, Guid AmendmentRevision, Guid HoldId, long AccountVersion);
    private sealed record TerminationSnapshot(bool Termination, Guid ApprovedRevision, Guid HoldId, decimal Approved, decimal Basis);
    private sealed record DispositionOutcome(string Outcome);
    private static string SnapshotHash<T>(AdvancePaymentProposal p, T snapshot) => PaymentCommandExecutor.Hash(new
    {
        p.Id,
        p.WorkspaceId,
        p.ProjectId,
        p.FamilyId,
        p.RequestId,
        p.InstallmentId,
        p.CommandId,
        p.Kind,
        p.FamilyVersion,
        p.RequestVersion,
        p.InstallmentVersion,
        p.ProposedBy,
        p.ProposedByPersonId,
        p.Reason,
        Snapshot = snapshot,
        Outcome = JsonSerializer.Deserialize<DispositionOutcome>(p.AfterJson)
    });
    private async Task<string[]> DispositionActions(Guid actor, AdvancePaymentProposal p, long familyVersion, long requestVersion, CancellationToken ct)
    {
        if (p.InstallmentId is not null || !(await access.CheckAsync(actor, p.RequestId, "terminate-confirm", ct)).Allowed) return [];
        var person = await people.ResolveAsync(actor, p.WorkspaceId, ct);
        if (person is null || person == p.ProposedByPersonId || await people.ResolveAsync(p.ProposedBy, p.WorkspaceId, ct) != p.ProposedByPersonId
            || !RequestSeparation.Check(p.ProposedByPersonId, "finance.check", await FamilyParticipation.ReadHistoryAsync(db, p.FamilyId, ct), []).Allowed
            || !await FinanceGrantQueries.Current(db, new(p.WorkspaceId, p.ProjectId), "finance.check", clock.GetUtcNow())
                .AnyAsync(x => x.RecipientId == p.ProposedBy && x.RecipientPersonId == p.ProposedByPersonId, ct)) return [];
        var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == p.RequestId, ct);
        var a = await db.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == p.RequestId, ct);
        var v = await db.Set<AdvanceRequestRevision>().SingleAsync(x => x.RequestId == p.RequestId && x.IsCurrent, ct);
        try
        {
            var t = JsonSerializer.Deserialize<TerminationSnapshot>(p.BeforeJson);
            if (t is { Termination: true })
            {
                if (p.ContentHash != SnapshotHash(p, t) || JsonSerializer.Deserialize<DispositionOutcome>(p.AfterJson)?.Outcome != "TerminateRemaining"
                    || r.State != "Approved" || a.ApprovedRevisionId != v.Id || v.Id != t.ApprovedRevision || a.Approved != t.Approved
                    || v.Total != a.Approved || v.ApprovalBasisAmount != t.Basis || v.ApprovalBasisAmount < a.Approved
                    || v.SubmittedAtUtc is null || v.ConfirmedByPersonId != r.OwnerPersonId || !await FullyApproved(v.Id, ct)
                    || !await db.Set<AdvancePaymentHold>().AnyAsync(x => x.Id == t.HoldId && x.FamilyId == p.FamilyId && x.RequestId == r.Id && x.InstallmentId == null && x.Code == "PaymentTermination" && x.ReleasedAtUtc == null, ct)
                    || await db.Set<AdvancePaymentProposal>().AnyAsync(x => x.RequestId == r.Id && x.Kind == "PaymentDisposition" && x.RequestVersion > p.RequestVersion, ct)) return [];
                if (p.State == "Pending" && p.FamilyVersion == familyVersion && p.RequestVersion == requestVersion) return ["confirm"];
                if (p.State == "Approved" && p.ReviewedAtUtc is not null && p.ReviewedBy == actor && p.ReviewedByPersonId == person && a.Reserved == 0
                    && !await db.Set<AdvanceInstallment>().AnyAsync(x => x.RequestId == r.Id && x.State != "Paid" && x.State != "Cancelled", ct)
                    && !await db.Set<AdvancePaymentAttempt>().AnyAsync(x => x.RequestId == r.Id && x.State != "Matched" && x.State != "Unpaid", ct)) return ["finalize"];
                return [];
            }
            var d = JsonSerializer.Deserialize<DispositionSnapshot>(p.BeforeJson);
            var hold = await CeilingAmendmentService.ActiveHoldAsync(db, r.Id, ct);
            if (d is null || p.ContentHash != SnapshotHash(p, d) || p.State != "Pending" || p.FamilyVersion != familyVersion || p.RequestVersion != requestVersion
                || d.Outcome is not ("ResumeOriginal" or "TerminateRemaining") || r.State is not ("Rejected" or "Withdrawn")
                || a.ApprovedRevisionId is null || v.Id == a.ApprovedRevisionId || hold is null || d.AmendmentRevision != v.Id
                || d.ApprovedRevision != a.ApprovedRevisionId || d.HoldId != hold.Id || d.AccountVersion != a.Version
                || !await CeilingAmendmentService.UnpaidAsync(db, a, ct)
                || d.Outcome == "TerminateRemaining" && !await CeilingAmendmentService.ClearedAsync(db, a, ct)) return [];
            return ["confirm"];
        }
        catch (JsonException) { return []; }
    }

    private async Task<bool> RecoveryProposalCurrent(AdvancePaymentProposal proposal, AdvanceInstallment installment, PaymentRecovery recovery, CancellationToken ct)
    {
        var snapshot = PaymentRecovery.Snapshot(proposal); var attempt = await recovery.PendingAttemptAsync(installment, ct);
        if (snapshot is null || attempt is null || snapshot.AttemptId != attempt.Id) return false;
        var owner = await PaymentRecovery.OwnerAsync(db, attempt, ct);
        return snapshot.Owner == owner && (proposal.Kind != "PaymentOutcome" || owner.Actor == proposal.ProposedBy && owner.Person == proposal.ProposedByPersonId);
    }
}
