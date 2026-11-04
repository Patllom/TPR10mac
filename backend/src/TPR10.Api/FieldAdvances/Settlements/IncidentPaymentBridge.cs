using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Evidence;
using TPR10.Api.FieldAdvances.Payments;
using static TPR10.Api.FieldAdvances.Settlements.RefundService;

namespace TPR10.Api.FieldAdvances.Settlements;

public sealed class IncidentPaymentBridge(Tpr10DbContext db, SettlementAccess access, SettlementCommandExecutor executor,
    TimeProvider clock, FinanceEvidenceOptions evidenceOptions)
{
    internal async Task<bool> CanReleaseAsync(Guid requestId, Guid incidentId, CancellationToken ct)
    {
        var request = await db.Set<AdvanceRequest>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == requestId, ct);
        var incident = await db.Set<PaymentIncidentResolution>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == incidentId && x.RequestId == requestId, ct);
        if (request is null || incident is null || await ResolvedAsync(request, incident, ct) is not { } snapshot)
            return false;
        var hold = await db.Set<AdvancePaymentHold>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == incident.HoldId, ct);
        if (hold is null || hold.ReleasedAtUtc is not null
            || await db.Set<IncidentPaymentAdjustment>().AnyAsync(x => x.AttemptId == incident.AttemptId
                || x.InstallmentId == incident.InstallmentId, ct)) return false;
        var entries = await db.Set<AdvanceReservationEntry>().AsNoTracking()
            .Where(x => x.InstallmentId == incident.InstallmentId).ToArrayAsync(ct);
        var reserved = entries.Sum(x => x.Action == "Reserve" ? x.Amount : -x.Amount);
        var consumed = entries.Where(x => x.Action == "Consume").ToArray();
        var paid = await db.Set<AdvancePaymentResult>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.AttemptId == incident.AttemptId, ct);
        return reserved >= 0 && (paid is null
            ? reserved == snapshot.Source.InstallmentAmount && consumed.Length == 0
            : reserved == 0 && consumed.Length == 1 && consumed[0].Amount == paid.Amount
                && paid.Amount == snapshot.Source.InstallmentAmount);
    }

    internal async Task<bool> CanPrepareShortfallAsync(Guid requestId, Guid incidentId, CancellationToken ct)
    {
        var request = await db.Set<AdvanceRequest>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == requestId, ct);
        var incident = await db.Set<PaymentIncidentResolution>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == incidentId && x.RequestId == requestId, ct);
        if (request is null || incident is null || await ResolvedAsync(request, incident, ct) is null
            || !await db.Set<AdvancePaymentHold>().AnyAsync(x => x.Id == incident.HoldId
                && x.ReleasedAtUtc != null, ct)
            || !await db.Set<IncidentPaymentAdjustment>().AnyAsync(x => x.IncidentId == incidentId, ct)
            || !await InstallmentService.EligibleAsync(db, requestId, null, ct)
            || !await PaymentCapacity.ClaimFitsAsync(db, incidentId, 0.01m, null, ct)) return false;
        var account = await PaymentCapacity.LockAsync(db, requestId, ct);
        var usage = await EffectivePaymentUsageAsync(requestId, ct);
        return usage >= 0.01m && account.Spendable - usage - account.Reserved + 0.01m >= 0.01m;
    }

    // Capacity usage includes unactivated entitlement. It is deliberately not owner liability:
    // returns never change it, and only a linked reservation can activate the missing payment.
    public async Task<decimal> EffectivePaymentUsageAsync(Guid requestId, CancellationToken ct)
        => PaymentCapacity.Usage(await PaymentCapacity.LockAsync(db, requestId, ct));

    public Task<FinanceResult<SettlementCommandResult>> ReleaseHoldAsync(Guid actor, Guid requestId, Guid incidentId,
        SettlementCommand command, CancellationToken ct)
        => executor.ExecuteAsync(actor, requestId, SettlementActions.IncidentRelease, incidentId, command,
            SettlementCommandExecutor.Hash(new { operation = "incident-release", incidentId }), async token =>
        {
            var incident = await db.Set<PaymentIncidentResolution>().SingleAsync(x => x.Id == incidentId, token);
            var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == requestId, token);
            var snapshot = await ResolvedAsync(request, incident, token);
            if (snapshot is null) return Conflict("incident-not-resolved");
            var hold = await db.Set<AdvancePaymentHold>().SingleAsync(x => x.Id == incident.HoldId, token);
            if (hold.ReleasedAtUtc is not null) return Conflict("incident-already-released");
            var person = (await access.CheckAsync(actor, requestId, SettlementActions.IncidentRelease, incidentId, token)).PersonId;
            if (person is null) return Denied();
            var account = await PaymentCapacity.LockAsync(db, requestId, token);
            var installment = await db.Set<AdvanceInstallment>().SingleAsync(x => x.Id == incident.InstallmentId, token);
            var entries = await db.Set<AdvanceReservationEntry>().Where(x => x.InstallmentId == installment.Id).ToArrayAsync(token);
            if (await db.Set<IncidentPaymentAdjustment>().AnyAsync(x => x.AttemptId == incident.AttemptId || x.InstallmentId == installment.Id, token))
                return Conflict("incident-already-adjusted");
            var reserved = entries.Sum(x => x.Action == "Reserve" ? x.Amount : -x.Amount);
            var consumed = entries.Where(x => x.Action == "Consume").ToArray();
            var paid = await db.Set<AdvancePaymentResult>().SingleOrDefaultAsync(x => x.AttemptId == incident.AttemptId, token);
            if (reserved < 0 || (paid is null
                ? reserved != snapshot.Source.InstallmentAmount || consumed.Length != 0
                : reserved != 0 || consumed.Length != 1 || consumed[0].Amount != paid.Amount || paid.Amount != snapshot.Source.InstallmentAmount))
                return Conflict("incident-reservation-unprovable");
            // Neutralize a pre-Paid reservation once, without a fictitious Consume or rewriting history.
            db.Add(new IncidentPaymentAdjustment
            {
                Id = Guid.NewGuid(),
                WorkspaceId = request.WorkspaceId,
                ProjectId = request.ProjectId,
                FamilyId = request.FamilyId,
                RequestId = requestId,
                InstallmentId = incident.InstallmentId,
                IncidentId = incidentId,
                AttemptId = incident.AttemptId,
                CommandId = command.CommandId,
                Amount = reserved,
                CreatedAtUtc = clock.GetUtcNow()
            });
            account.Reserved = checked(account.Reserved - reserved);
            account.IncidentReservationUsage = checked(account.IncidentReservationUsage + reserved);
            account.Version = checked(account.Version + 1);
            if (paid is null)
            {
                installment.State = "IncidentResolved"; installment.Version = checked(installment.Version + 1);
                var priorClaim = await db.Set<PaymentShortfallClaim>().SingleOrDefaultAsync(x => x.InstallmentId == installment.Id, token);
                if (priorClaim is not null)
                {
                    if (priorClaim.State != "Reserved" || priorClaim.Amount != reserved) return Conflict("incident-claim-unprovable");
                    priorClaim.State = "Settled"; // Retain the parent's activation: this entitlement was already used.
                }
            }
            hold.ReleasedAtUtc = clock.GetUtcNow(); hold.ReleasedByPersonId = person;
            IncidentAccounting.Event(db, request, incident.InstallmentId, actor, person.Value, command, clock.GetUtcNow(), "incident-released",
                new { IncidentId = incidentId, incident.HoldId, incident.AttemptId, NeutralizedReservation = reserved });
            _ = PaymentCapacity.Available(account);
            return new(200, "incident-released", new(requestId, incidentId, "Released", 0, 0, incident.Version));
        }, ct);

    public Task<FinanceResult<SettlementCommandResult>> PrepareShortfallAsync(Guid actor, Guid requestId, Guid incidentId,
        decimal amount, SettlementCommand command, CancellationToken ct)
        => executor.ExecuteAsync(actor, requestId, SettlementActions.ShortfallPrepare, command,
            SettlementCommandExecutor.Hash(new { operation = "incident-shortfall", incidentId, amount }), async token =>
        {
            if (amount <= 0 || !IncidentAccounting.Money(amount)) return new(422, "invalid-shortfall", null);
            var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == requestId, token);
            var incident = await db.Set<PaymentIncidentResolution>().SingleOrDefaultAsync(x => x.Id == incidentId
                && x.RequestId == requestId && x.WorkspaceId == request.WorkspaceId && x.ProjectId == request.ProjectId && x.FamilyId == request.FamilyId, token);
            if (incident is null) return Denied();
            var snapshot = await ResolvedAsync(request, incident, token);
            if (snapshot is null || !await db.Set<AdvancePaymentHold>().AnyAsync(x => x.Id == incident.HoldId && x.ReleasedAtUtc != null, token)
                || !await db.Set<IncidentPaymentAdjustment>().AnyAsync(x => x.IncidentId == incidentId, token)
                || !await InstallmentService.EligibleAsync(db, requestId, null, token)) return Conflict("incident-not-released");
            var account = await PaymentCapacity.LockAsync(db, requestId, token);
            if (!await PaymentCapacity.ClaimFitsAsync(db, incidentId, amount, null, token)) return Conflict("shortfall-exhausted");
            var usage = await EffectivePaymentUsageAsync(requestId, token);
            // Spendable already excludes terminated capacity; do not subtract termination a second time.
            if (usage < amount || account.Spendable - usage - account.Reserved + amount < amount) return Conflict("shortfall-capacity");
            var original = await db.Set<AdvanceInstallmentRevision>().SingleAsync(x => x.Id == snapshot.Source.RevisionId, token);
            var installment = new AdvanceInstallment
            {
                Id = Guid.NewGuid(),
                WorkspaceId = request.WorkspaceId,
                ProjectId = request.ProjectId,
                FamilyId = request.FamilyId,
                RequestId = requestId,
                State = "PendingReview",
                CreatedBy = actor,
                CreatedAtUtc = clock.GetUtcNow()
            };
            var input = new InstallmentInput(amount.ToString("0.00", CultureInfo.InvariantCulture),
                new(original.Channel, request.OwnerPersonId, original.BankCode, original.AccountNumber, original.AccountHolder));
            db.Add(installment);
            db.Add(new AdvanceInstallmentRevision
            {
                Id = Guid.NewGuid(),
                WorkspaceId = request.WorkspaceId,
                ProjectId = request.ProjectId,
                FamilyId = request.FamilyId,
                RequestId = requestId,
                InstallmentId = installment.Id,
                RevisionNumber = 1,
                IsCurrent = true,
                Amount = amount,
                Channel = original.Channel,
                RecipientPersonId = request.OwnerPersonId,
                BankCode = original.BankCode,
                AccountNumber = original.AccountNumber,
                AccountHolder = original.AccountHolder,
                ContentHash = PaymentCommandExecutor.Hash(input),
                Reason = command.Reason.Trim(),
                CreatedBy = actor,
                CreatedAtUtc = clock.GetUtcNow()
            });
            db.Add(new PaymentShortfallClaim
            {
                Id = Guid.NewGuid(),
                WorkspaceId = request.WorkspaceId,
                ProjectId = request.ProjectId,
                FamilyId = request.FamilyId,
                RequestId = requestId,
                InstallmentId = installment.Id,
                IncidentId = incidentId,
                SourceInstallmentId = incident.InstallmentId,
                SourceAttemptId = incident.AttemptId,
                Amount = amount,
                CommandId = command.CommandId,
                CreatedAtUtc = clock.GetUtcNow()
            });
            db.Add(new AdvanceReservationEntry
            {
                Id = Guid.NewGuid(),
                WorkspaceId = request.WorkspaceId,
                ProjectId = request.ProjectId,
                FamilyId = request.FamilyId,
                RequestId = requestId,
                InstallmentId = installment.Id,
                Amount = amount,
                Action = "Reserve",
                CommandId = command.CommandId,
                CreatedAtUtc = clock.GetUtcNow()
            });
            account.ActivatedShortfall = checked(account.ActivatedShortfall + amount);
            account.Reserved = checked(account.Reserved + amount); account.Version = checked(account.Version + 1);
            var person = (await access.CheckAsync(actor, requestId, SettlementActions.ShortfallPrepare, null, token)).PersonId;
            if (person is null) return Denied();
            // This is a new prepare event with a new revision and version; no old review is carried over.
            IncidentAccounting.Event(db, request, installment.Id, actor, person.Value, command, clock.GetUtcNow(), "prepare", new { IncidentId = incidentId, Amount = amount });
            db.Set<AdvancePaymentEvent>().Local.Single(x => x.CommandId == command.CommandId).InstallmentVersion = installment.Version;
            _ = PaymentCapacity.Available(account);
            return new(201, "incident-shortfall-prepared", new(requestId, installment.Id, "PendingReview", 0, 0, installment.Version));
        }, ct);

    private async Task<IncidentSnapshot?> ResolvedAsync(AdvanceRequest request, PaymentIncidentResolution incident, CancellationToken ct)
    {
        if (incident.State != "Accepted" || incident.AcceptedAtUtc is null || IncidentAccounting.Read(incident) is not { } snapshot) return null;
        var source = await IncidentAccounting.SourceAsync(db, request, incident.AttemptId, evidenceOptions, ct, incident.HoldId);
        if (source != snapshot.Source) return null;
        var evidence = await EvidenceAsync(db, request, snapshot.Input.EvidenceIds, evidenceOptions, ct);
        if (evidence is null || !evidence.SequenceEqual(snapshot.Evidence)) return null;
        var recoveries = await db.Set<PaymentIncidentRecovery>().Where(x => x.ResolutionId == incident.Id).ToArrayAsync(ct);
        if (recoveries.Any(x => x.State != "Accepted" || x.AcceptedAtUtc is null || IncidentAccounting.Read(x) is null)) return null;
        foreach (var recovery in recoveries)
        {
            var saved = IncidentAccounting.Read(recovery)!;
            var proof = await EvidenceAsync(db, request, saved.EvidenceIds, evidenceOptions, ct);
            if (proof is null || !proof.SequenceEqual(saved.Evidence)) return null;
        }
        var wrong = recoveries.Where(x => x.ReturnKind == "WrongRecipient").Sum(x => x.Amount);
        var owner = recoveries.Where(x => x.ReturnKind == "Owner").Sum(x => x.Amount);
        return wrong == incident.WrongRecipientReceived && owner <= incident.OwnerReceived
            && owner >= Math.Max(0, incident.OwnerReceived - source.InstallmentAmount) ? snapshot : null;
    }
}
