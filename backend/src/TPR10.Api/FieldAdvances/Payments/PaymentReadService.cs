using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.Scopes;

namespace TPR10.Api.FieldAdvances.Payments;

public sealed class PaymentReadService(Tpr10DbContext db, PaymentAccess access, FinanceAccess finance, ScopeAccess sessions)
{
    private static FinanceResult<T> Denied<T>() => new(403, "finance-access-denied", default);
    private async Task<FinanceResult<T>> Read<T>(Guid actor, Guid request, Func<Task<T>> load, CancellationToken ct, string action = "read")
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await PaymentHttp.Allowed(db, access, actor, request, action, ct)) return Denied<T>();
        var value = await load();
        if (!await PaymentHttp.Allowed(db, access, actor, request, action, ct)) return Denied<T>();
        return new(200, "ok", value);
    }
    public Task<FinanceResult<PaymentView>> GetAsync(Guid actor, Guid requestId, CancellationToken ct) =>
        Read(actor, requestId, () => Summary(requestId, null, ct), ct);

    public async Task<FinanceResult<PaymentView[]>> ListAsync(Guid actor, FinanceScope scope, int skip, int take, CancellationToken ct)
    {
        if (skip < 0 || take is < 1 or > 100) return new(400, "invalid-payment-page", null);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct);
        if (scope.ProjectId is null || !(await finance.CheckAsync(actor, scope, "payment.record", ct)).Allowed
            && !(await finance.CheckAsync(actor, scope, "finance.check", ct)).Allowed
            && !(await finance.CheckAsync(actor, scope, "request.approve", ct)).Allowed) return Denied<PaymentView[]>();
        var rows = new List<PaymentView>(); var eligible = 0; var offset = 0;
        while (rows.Count < take)
        {
            var ids = await db.Set<AdvanceRequest>().AsNoTracking().Where(x => x.WorkspaceId == scope.WorkspaceId && x.ProjectId == scope.ProjectId)
                .OrderBy(x => x.Id).Skip(offset).Take(100).Select(x => x.Id).ToArrayAsync(ct);
            foreach (var id in ids)
            {
                if (!await PaymentHttp.Allowed(db, access, actor, id, "queue-read", ct)) continue;
                if (eligible++ < skip) continue;
                rows.Add(await Summary(id, null, ct)); if (rows.Count == take) break;
            }
            if (ids.Length < 100) break;
            offset = checked(offset + ids.Length);
        }
        foreach (var row in rows)
            if (!await PaymentHttp.Allowed(db, access, actor, row.RequestId, "queue-read", ct)) return Denied<PaymentView[]>();
        var identity = await sessions.ValidateSessionAsync(true, ct);
        return identity.Status is null && identity.ActorId == actor ? new(200, "ok", rows.ToArray()) : Denied<PaymentView[]>();
    }
    public Task<FinanceResult<PaymentCatalogHttp>> InstallmentsAsync(Guid actor, Guid request, CancellationToken ct) => Read(actor, request, async () =>
    {
        if (!(await access.CheckAsync(actor, request, "read", ct)).Allowed)
        {
            var assigned = await db.Set<AdvancePaymentProposal>().AsNoTracking().Where(x => x.RequestId == request && x.Kind == "PaymentDisposition")
                .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).ToArrayAsync(ct);
            var decisions = new List<PaymentProposalHttp>(); foreach (var p in assigned) decisions.Add(await Proposal(p, ct));
            return new PaymentCatalogHttp([], decisions.ToArray(), []) { RequiredReadAction = "disposition-read" };
        }
        var ids = await db.Set<AdvanceInstallment>().AsNoTracking().Where(x => x.RequestId == request).OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync(ct);
        var rows = new List<PaymentHttpView>();
        foreach (var id in ids) rows.Add(PaymentHttpView.From(await Summary(request, id, ct)));
        var proposals = await db.Set<AdvancePaymentProposal>().AsNoTracking().Where(x => x.RequestId == request).OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).ToArrayAsync(ct);
        var projected = new List<PaymentProposalHttp>(); foreach (var p in proposals) projected.Add(await Proposal(p, ct));
        var evidence = await db.Set<AdvanceEvidence>().AsNoTracking().Where(x => x.RequestId == request).OrderBy(x => x.Id)
            .Select(x => new EvidenceTicket(x.Id, x.Sha256, x.Length, x.ContentType, x.State)).ToArrayAsync(ct);
        return new PaymentCatalogHttp(rows.ToArray(), projected.ToArray(), evidence);
    }, ct, "catalog-read");
    public async Task<FinanceResult<InstallmentDetailHttp>> InstallmentAsync(Guid actor, Guid id, CancellationToken ct)
    {
        var request = await db.Set<AdvanceInstallment>().AsNoTracking().Where(x => x.Id == id).Select(x => (Guid?)x.RequestId).SingleOrDefaultAsync(ct);
        if (request is null) return Denied<InstallmentDetailHttp>();
        return await Read(actor, request.Value, async () =>
        {
            var revisions = await db.Set<AdvanceInstallmentRevision>().AsNoTracking().Where(x => x.InstallmentId == id && x.RequestId == request)
                .OrderBy(x => x.RevisionNumber).ToArrayAsync(ct);
            var proposals = await db.Set<AdvancePaymentProposal>().AsNoTracking().Where(x => x.InstallmentId == id && x.RequestId == request).OrderBy(x => x.CreatedAtUtc).ToArrayAsync(ct);
            var projected = new List<PaymentProposalHttp>(); foreach (var proposal in proposals) projected.Add(await Proposal(proposal, ct));
            return new InstallmentDetailHttp(PaymentHttpView.From(await Summary(request.Value, id, ct)), revisions.Select(x => new InstallmentRevisionHttp(
                x.Id, x.RevisionNumber, x.IsCurrent, PaymentHttp.Money(x.Amount), new(x.Channel, x.RecipientPersonId, x.BankCode, x.AccountNumber, x.AccountHolder), x.ReviewedAtUtc, x.Reason)).ToArray(), projected.ToArray(),
                await Evidence(request.Value, id, null, ct));
        }, ct);
    }
    public async Task<FinanceResult<PaymentProposalHttp>> ProposalAsync(Guid actor, Guid id, CancellationToken ct)
    {
        var row = await db.Set<AdvancePaymentProposal>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return Denied<PaymentProposalHttp>();
        return await Read(actor, row.RequestId, async () => await Proposal(await db.Set<AdvancePaymentProposal>().AsNoTracking().SingleAsync(x => x.Id == id, ct), ct), ct, row.Kind == "PaymentDisposition" ? "catalog-read" : "read");
    }
    public async Task<FinanceResult<PaymentView>> CommandAsync(Guid actor, Guid commandId, CancellationToken ct)
    {
        var receipt = await db.Set<FinanceCommandReceipt>().AsNoTracking().SingleOrDefaultAsync(x => x.CommandId == commandId && x.ActorId == actor, ct);
        var e = await db.Set<AdvancePaymentEvent>().AsNoTracking().SingleOrDefaultAsync(x => x.CommandId == commandId && x.ActorId == actor, ct);
        if (receipt is null || e is null || receipt.Action != "payment." + e.Kind) return Denied<PaymentView>();
        return await Read(actor, e.RequestId, async () =>
        {
            // Return the current state after finding the actor-bound receipt. Never deserialize retained private payloads to HTTP.
            return await Summary(e.RequestId, e.InstallmentId, ct);
        }, ct, e.Kind);
    }
    internal async Task<PaymentView> Summary(Guid requestId, Guid? installmentId, CancellationToken ct)
    {
        var request = await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == requestId, ct);
        var family = await db.Set<AdvanceFamily>().AsNoTracking().SingleAsync(x => x.Id == request.FamilyId, ct);
        var account = await db.Set<AdvancePaymentAccount>().AsNoTracking().SingleAsync(x => x.RequestId == requestId, ct);
        var installment = installmentId is null ? null : await db.Set<AdvanceInstallment>().AsNoTracking().SingleAsync(x => x.Id == installmentId && x.RequestId == requestId, ct);
        var holds = await db.Set<AdvancePaymentHold>().AsNoTracking().Where(x => x.FamilyId == family.Id && x.ReleasedAtUtc == null
            && (x.RequestId == null || x.RequestId == requestId) && (installmentId == null || x.InstallmentId == null || x.InstallmentId == installmentId))
            .Select(x => x.Code).Distinct().OrderBy(x => x).ToArrayAsync(ct);
        return new(requestId, installmentId, family.Version, request.Version, installment?.Version, installment?.State ?? request.State,
            account.Approved, account.Spendable, account.Reserved, account.Paid, account.Spendable - account.Reserved - account.Paid, holds);
    }
    private async Task<EvidenceTicket[]> Evidence(Guid request, Guid? installment, Guid[]? ids, CancellationToken ct) =>
        await db.Set<AdvanceEvidence>().AsNoTracking().Where(x => x.RequestId == request && (ids != null ? ids.Contains(x.Id) : x.InstallmentId == installment))
            .OrderBy(x => x.Id).Select(x => new EvidenceTicket(x.Id, x.Sha256, x.Length, x.ContentType, x.State)).ToArrayAsync(ct);
    private async Task<PaymentProposalHttp> Proposal(AdvancePaymentProposal p, CancellationToken ct) => new(p.Id, p.RequestId, p.InstallmentId,
        p.Kind, p.State, p.FamilyVersion, p.RequestVersion, p.InstallmentVersion, p.Reason, Change(p.BeforeJson, p.Kind), Change(p.AfterJson, p.Kind),
        await Evidence(p.RequestId, p.InstallmentId, JsonSerializer.Deserialize<Guid[]>(p.EvidenceIdsJson) ?? [], ct));
    private static PaymentProposalChangeHttp Change(string json, string kind)
    {
        using var document = JsonDocument.Parse(json); var value = document.RootElement;
        string? Text(string name) => value.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
        string? reserved = value.TryGetProperty("Reserved", out var amount) && amount.TryGetDecimal(out var n) ? PaymentHttp.Money(n) : null;
        PaymentMetadataInput? metadata = null; PaymentOutcomeInput? payment = null;
        if (value.TryGetProperty("Input", out var input))
        {
            if (kind == "PaymentMetadata") metadata = input.Deserialize<PaymentMetadataInput>();
            if (kind is "PaymentOutcome" or "PaymentTakeover") payment = input.Deserialize<PaymentOutcomeInput>();
        }
        Guid? revision = value.TryGetProperty("RevisionId", out var rid) && rid.TryGetGuid(out var id) ? id : null;
        string? Money(string name) => value.TryGetProperty(name, out var money) && money.TryGetDecimal(out var number) ? PaymentHttp.Money(number) : null;
        return new(Text("Outcome"), Text("State") ?? Text("PriorState"), reserved, metadata, payment, revision, Money("Approved"), Money("Basis"));
    }
}
