using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Requests;
using static TPR10.Api.FieldAdvances.Payments.InstallmentService;

namespace TPR10.Api.FieldAdvances.Payments;

public sealed class CeilingAmendmentService(Tpr10DbContext db, PaymentCommandExecutor executor,
    RequestDraftService drafts, FinancePersonResolver people, TimeProvider clock)
{
    internal const string HoldCode = "CeilingAmendment";

    public Task<FinanceResult<PaymentView>> BeginAsync(Guid actor, Guid requestId, RequestContent content, PaymentCommand meta, CancellationToken ct)
        => executor.ExecuteAsync(actor, requestId, null, "ceiling-amend", meta,
            PaymentCommandExecutor.Hash(new { Operation = "ceiling.begin", Content = content }), async token =>
            {
                var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == requestId, token);
                var account = await db.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == requestId, token);
                if (!await drafts.AuthorizeAuthorAsync(actor, request, token)) return Denied();
                var current = await db.Set<AdvanceRequestRevision>().SingleOrDefaultAsync(x => x.RequestId == requestId && x.IsCurrent, token);
                if (request.RelationKind != "Normal" || request.State != "Approved" || account.Spendable <= 0
                    || current is null || current.Id != account.ApprovedRevisionId || current.SubmittedAtUtc is null
                    || current.ConfirmedByPersonId != request.OwnerPersonId || current.Total != account.Approved
                    || !await db.Set<AdvanceApprovalStep>().AnyAsync(x => x.RevisionId == current.Id, token)
                    || await db.Set<AdvanceApprovalStep>().AnyAsync(x => x.RevisionId == current.Id && x.Outcome != "Approved", token)
                    || await ActiveHoldAsync(db, requestId, token) is not null || !await UnpaidAsync(db, account, token)) return Conflict();
                var calculated = await drafts.CalculateAsync(new(request.WorkspaceId, request.ProjectId), content, token);
                if (calculated.Value is null) return new(calculated.Status, calculated.Code, null);
                var number = await db.Set<AdvanceRequestRevision>().Where(x => x.RequestId == requestId).MaxAsync(x => x.RevisionNumber, token);
                if (number == int.MaxValue) return Conflict();
                var person = await people.ResolveAsync(actor, request.WorkspaceId, token);
                if (person is null) return Denied();
                db.Add(new AdvancePaymentHold
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = request.WorkspaceId,
                    ProjectId = request.ProjectId,
                    FamilyId = request.FamilyId,
                    RequestId = requestId,
                    Code = HoldCode,
                    Reason = meta.Reason,
                    CreatedBy = actor,
                    CreatedByPersonId = person.Value,
                    CreatedAtUtc = clock.GetUtcNow()
                });
                current.IsCurrent = false;
                // Hold and current-pointer change are protected by the same family lock as payment start.
                await db.SaveChangesAsync(token);
                var (estimate, policy) = calculated.Value;
                var revision = new AdvanceRequestRevision
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = request.WorkspaceId,
                    ProjectId = request.ProjectId,
                    RequestId = requestId,
                    RevisionNumber = number + 1,
                    IsCurrent = true,
                    ContentJson = JsonSerializer.Serialize(content),
                    ContentHash = PaymentCommandExecutor.Hash(content),
                    Total = estimate.Total,
                    CreatedBy = actor,
                    CreatedByPersonId = person.Value,
                    CreatedAtUtc = clock.GetUtcNow()
                };
                db.Add(revision); drafts.AddLines(request, revision, estimate, policy);
                request.State = "Draft"; request.HoldCode = null;
                PaymentCommandExecutor.RegisterFinalAuthorization(db, requestId, null, meta.CommandId,
                    finalToken => drafts.AuthorizeAuthorAsync(actor, request, finalToken));
                return Success(requestId);
            }, ct);

    public Task<FinanceResult<PaymentView>> ActivateAsync(Guid actor, Guid requestId, PaymentCommand meta, CancellationToken ct)
        => executor.ExecuteAsync(actor, requestId, null, "ceiling-amend", meta,
            PaymentCommandExecutor.Hash(new { Operation = "ceiling.activate" }), async token =>
            {
                var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == requestId, token);
                if (!await drafts.AuthorizeAuthorAsync(actor, request, token)) return Denied();
                var account = await db.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == requestId, token);
                var current = await db.Set<AdvanceRequestRevision>().SingleOrDefaultAsync(x => x.RequestId == requestId && x.IsCurrent, token);
                var hold = await ActiveHoldAsync(db, requestId, token);
                if (request.State != "Approved" || request.HoldCode is not null || hold is null || current is null
                    || account.ApprovedRevisionId is null || current.Id == account.ApprovedRevisionId
                    || current.SubmittedAtUtc is null || current.ConfirmedByPersonId != request.OwnerPersonId
                    || !await db.Set<AdvanceApprovalStep>().AnyAsync(x => x.RevisionId == current.Id, token)
                    || await db.Set<AdvanceApprovalStep>().AnyAsync(x => x.RevisionId == current.Id && x.Outcome != "Approved", token)
                    || !await ClearedAsync(db, account, token)) return Conflict();
                var person = await people.ResolveAsync(actor, request.WorkspaceId, token);
                if (person is null) return Denied();
                account.ApprovedRevisionId = current.Id; account.Approved = current.Total; account.Spendable = current.Total;
                account.Version = checked(account.Version + 1);
                hold.ReleasedAtUtc = clock.GetUtcNow(); hold.ReleasedByPersonId = person;
                PaymentCommandExecutor.RegisterFinalAuthorization(db, requestId, null, meta.CommandId,
                    finalToken => drafts.AuthorizeAuthorAsync(actor, request, finalToken));
                return Success(requestId);
            }, ct);

    internal static Task<AdvancePaymentHold?> ActiveHoldAsync(Tpr10DbContext db, Guid requestId, CancellationToken ct)
        => db.Set<AdvancePaymentHold>().SingleOrDefaultAsync(x => x.RequestId == requestId && x.InstallmentId == null
            && x.Code == HoldCode && x.ReleasedAtUtc == null, ct);

    internal static async Task<bool> UnpaidAsync(Tpr10DbContext db, AdvancePaymentAccount account, CancellationToken ct)
        => !account.EverPaid && account.Paid == 0
            && !await db.Set<AdvancePaymentResult>().AnyAsync(x => x.RequestId == account.RequestId, ct)
            && !await db.Set<AdvancePaymentAttempt>().AnyAsync(x => x.RequestId == account.RequestId && x.State != "Unpaid", ct);

    internal static async Task<bool> ClearedAsync(Tpr10DbContext db, AdvancePaymentAccount account, CancellationToken ct)
        => account.Reserved == 0 && await UnpaidAsync(db, account, ct)
            && !await db.Set<AdvanceInstallment>().AnyAsync(x => x.RequestId == account.RequestId && x.State != "Cancelled", ct);

    // All ordinary 7B commands continue using their existing receipt and approval contracts.
    internal static async Task<bool> CanProgressAsync(Tpr10DbContext db, Guid requestId, CancellationToken ct)
    {
        var account = await db.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == requestId, ct);
        if (account.ApprovedRevisionId is null) return true;
        var current = await db.Set<AdvanceRequestRevision>().SingleAsync(x => x.RequestId == requestId && x.IsCurrent, ct);
        return current.Id != account.ApprovedRevisionId && await ActiveHoldAsync(db, requestId, ct) is not null
            && await UnpaidAsync(db, account, ct);
    }

    internal static FinanceResult<PaymentView> Success(Guid requestId)
        => new(200, "payment-success", new(requestId, null, 0, 0, null, "", 0, 0, 0, 0, 0, []));
}
