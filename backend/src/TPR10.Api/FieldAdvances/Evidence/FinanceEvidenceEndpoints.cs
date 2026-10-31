using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Payments;

namespace TPR10.Api.FieldAdvances.Evidence;

public static class FinanceEvidenceEndpoints
{
    public static void MapFinanceEvidence(this RouteGroupBuilder group)
    {
        PaymentEndpoints.Contract(group.MapPost("/requests/{id}/payment-evidence", Stage))
            .Accepts<byte[]>("image/png", "image/jpeg", "application/pdf").Produces<EvidenceTicket>(201).ProducesProblem(413);
        PaymentEndpoints.Contract(group.MapGet("/payment-evidence/{id}", Download))
            .Produces<byte[]>(200, "application/octet-stream");
    }
    private static async Task<IResult> Stage(Guid id, HttpContext c, FinanceEvidenceService evidence, FinanceEvidenceOptions options, CancellationToken ct)
    {
        var actor = PaymentEndpoints.Actor(c);
        // Raw body streaming delegates the actual bounded read and content validation to the evidence service.
        var type = c.Request.ContentType ?? "";
        var result = await evidence.StageAsync(actor, id, c.Request.Body, type, ct);
        return result.Value is null ? PaymentHttp.Error(result.Status, result.Code)
            : PaymentHttp.Protect(actor, id, PaymentHttp.Result(result), "record");
    }
    private static async Task<IResult> Download(Guid id, HttpContext c, FinanceEvidenceService evidence, Tpr10DbContext db, CancellationToken ct)
    {
        var actor = PaymentEndpoints.Actor(c);
        var result = await evidence.ReadAsync(actor, id, ct);
        if (result.Value is null) return PaymentHttp.Error(result.Status, result.Code);
        var row = await db.Set<AdvanceEvidence>().AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.RequestId, x.ContentType }).SingleAsync(ct);
        c.Response.Headers.XContentTypeOptions = "nosniff";
        var extension = row.ContentType switch { "image/png" => ".png", "image/jpeg" => ".jpg", "application/pdf" => ".pdf", _ => ".bin" };
        return PaymentHttp.Protect(actor, row.RequestId, Results.File(result.Value, row.ContentType,
            "payment-evidence-" + id.ToString("N") + extension, enableRangeProcessing: false), "evidence-read");
    }
}
