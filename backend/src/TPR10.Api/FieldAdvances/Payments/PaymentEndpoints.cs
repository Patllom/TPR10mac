using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Requests;
using TPR10.Api.Identity.Sessions;

namespace TPR10.Api.FieldAdvances.Payments;

public static class PaymentEndpoints
{
    private enum Operation { Create, Update, Submit, Review, Start, Record, Outcome, Mismatch, Cancel, Metadata, Takeover, Confirm, CeilingAmendment, CeilingActivate, Disposition, Finalize }
    internal static Guid Actor(HttpContext c) => c.RequestServices.GetRequiredService<RequestSession>().Entity?.UserId ?? Guid.Empty;
    public static void MapPayments(this RouteGroupBuilder group)
    {
        Contract(group.MapGet("/requests/{id}/payments", Summary)).Produces<PaymentHttpView>();
        Contract(group.MapGet("/payment-scopes", (int? page, int? pageSize, PaymentUiService s, HttpContext c, CancellationToken ct) =>
            s.ScopesAsync(Actor(c), page ?? 1, pageSize ?? 25, ct))).Produces<RequestScopeChoice[]>();
        Contract(group.MapGet("/requests/{id}/payment-context", (Guid id, PaymentUiService s, HttpContext c, CancellationToken ct) =>
            s.ContextAsync(Actor(c), id, ct))).Produces<PaymentUiContext>();
        Contract(group.MapGet("/payments", Queue)).Produces<PaymentHttpView[]>();
        Contract(group.MapGet("/requests/{id}/installments", Installments)).Produces<PaymentCatalogHttp>();
        Contract(group.MapGet("/installments/{id}", Installment)).Produces<InstallmentDetailHttp>();
        Contract(group.MapGet("/payment-proposals/{id}", Proposal)).Produces<PaymentProposalHttp>();
        Contract(group.MapGet("/payment-commands/{commandId}", Lookup)).Produces<PaymentHttpView>();
        Map<InstallmentCommandHttp>("/requests/{id}/installments", Operation.Create);
        Map<InstallmentCommandHttp>("/installments/{id}/update", Operation.Update);
        Map<PaymentCommand>("/installments/{id}/submit", Operation.Submit);
        Map<PaymentReviewHttp>("/installments/{id}/review", Operation.Review);
        Map<PaymentCommand>("/installments/{id}/start", Operation.Start);
        Map<PaymentActualHttp>("/installments/{id}/record", Operation.Record);
        Map<PaymentOutcomeHttp>("/installments/{id}/outcome", Operation.Outcome);
        Map<PaymentActualHttp>("/installments/{id}/mismatch", Operation.Mismatch);
        Map<PaymentCommand>("/installments/{id}/cancel", Operation.Cancel);
        Map<PaymentMetadataHttp>("/installments/{id}/metadata", Operation.Metadata);
        Map<PaymentTakeoverHttp>("/installments/{id}/takeover", Operation.Takeover);
        Map<PaymentCommand>("/payment-proposals/{id}/confirm", Operation.Confirm);
        Map<CeilingAmendmentHttp>("/requests/{id}/ceiling-amendment", Operation.CeilingAmendment);
        Map<PaymentCommand>("/requests/{id}/ceiling-activate", Operation.CeilingActivate);
        Map<PaymentReviewHttp>("/requests/{id}/disposition", Operation.Disposition);
        Map<PaymentCommand>("/payment-proposals/{id}/finalize", Operation.Finalize);
        void Map<T>(string path, Operation operation) where T : notnull => Contract(group.MapPost(path,
            (Guid id, HttpContext c, CancellationToken ct) => Command<T>(id, c, operation, ct)))
            .Accepts<T>("application/json").Produces<PaymentHttpView>();
    }
    internal static RouteHandlerBuilder Contract(RouteHandlerBuilder route) => route.WithMetadata(new PaymentApiMetadata())
        .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409).ProducesProblem(422).ProducesProblem(503);
    private static IResult View(Guid actor, FinanceResult<PaymentView> r) => r.Value is null ? PaymentHttp.Error(r.Status, r.Code)
        : PaymentHttp.Protect(actor, r.Value.RequestId, Results.Json(PaymentHttpView.From(r.Value), statusCode: r.Status));
    private static async Task<IResult> Summary(Guid id, PaymentReadService s, HttpContext c, CancellationToken ct) => View(Actor(c), await s.GetAsync(Actor(c), id, ct));
    private static async Task<IResult> Queue(Guid workspaceId, Guid? projectId, int? skip, int? take, PaymentReadService s, HttpContext c, CancellationToken ct)
    {
        var r = await s.ListAsync(Actor(c), new(workspaceId, projectId), skip ?? 0, take ?? 25, ct);
        return r.Value is null ? PaymentHttp.Error(r.Status, r.Code) : PaymentHttp.Protect(Actor(c), r.Value.Select(x => x.RequestId).ToArray(), Results.Json(r.Value.Select(PaymentHttpView.From)), "queue-read");
    }
    internal static async Task<IResult> Installments(Guid id, PaymentReadService s, HttpContext c, CancellationToken ct)
    {
        var result = await s.InstallmentsAsync(Actor(c), id, ct);
        return result.Value is null ? PaymentHttp.Error(result.Status, result.Code)
            : PaymentHttp.Protect(Actor(c), id, PaymentHttp.Result(result), result.Value.RequiredReadAction);
    }
    private static async Task<IResult> Installment(Guid id, PaymentReadService s, HttpContext c, CancellationToken ct)
    {
        var r = await s.InstallmentAsync(Actor(c), id, ct);
        return r.Value is null ? PaymentHttp.Error(r.Status, r.Code) : PaymentHttp.Protect(Actor(c), r.Value.Summary.RequestId, PaymentHttp.Result(r));
    }
    private static async Task<IResult> Proposal(Guid id, PaymentReadService s, HttpContext c, CancellationToken ct)
    {
        var r = await s.ProposalAsync(Actor(c), id, ct);
        return r.Value is null ? PaymentHttp.Error(r.Status, r.Code) : PaymentHttp.Protect(Actor(c), r.Value.RequestId, PaymentHttp.Result(r), r.Value.Kind == "PaymentDisposition" ? "catalog-read" : "read");
    }
    private static async Task<IResult> Lookup(Guid commandId, PaymentReadService s, HttpContext c, CancellationToken ct)
    {
        var actor = Actor(c); var r = await s.CommandAsync(actor, commandId, ct);
        if (r.Value is null) return PaymentHttp.Error(r.Status, r.Code);
        var action = await c.RequestServices.GetRequiredService<Tpr10DbContext>().Set<AdvancePaymentEvent>().AsNoTracking()
            .Where(x => x.CommandId == commandId && x.ActorId == actor).Select(x => x.Kind).SingleAsync(ct);
        return PaymentHttp.Protect(actor, r.Value.RequestId, Results.Json(PaymentHttpView.From(r.Value)), action);
    }
    private static async Task<IResult> Command<T>(Guid id, HttpContext c, Operation operation, CancellationToken ct)
    {
        T? body;
        try { body = await c.Request.ReadFromJsonAsync<T>(RequestHttp.InputOptions, ct); }
        catch (Exception e) when (e is JsonException or BadHttpRequestException or InvalidOperationException) { return PaymentHttp.Error(400, "invalid-payment"); }
        var meta = body switch
        {
            PaymentCommand x => x,
            InstallmentCommandHttp x => x.Meta,
            PaymentReviewHttp x => x.Meta,
            PaymentActualHttp x => x.Meta,
            PaymentOutcomeHttp x => x.Meta,
            PaymentMetadataHttp x => x.Meta,
            PaymentTakeoverHttp x => x.Meta,
            CeilingAmendmentHttp x => x.Meta,
            _ => null
        };
        if (!PaymentHttp.ValidMeta(meta) || body is null) return PaymentHttp.Error(400, "invalid-payment");
        var actor = Actor(c); var services = c.RequestServices;
        FinanceResult<PaymentView> result;
        var action = operation switch
        {
            Operation.Create or Operation.Update or Operation.Submit => "prepare",
            Operation.Review => "check",
            Operation.Start => "start",
            Operation.Takeover => "takeover",
            Operation.CeilingAmendment or Operation.CeilingActivate => "ceiling-amend",
            Operation.Disposition => "terminate-propose",
            Operation.Finalize => "terminate-confirm",
            Operation.Confirm => "reconcile",
            _ => "record"
        };
        switch (operation)
        {
            case Operation.Create:
                result = await services.GetRequiredService<InstallmentService>().CreateAsync(actor, id, ((InstallmentCommandHttp)(object)body).Input, meta!, ct); break;
            case Operation.Update:
                result = await services.GetRequiredService<InstallmentService>().UpdateAsync(actor, id, ((InstallmentCommandHttp)(object)body).Input, meta!, ct); break;
            case Operation.Submit:
                result = await services.GetRequiredService<InstallmentService>().SubmitAsync(actor, id, meta!, ct); break;
            case Operation.Review:
                result = await services.GetRequiredService<InstallmentReviewService>().ReviewAsync(actor, id, ((PaymentReviewHttp)(object)body).Outcome, meta!, ct); break;
            case Operation.Start:
                result = await services.GetRequiredService<PaymentExecutionService>().StartAsync(actor, id, meta!, ct); break;
            case Operation.Record:
                result = await services.GetRequiredService<PaymentExecutionService>().RecordAsync(actor, id, ((PaymentActualHttp)(object)body).Input, meta!, ct); break;
            case Operation.Outcome:
                result = await services.GetRequiredService<PaymentOutcomeService>().ProposeAsync(actor, id, ((PaymentOutcomeHttp)(object)body).Input, meta!, ct); break;
            case Operation.Mismatch:
                result = await services.GetRequiredService<PaymentHoldService>().ReportMismatchAsync(actor, id, ((PaymentActualHttp)(object)body).Input, meta!, ct); break;
            case Operation.Cancel:
                result = await services.GetRequiredService<PaymentCancellationService>().ProposeAsync(actor, id, meta!, ct); break;
            case Operation.Metadata:
                result = await services.GetRequiredService<PaymentMetadataService>().ProposeAsync(actor, id, ((PaymentMetadataHttp)(object)body).Input, meta!, ct); break;
            case Operation.Takeover:
                result = await services.GetRequiredService<PaymentTakeoverService>().ProposeAsync(actor, id, ((PaymentTakeoverHttp)(object)body).EvidenceIds, meta!, ct); break;
            case Operation.CeilingAmendment:
                result = await services.GetRequiredService<CeilingAmendmentService>().BeginAsync(actor, id, ((CeilingAmendmentHttp)(object)body).Content, meta!, ct); break;
            case Operation.CeilingActivate:
                result = await services.GetRequiredService<CeilingAmendmentService>().ActivateAsync(actor, id, meta!, ct); break;
            case Operation.Disposition:
                result = await services.GetRequiredService<PaymentDispositionService>().ProposeAsync(actor, id, ((PaymentReviewHttp)(object)body).Outcome, meta!, ct); break;
            case Operation.Finalize:
                result = await services.GetRequiredService<PaymentDispositionService>().FinalizeAsync(actor, id, meta!, ct); break;
            case Operation.Confirm:
                var kind = await services.GetRequiredService<Tpr10DbContext>().Set<AdvancePaymentProposal>().AsNoTracking().Where(x => x.Id == id).Select(x => x.Kind).SingleOrDefaultAsync(ct);
                if (kind == "PaymentDisposition") action = "terminate-confirm";
                result = kind switch
                {
                    "PaymentOutcome" => await services.GetRequiredService<PaymentOutcomeService>().ConfirmAsync(actor, id, meta!, ct),
                    "PaymentCancellation" => await services.GetRequiredService<PaymentCancellationService>().ConfirmAsync(actor, id, meta!, ct),
                    "PaymentMetadata" => await services.GetRequiredService<PaymentMetadataService>().ConfirmAsync(actor, id, meta!, ct),
                    "PaymentTakeover" => await services.GetRequiredService<PaymentTakeoverService>().ConfirmAsync(actor, id, meta!, ct),
                    "PaymentDisposition" => await services.GetRequiredService<PaymentDispositionService>().ConfirmAsync(actor, id, meta!, ct),
                    _ => new(403, "finance-access-denied", null)
                }; break;
            default: return PaymentHttp.Error(400, "invalid-payment");
        }
        return result.Value is null ? PaymentHttp.Error(result.Status, result.Code)
            : PaymentHttp.Protect(actor, result.Value.RequestId, Results.Json(PaymentHttpView.From(result.Value), statusCode: result.Status), action);
    }
}
public sealed class PaymentApiMetadata;
