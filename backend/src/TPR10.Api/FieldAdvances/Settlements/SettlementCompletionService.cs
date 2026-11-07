using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;

namespace TPR10.Api.FieldAdvances.Settlements;

public sealed class SettlementCompletionService(Tpr10DbContext db, SettlementAccess access,
    SettlementCommandExecutor executor, TimeProvider clock)
{
    public Task<FinanceResult<SettlementCommandResult>> ConfirmEndAsync(Guid actor, Guid requestId, DateOnly actualEnd,
        SettlementCommand command, CancellationToken ct)
        => CreateEndAsync(actor, requestId, actualEnd, false, command, ct);

    public Task<FinanceResult<SettlementCommandResult>> ProposeEndCorrectionAsync(Guid actor, Guid requestId, DateOnly actualEnd,
        SettlementCommand command, CancellationToken ct)
        => CreateEndAsync(actor, requestId, actualEnd, true, command, ct);

    private Task<FinanceResult<SettlementCommandResult>> CreateEndAsync(Guid actor, Guid requestId, DateOnly actualEnd,
        bool correction, SettlementCommand command, CancellationToken ct)
        => executor.ExecuteAsync(actor, requestId, SettlementActions.CompletionPropose, command,
            SettlementCommandExecutor.Hash(new { operation = correction ? "propose-end-correction" : "confirm-end", entityId = (Guid?)null, requestId, actualEnd }), async token =>
        {
            if (actualEnd == default) return new(422, "settlement-end-invalid", null);
            var basis = await CurrentEndAsync(db, requestId, token);
            if (correction ? basis is null : basis is not null) return Conflict("settlement-end-basis-required");
            CompletionSnapshot? snapshot;
            if (basis is not null)
            {
                snapshot = ReadSnapshot(basis.SnapshotJson);
                if (snapshot is null) return Conflict("settlement-end-basis-invalid");
                snapshot = snapshot with
                {
                    BasisId = basis.Id,
                    BasisVersion = basis.Version,
                    BasisHash = SettlementCommandExecutor.Hash(basis),
                    PreviousDueOn = basis.ClearingDueOn,
                    Assessments = []
                };
            }
            else
            {
                var approvedId = await db.Set<AdvancePaymentAccount>().Where(x => x.RequestId == requestId)
                    .Select(x => x.ApprovedRevisionId).SingleOrDefaultAsync(token);
                var approved = await db.Set<AdvanceRequestRevision>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == approvedId && x.RequestId == requestId, token);
                PolicySnapshot? policy;
                try { policy = JsonSerializer.Deserialize<PolicySnapshot>(approved?.PolicySnapshotJson ?? "null"); }
                catch (JsonException) { return Conflict("settlement-approved-snapshot-required"); }
                if (approved is null || policy?.Content is null || policy.Content.ClearingDays < 0) return Conflict("settlement-approved-snapshot-required");
                snapshot = new(approved.Id, policy, null, null, null, null, [], "Asia/Bangkok");
            }
            DateOnly due;
            try { due = SettlementDeadlineCalculator.DueOn(actualEnd, snapshot.Policy.Content.ClearingDays); }
            catch (ArgumentOutOfRangeException) { return new(422, "settlement-deadline-out-of-range", null); }
            var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == requestId, token);
            var person = (await access.CheckAsync(actor, requestId, SettlementActions.CompletionPropose, null, token)).PersonId;
            if (person is null) return Denied();
            var at = clock.GetUtcNow();
            var row = new WorkCompletion
            {
                Id = Guid.NewGuid(),
                WorkspaceId = request.WorkspaceId,
                ProjectId = request.ProjectId,
                FamilyId = request.FamilyId,
                RequestId = requestId,
                CommandId = command.CommandId,
                ProposedBy = actor,
                ProposedByPersonId = person.Value,
                CreatedAtUtc = at,
                Reason = command.Reason.Trim(),
                CompletedOn = actualEnd,
                ClearingDays = snapshot.Policy.Content.ClearingDays,
                ClearingDueOn = due,
                RefundDueOn = due,
                SupersedesId = basis?.Id,
                State = correction ? "Pending" : "Accepted"
            };
            if (!correction)
            {
                row.ReviewedBy = actor; row.ReviewedByPersonId = person; row.ReviewedAtUtc = at; row.AcceptedAtUtc = at;
                snapshot = snapshot with { Assessments = await AssessAsync(requestId, null, due, token) };
            }
            row.SnapshotJson = JsonSerializer.Serialize(snapshot);
            db.Add(row);
            return new(200, correction ? "settlement-end-correction-proposed" : "settlement-end-confirmed", new(requestId, row.Id, row.State, 0, 0, row.Version));
        }, ct);

    public Task<FinanceResult<SettlementCommandResult>> ConfirmEndCorrectionAsync(Guid actor, Guid requestId, Guid proposalId,
        SettlementCommand command, CancellationToken ct)
        => executor.ExecuteAsync(actor, requestId, SettlementActions.CompletionReview, proposalId, command,
            SettlementCommandExecutor.Hash(new { operation = "confirm-end-correction", entityId = proposalId, requestId }), async token =>
        {
            var row = await db.Set<WorkCompletion>().SingleAsync(x => x.Id == proposalId, token);
            if (row.State != "Pending" || row.AcceptedAtUtc is not null || row.SupersedesId is null) return Conflict("settlement-end-proposal-stale");
            var basis = await CurrentEndAsync(db, requestId, token);
            var snapshot = ReadSnapshot(row.SnapshotJson);
            var proposedBasis = await db.Set<WorkCompletion>().AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == row.SupersedesId && x.RequestId == requestId && x.AcceptedAtUtc != null, token);
            if (basis is null || snapshot is null || proposedBasis is null || snapshot.BasisId != proposedBasis.Id
                || snapshot.BasisVersion != proposedBasis.Version || snapshot.BasisHash != SettlementCommandExecutor.Hash(proposedBasis))
                return Conflict("settlement-end-proposal-stale");
            var person = (await access.CheckAsync(actor, requestId, SettlementActions.CompletionReview, proposalId, token)).PersonId;
            if (person is null) return Denied();
            var at = clock.GetUtcNow();
            if (basis.Id != proposedBasis.Id)
            {
                // Pre-fix competing proposals can already be obsolete. An authorized
                // review resolves that pending obligation without accepting its date.
                row.State = "Cancelled";
                row.ReviewedBy = actor; row.ReviewedByPersonId = person; row.ReviewedAtUtc = at;
                return new(200, "settlement-end-proposal-obsolete", new(requestId, row.Id, row.State, 0, 0, row.Version));
            }
            row.SnapshotJson = JsonSerializer.Serialize(snapshot with { Assessments = await AssessAsync(requestId, basis.ClearingDueOn, row.ClearingDueOn, token) });
            row.ReviewedBy = actor; row.ReviewedByPersonId = person; row.ReviewedAtUtc = at; row.AcceptedAtUtc = at; row.State = "Accepted";
            // Accepting this basis replacement makes competing proposals inapplicable.
            // Retain their proposed facts and snapshots; cancellation is not acceptance.
            var competing = await db.Set<WorkCompletion>().Where(x => x.RequestId == requestId
                && x.Id != row.Id && x.SupersedesId == basis.Id && x.State == "Pending"
                && x.AcceptedAtUtc == null).ToArrayAsync(token);
            foreach (var stale in competing)
            {
                stale.State = "Cancelled";
                stale.Version = checked(stale.Version + 1);
                stale.ReviewedBy = actor;
                stale.ReviewedByPersonId = person;
                stale.ReviewedAtUtc = at;
            }

            return new(200, "settlement-end-correction-confirmed", new(requestId, row.Id, row.State, 0, 0, row.Version));
        }, ct);

    public Task<FinanceResult<SettlementCommandResult>> DeclareCompleteAsync(Guid actor, Guid requestId, bool complete,
        SettlementCommand command, CancellationToken ct)
        => executor.ExecuteAsync(actor, requestId, SettlementActions.Declare, command,
            SettlementCommandExecutor.Hash(new { operation = "declare-complete", entityId = (Guid?)null, requestId, complete }), async token =>
        {
            var completion = await CurrentEndAsync(db, requestId, token);
            if (completion is null) return Conflict("settlement-end-required");
            var previous = await CurrentDeclarationAsync(db, requestId, token);
            if (complete ? previous is { IsWithdrawal: false } : previous is null || previous.IsWithdrawal)
                return Conflict("settlement-declaration-stale");
            var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == requestId, token);
            var person = (await access.CheckAsync(actor, requestId, SettlementActions.Declare, null, token)).PersonId;
            if (person is null) return Denied();
            var at = clock.GetUtcNow();
            var first = await db.Set<SubmissionDeclaration>().AsNoTracking().SingleOrDefaultAsync(x => x.RequestId == requestId
                && x.AcceptedAtUtc != null && x.SupersedesId == null, token);
            var initialEnd = await db.Set<WorkCompletion>().AsNoTracking().SingleAsync(x => x.RequestId == requestId
                && x.AcceptedAtUtc != null && x.SupersedesId == null, token);
            var row = new SubmissionDeclaration
            {
                Id = Guid.NewGuid(),
                WorkspaceId = request.WorkspaceId,
                ProjectId = request.ProjectId,
                FamilyId = request.FamilyId,
                RequestId = requestId,
                CommandId = command.CommandId,
                ProposedBy = actor,
                ProposedByPersonId = person.Value,
                CreatedAtUtc = at,
                Reason = command.Reason.Trim(),
                WorkCompletionId = completion.Id,
                SupersedesId = previous?.Id,
                IsWithdrawal = !complete,
                State = "Accepted",
                ReviewedBy = actor,
                ReviewedByPersonId = person,
                ReviewedAtUtc = at,
                AcceptedAtUtc = at,
                SnapshotJson = JsonSerializer.Serialize(new
                {
                    WorkCompletionId = completion.Id,
                    completion.CompletedOn,
                    completion.ClearingDueOn,
                    completion.RefundDueOn,
                    DeclaredAtUtc = at,
                    Complete = complete,
                    InitialDeclaredAtUtc = first?.CreatedAtUtc ?? at,
                    OriginalWorkCompletionId = initialEnd.Id,
                    OriginalDueOn = initialEnd.ClearingDueOn,
                    InitialCompletionOnTime = SettlementDeadlineCalculator.OnTime(first?.CreatedAtUtc ?? at, initialEnd.ClearingDueOn),
                    CompletionOnTime = complete ? (bool?)SettlementDeadlineCalculator.OnTime(at, completion.ClearingDueOn) : null,
                    TimeZone = "Asia/Bangkok"
                })
            };
            db.Add(row);
            // Acceptance here freezes the owner's declaration; it never certifies any expense.
            return new(200, "settlement-declaration-recorded", new(requestId, row.Id, complete ? "DeclaredComplete" : "DeclarationWithdrawn", 0, 0, row.Version));
        }, ct);

    internal static Task<WorkCompletion?> CurrentEndAsync(Tpr10DbContext db, Guid requestId, CancellationToken ct)
        => db.Set<WorkCompletion>().AsNoTracking().Where(x => x.RequestId == requestId && x.AcceptedAtUtc != null
            && !db.Set<WorkCompletion>().Any(n => n.RequestId == requestId && n.AcceptedAtUtc != null && n.SupersedesId == x.Id)).SingleOrDefaultAsync(ct);

    internal static Task<SubmissionDeclaration?> CurrentDeclarationAsync(Tpr10DbContext db, Guid requestId, CancellationToken ct)
        => db.Set<SubmissionDeclaration>().AsNoTracking().Where(x => x.RequestId == requestId && x.AcceptedAtUtc != null
            && !db.Set<SubmissionDeclaration>().Any(n => n.RequestId == requestId && n.AcceptedAtUtc != null && n.SupersedesId == x.Id)).SingleOrDefaultAsync(ct);

    // Submission timestamps and returned windows are authoritative. Never rewrite an older
    // revision when a later end-date decision or delayed financial review changes the view.
    internal static async Task<SettlementSubmissionTiming> SubmissionTimingAsync(Tpr10DbContext db, SettlementRevision revision, DateTimeOffset at, CancellationToken ct)
    {
        var first = await db.Set<SettlementRevision>().AsNoTracking().Where(x => x.RoundId == revision.RoundId && x.SubmittedAtUtc != null)
            .OrderBy(x => x.RevisionNumber).FirstOrDefaultAsync(ct);
        var original = await db.Set<WorkCompletion>().AsNoTracking().SingleOrDefaultAsync(x => x.RequestId == revision.RequestId && x.AcceptedAtUtc != null && x.SupersedesId == null, ct);
        var current = await CurrentEndAsync(db, revision.RequestId, ct);
        var firstAt = first?.SubmittedAtUtc ?? at;
        return new(firstAt, original is null ? null : SettlementDeadlineCalculator.OnTime(firstAt, original.ClearingDueOn),
            at, current?.Id, original?.ClearingDueOn, current?.ClearingDueOn,
            current is null ? null : SettlementDeadlineCalculator.OnTime(at, current.ClearingDueOn), revision.CorrectionDueOn,
            revision.CorrectionDueOn is { } due ? SettlementDeadlineCalculator.OnTime(at, due) : null);
    }

    private async Task<DeadlineAssessment[]> AssessAsync(Guid requestId, DateOnly? previousDue, DateOnly due, CancellationToken ct)
    {
        var revisions = await db.Set<SettlementRevision>().AsNoTracking().Where(x => x.RequestId == requestId && x.SubmittedAtUtc != null)
            .OrderBy(x => x.Id).Select(x => new { x.Id, At = x.SubmittedAtUtc!.Value }).ToArrayAsync(ct);
        var declarations = await db.Set<SubmissionDeclaration>().AsNoTracking().Where(x => x.RequestId == requestId && x.AcceptedAtUtc != null && !x.IsWithdrawal)
            .OrderBy(x => x.Id).Select(x => new { x.Id, At = x.CreatedAtUtc }).ToArrayAsync(ct);
        return revisions.Select(x => Assessment("Submission", x.Id, x.At)).Concat(declarations.Select(x => Assessment("Completion", x.Id, x.At))).ToArray();
        DeadlineAssessment Assessment(string kind, Guid id, DateTimeOffset at) => new(kind, id, at, previousDue, due,
            previousDue is { } old ? SettlementDeadlineCalculator.OnTime(at, old) : null, SettlementDeadlineCalculator.OnTime(at, due));
    }

    private static CompletionSnapshot? ReadSnapshot(string json)
    {
        try { return JsonSerializer.Deserialize<CompletionSnapshot>(json); }
        catch (JsonException) { return null; }
    }
    private sealed record CompletionSnapshot(Guid ApprovedRequestRevisionId, PolicySnapshot Policy, Guid? BasisId,
        long? BasisVersion, string? BasisHash, DateOnly? PreviousDueOn, DeadlineAssessment[] Assessments, string TimeZone);
    private sealed record DeadlineAssessment(string Kind, Guid EntityId, DateTimeOffset SubmittedAtUtc, DateOnly? PreviousDueOn,
        DateOnly NewDueOn, bool? PreviousOnTime, bool NewOnTime);
    private static FinanceResult<SettlementCommandResult> Conflict(string code) => new(409, code, null);
    private static FinanceResult<SettlementCommandResult> Denied() => new(403, "finance-access-denied", null);
}
