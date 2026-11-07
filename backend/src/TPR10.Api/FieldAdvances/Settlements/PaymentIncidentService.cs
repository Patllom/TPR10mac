using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Evidence;
using TPR10.Api.FieldAdvances.Payments;
using TPR10.Api.FieldAdvances.Requests;
using static TPR10.Api.FieldAdvances.Settlements.RefundService;

namespace TPR10.Api.FieldAdvances.Settlements;

public sealed record IncidentInput(Guid AttemptId, decimal OwnerReceived, decimal WrongRecipientReceived, Guid[] EvidenceIds);

public sealed class PaymentIncidentService(Tpr10DbContext db, SettlementAccess access, SettlementCommandExecutor executor,
    TimeProvider clock, FinanceEvidenceOptions evidenceOptions)
{
    public Task<FinanceResult<SettlementCommandResult>> ProposeAsync(Guid actor, Guid requestId, IncidentInput input, SettlementCommand command, CancellationToken ct)
        => executor.ExecuteAsync(actor, requestId, SettlementActions.IncidentPropose, command,
            SettlementCommandExecutor.Hash(new { operation = "incident-propose", input }), async token =>
        {
            if (input is null || input.AttemptId == Guid.Empty || !IncidentAccounting.Money(input.OwnerReceived)
                || !IncidentAccounting.Money(input.WrongRecipientReceived)) return Invalid();
            var request = await Request(requestId, token);
            var source = await IncidentAccounting.SourceAsync(db, request, input.AttemptId, evidenceOptions, token);
            if (source is null) return Conflict("incident-source-unprovable");
            if (input.WrongRecipientReceived > 0 && source.ActualRecipientPersonId == request.OwnerPersonId) return Invalid();
            if (await db.Set<PaymentIncidentResolution>().AnyAsync(x => x.AttemptId == input.AttemptId
                && (x.AcceptedAtUtc != null || x.State == "Pending"), token)) return Conflict("incident-already-proposed");
            var evidence = await EvidenceAsync(db, request, input.EvidenceIds, evidenceOptions, token);
            if (evidence is null) return Invalid();
            var person = (await access.CheckAsync(actor, requestId, SettlementActions.IncidentPropose, null, token)).PersonId;
            if (person is null) return Denied();
            var attempt = await db.Set<AdvancePaymentAttempt>().SingleAsync(x => x.Id == input.AttemptId, token);
            var row = new PaymentIncidentResolution
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
                AttemptId = input.AttemptId,
                InstallmentId = attempt.InstallmentId,
                HoldId = source.HoldId,
                OwnerReceived = input.OwnerReceived,
                WrongRecipientReceived = input.WrongRecipientReceived,
                WrongRecipientPersonId = input.WrongRecipientReceived > 0 ? source.ActualRecipientPersonId : null,
                EvidenceId = input.EvidenceIds[0],
                SnapshotJson = JsonSerializer.Serialize(new IncidentSnapshot(1, input, source, evidence))
            };
            db.Add(row);
            return Result(row, 201);
        }, ct);

    // Confirmation is the checker's explicit attestation of actual recipients/amounts and all evidence.
    public Task<FinanceResult<SettlementCommandResult>> ConfirmAsync(Guid actor, Guid requestId, Guid incidentId, SettlementCommand command, CancellationToken ct)
        => executor.ExecuteAsync(actor, requestId, SettlementActions.IncidentReview, incidentId, command,
            SettlementCommandExecutor.Hash(new { operation = "incident-confirm", incidentId }), async token =>
        {
            var row = await db.Set<PaymentIncidentResolution>().SingleAsync(x => x.Id == incidentId, token);
            var snapshot = IncidentAccounting.Read(row);
            if (row.AcceptedAtUtc is not null || row.State != "Pending" || snapshot is null) return Conflict("incident-not-pending");
            var request = await Request(requestId, token);
            if (!await CurrentPayer(row.ProposedBy, row.ProposedByPersonId, request, token)) return Denied();
            executor.RegisterFinalAuthorization(requestId, command.CommandId, finalToken => CurrentPayer(row.ProposedBy, row.ProposedByPersonId, request, finalToken));
            var source = await IncidentAccounting.SourceAsync(db, request, row.AttemptId, evidenceOptions, token);
            if (source is null || source != snapshot.Source) return Conflict("incident-source-changed");
            var evidence = await EvidenceAsync(db, request, snapshot.Input.EvidenceIds, evidenceOptions, token);
            if (evidence is null || !evidence.SequenceEqual(snapshot.Evidence)) return Conflict("incident-evidence-changed");
            if (await db.Set<PaymentIncidentResolution>().AnyAsync(x => x.AttemptId == row.AttemptId && x.AcceptedAtUtc != null, token)) return Conflict("incident-already-accepted");
            var person = (await access.CheckAsync(actor, requestId, SettlementActions.IncidentReview, incidentId, token)).PersonId;
            if (person is null || person == row.ProposedByPersonId) return Denied();
            var at = clock.GetUtcNow();
            row.State = "Accepted"; row.ReviewedBy = actor; row.ReviewedByPersonId = person;
            row.ReviewedAtUtc = at; row.AcceptedAtUtc = at;
            // Preserve the complete proposed fact snapshot. Acceptance metadata is separate.
            IncidentAccounting.Event(db, request, row.InstallmentId, actor, person.Value, command, at, "incident-accepted", new
            {
                IncidentId = row.Id,
                row.AttemptId,
                source.RevisionId,
                source.HoldId,
                source.ProposalId,
                row.OwnerReceived,
                row.WrongRecipientReceived,
                row.WrongRecipientPersonId,
                OwnerExcess = Math.Max(0, row.OwnerReceived - source.InstallmentAmount),
                ReplacesPaid = true,
                RestoresCapacity = false
            });
            return Result(row, 200);
        }, ct);

    public Task<FinanceResult<SettlementCommandResult>> RecordRecoveryAsync(Guid actor, Guid requestId, Guid incidentId,
        decimal amount, string source, Guid[] evidenceIds, SettlementCommand command, CancellationToken ct)
        => executor.ExecuteAsync(actor, requestId, SettlementActions.RecoveryPropose, command,
            SettlementCommandExecutor.Hash(new { operation = "incident-recovery-propose", incidentId, amount, source, evidenceIds }), async token =>
        {
            if (amount <= 0 || !IncidentAccounting.Money(amount) || source is not ("Owner" or "WrongRecipient")) return Invalid();
            var request = await Request(requestId, token);
            var incident = await AcceptedIncident(request, incidentId, token);
            if (incident is null) return Conflict("incident-not-accepted");
            var snapshot = IncidentAccounting.Read(incident);
            var basis = await IncidentAccounting.SourceAsync(db, request, incident.AttemptId, evidenceOptions, token);
            if (snapshot is null || basis != snapshot.Source) return Conflict("incident-source-changed");
            var person = (await access.CheckAsync(actor, requestId, SettlementActions.RecoveryPropose, null, token)).PersonId;
            if (person is null) return Denied();
            var evidence = await EvidenceAsync(db, request, evidenceIds, evidenceOptions, token);
            if (evidence is null) return Invalid();
            if (await IncidentAccounting.RecoveryProofUsedAsync(db, request.WorkspaceId, evidence, null, token)
                || await IncidentAccounting.OrdinaryProofUsedAsync(db, request.WorkspaceId, evidence, token)) return Conflict("recovery-proof-already-used");
            if (!await IncidentAccounting.RecoveryFitsAsync(db, incident, source, amount, null, token)) return Conflict("recovery-exceeds-actual");
            var returnedBy = source == "Owner" ? request.OwnerPersonId : incident.WrongRecipientPersonId;
            if (returnedBy is null) return Invalid();
            var row = new PaymentIncidentRecovery
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
                ResolutionId = incidentId,
                Amount = amount,
                ReturnKind = source,
                ReturnedByPersonId = returnedBy.Value,
                ReturnedOn = SettlementDeadlineCalculator.LocalDate(clock.GetUtcNow()),
                EvidenceId = evidenceIds[0],
                SnapshotJson = JsonSerializer.Serialize(new IncidentRecoverySnapshot(1, incidentId, amount, source, returnedBy.Value, evidenceIds, evidence))
            };
            db.Add(row); return Result(row, 201);
        }, ct);

    public Task<FinanceResult<SettlementCommandResult>> ConfirmRecoveryAsync(Guid actor, Guid requestId, Guid recoveryId, SettlementCommand command, CancellationToken ct)
        => executor.ExecuteAsync(actor, requestId, SettlementActions.RecoveryReview, recoveryId, command,
            SettlementCommandExecutor.Hash(new { operation = "incident-recovery-confirm", recoveryId }), async token =>
        {
            var row = await db.Set<PaymentIncidentRecovery>().SingleAsync(x => x.Id == recoveryId, token);
            var snapshot = IncidentAccounting.Read(row);
            if (row.AcceptedAtUtc is not null || row.State != "Pending" || snapshot is null) return Conflict("recovery-not-pending");
            var request = await Request(requestId, token);
            if (!await CurrentPayer(row.ProposedBy, row.ProposedByPersonId, request, token)) return Denied();
            executor.RegisterFinalAuthorization(requestId, command.CommandId, finalToken => CurrentPayer(row.ProposedBy, row.ProposedByPersonId, request, finalToken));
            var incident = await AcceptedIncident(request, row.ResolutionId, token);
            if (incident is null) return Conflict("incident-not-accepted");
            var incidentSnapshot = IncidentAccounting.Read(incident);
            var basis = await IncidentAccounting.SourceAsync(db, request, incident.AttemptId, evidenceOptions, token);
            if (incidentSnapshot is null || basis != incidentSnapshot.Source) return Conflict("incident-source-changed");
            var evidence = await EvidenceAsync(db, request, snapshot.EvidenceIds, evidenceOptions, token);
            if (evidence is null || !evidence.SequenceEqual(snapshot.Evidence)) return Conflict("recovery-evidence-changed");
            if (await IncidentAccounting.RecoveryProofUsedAsync(db, request.WorkspaceId, evidence, recoveryId, token)
                || await IncidentAccounting.OrdinaryProofUsedAsync(db, request.WorkspaceId, evidence, token)) return Conflict("recovery-proof-already-used");
            if (!await IncidentAccounting.RecoveryFitsAsync(db, incident, row.ReturnKind, row.Amount, row.Id, token)) return Conflict("recovery-exceeds-actual");
            if (row.ReturnedByPersonId != (row.ReturnKind == "Owner" ? request.OwnerPersonId : incident.WrongRecipientPersonId)) return Conflict("recovery-recipient-changed");
            var person = (await access.CheckAsync(actor, requestId, SettlementActions.RecoveryReview, recoveryId, token)).PersonId;
            if (person is null || person == row.ProposedByPersonId) return Denied();
            var at = clock.GetUtcNow(); row.State = "Accepted"; row.ReviewedBy = actor; row.ReviewedByPersonId = person;
            row.ReviewedAtUtc = at; row.AcceptedAtUtc = at;
            IncidentAccounting.Event(db, request, incident.InstallmentId, actor, person.Value, command, at, "incident-recovery-accepted", new
            {
                RecoveryId = row.Id,
                IncidentId = incident.Id,
                incident.AttemptId,
                incident.HoldId,
                row.ReturnKind,
                row.ReturnedByPersonId,
                row.Amount,
                RestoresCapacity = false
            });
            return Result(row, 200);
        }, ct);

    private Task<AdvanceRequest> Request(Guid id, CancellationToken ct) => db.Set<AdvanceRequest>().SingleAsync(x => x.Id == id, ct);
    private Task<PaymentIncidentResolution?> AcceptedIncident(AdvanceRequest r, Guid id, CancellationToken ct)
        => db.Set<PaymentIncidentResolution>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == r.WorkspaceId
            && x.ProjectId == r.ProjectId && x.FamilyId == r.FamilyId && x.RequestId == r.Id && x.State == "Accepted" && x.AcceptedAtUtc != null, ct);
    private async Task<bool> CurrentPayer(Guid actor, Guid person, AdvanceRequest request, CancellationToken ct)
        => await new FinancePersonResolver(db, clock).ResolveAsync(actor, request.WorkspaceId, ct) == person
            && RequestSeparation.Check(person, "payment.record", await FamilyParticipation.ReadHistoryAsync(db, request.FamilyId, ct), []).Allowed
            && await FinanceGrantQueries.Current(db, new(request.WorkspaceId, request.ProjectId), "payment.record", clock.GetUtcNow())
                .AnyAsync(x => x.RecipientId == actor && x.RecipientPersonId == person, ct);
    private static FinanceResult<SettlementCommandResult> Invalid() => new(422, "incident-invalid", null);
    private static FinanceResult<SettlementCommandResult> Result(PaymentIncidentResolution row, int status)
        => new(status, "incident-saved", new(row.RequestId, row.Id, row.State, 0, 0, row.Version));
    private static FinanceResult<SettlementCommandResult> Result(PaymentIncidentRecovery row, int status)
        => new(status, "incident-recovery-saved", new(row.RequestId, row.Id, row.State, 0, 0, row.Version));
}
