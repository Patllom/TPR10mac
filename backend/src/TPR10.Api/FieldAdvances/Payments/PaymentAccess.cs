using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Requests;

namespace TPR10.Api.FieldAdvances.Payments;

// Authorization only: callers still enforce workflow state, versions and command replay.
// Retain the transaction until participation and the protected operation commit together.
public sealed class PaymentAccess(Tpr10DbContext db, FinanceAccess access, FinancePersonResolver people,
    RequestAccess requests, TimeProvider clock)
{
    public async Task<PaymentAccessDecision> CheckAsync(Guid actor, Guid requestId, string action, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null || actor == Guid.Empty || requestId == Guid.Empty) return Denied();
        var capability = action switch
        {
            "prepare" or "start" or "record" or "takeover" => "payment.record",
            "check" or "reconcile" or "terminate-propose" => "finance.check",
            "read" or "evidence-read" => "read",
            "terminate-confirm" => "request.approve",
            _ => null
        };
        if (capability is null) return Denied();
        var request = await FamilyParticipation.LockRequestAsync(db, requestId, ct);
        if (request is null) return Denied();
        var person = await people.ResolveAsync(actor, request.WorkspaceId, ct);
        if (person is null) return Denied();
        if (capability == "read") return (await requests.CheckAsync(actor, requestId, "read", ct)).Allowed
            ? new(true, person) : Denied();
        if (!(await access.CheckAsync(actor, new(request.WorkspaceId, request.ProjectId), capability, ct)).Allowed) return Denied();
        var history = await FamilyParticipation.ReadHistoryAsync(db, request.FamilyId, ct);
        if (!RequestSeparation.Check(person.Value, capability, history, []).Allowed) return Denied();
        if (action == "terminate-confirm")
        {
            var final = await new ApprovalRouteResolver(db, people, clock).FinalRoleAsync(requestId, ct);
            if (final.Route.HoldCode is not null || final.Route.PersonId != person || final.Level is null
                || !await FinanceGrantQueries.Current(db, new(request.WorkspaceId, request.ProjectId), capability, clock.GetUtcNow())
                    .AnyAsync(x => x.RecipientId == actor && x.RecipientPersonId == person
                        && x.ApprovalLevels.Contains(final.Level) && x.ApprovalLimit != null && x.ApprovalLimit >= final.Basis, ct)) return Denied();
        }
        return new(true, person);
    }

    private static PaymentAccessDecision Denied() => new(false, null);
}
