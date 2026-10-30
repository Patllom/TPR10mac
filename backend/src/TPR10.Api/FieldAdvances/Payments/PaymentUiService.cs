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
    FinancePersonResolver people, ScopeAccess sessions, FinanceEvidenceService evidence, FinanceEvidenceOptions options, TimeProvider clock)
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
            return current is not null && fingerprint == JsonSerializer.Serialize(current);
        });
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
    private async Task<PaymentUiContext?> ContextCore(Guid actor, Guid id, CancellationToken ct)
    {
        if (!(await access.CheckAsync(actor, id, "read", ct)).Allowed) return null;
        var request = await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == id, ct);
        var family = await db.Set<AdvanceFamily>().AsNoTracking().SingleAsync(x => x.Id == request.FamilyId, ct);
        var prepare = (await access.CheckAsync(actor, id, "prepare", ct)).Allowed;
        var check = (await access.CheckAsync(actor, id, "check", ct)).Allowed;
        var person = await people.ResolveAsync(actor, request.WorkspaceId, ct);
        var recovery = new PaymentRecovery(db, people, evidence, options, clock);
        var requestActions = prepare && await EligibleAsync(db, id, null, ct) ? new[] { "installments" } : [];
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
            proposalRows.Add(new(proposal.Id, allowed ? ["confirm"] : []));
        }
        return new(id, family.Version, request.Version, requestActions, rows.ToArray(), proposalRows.ToArray());
    }
    private async Task<bool> RecoveryProposalCurrent(AdvancePaymentProposal proposal, AdvanceInstallment installment, PaymentRecovery recovery, CancellationToken ct)
    {
        var snapshot = PaymentRecovery.Snapshot(proposal); var attempt = await recovery.PendingAttemptAsync(installment, ct);
        if (snapshot is null || attempt is null || snapshot.AttemptId != attempt.Id) return false;
        var owner = await PaymentRecovery.OwnerAsync(db, attempt, ct);
        return snapshot.Owner == owner && (proposal.Kind != "PaymentOutcome" || owner.Actor == proposal.ProposedBy && owner.Person == proposal.ProposedByPersonId);
    }
}
