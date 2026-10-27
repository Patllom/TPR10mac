using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.Scopes;
using TPR10.Api.FieldAdvances.Requests;

namespace TPR10.Api.FieldAdvances.Payments;

public sealed record InstallmentCommandHttp(InstallmentInput Input, PaymentCommand Meta);
public sealed record PaymentReviewHttp(string Outcome, PaymentCommand Meta);
public sealed record PaymentActualHttp(ActualPaymentInput Input, PaymentCommand Meta);
public sealed record PaymentOutcomeHttp(PaymentOutcomeInput Input, PaymentCommand Meta);
public sealed record PaymentMetadataHttp(PaymentMetadataInput Input, PaymentCommand Meta);
public sealed record PaymentTakeoverHttp(Guid[] EvidenceIds, PaymentCommand Meta);
public sealed record CeilingAmendmentHttp(RequestContent Content, PaymentCommand Meta);
public sealed record SupplementHttp(RequestContent Content, RequestCommand Meta);
public sealed record PaymentHttpView(Guid RequestId, Guid? InstallmentId, long FamilyVersion, long RequestVersion,
    long? InstallmentVersion, string State, string Approved, string Spendable, string Reserved, string Paid, string Available, string[] HoldCodes)
{
    internal static PaymentHttpView From(PaymentView v) => new(v.RequestId, v.InstallmentId, v.FamilyVersion, v.RequestVersion,
        v.InstallmentVersion, v.State, PaymentHttp.Money(v.Approved), PaymentHttp.Money(v.Spendable), PaymentHttp.Money(v.Reserved),
        PaymentHttp.Money(v.Paid), PaymentHttp.Money(v.Available), v.HoldCodes);
}
public sealed record InstallmentRevisionHttp(Guid Id, int RevisionNumber, bool IsCurrent, string Amount,
    PaymentDestination Destination, DateTimeOffset? ReviewedAtUtc, string Reason);
public sealed record PaymentCatalogHttp(PaymentHttpView[] Installments, PaymentProposalHttp[] Proposals, EvidenceTicket[] Evidence)
{
    // Server-only provenance: final delivery must authorize the projection that was prepared.
    internal string RequiredReadAction { get; init; } = "read";
}
public sealed record InstallmentDetailHttp(PaymentHttpView Summary, InstallmentRevisionHttp[] Revisions,
    PaymentProposalHttp[] Proposals, EvidenceTicket[] Evidence);
public sealed record PaymentProposalChangeHttp(string? Outcome, string? State, string? Reserved,
    PaymentMetadataInput? Metadata, PaymentOutcomeInput? Payment, Guid? RevisionId, string? Approved, string? Basis);
public sealed record PaymentProposalHttp(Guid Id, Guid RequestId, Guid? InstallmentId, string Kind, string State,
    long FamilyVersion, long RequestVersion, long? InstallmentVersion, string Reason,
    PaymentProposalChangeHttp Before, PaymentProposalChangeHttp After, EvidenceTicket[] Evidence);

internal static class PaymentHttp
{
    internal static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
    internal static bool ValidMeta(PaymentCommand? x) => x is not null && x.CommandId != Guid.Empty
        && Safe(x.FamilyVersion) && Safe(x.RequestVersion) && (x.InstallmentVersion is null || Safe(x.InstallmentVersion.Value))
        && !string.IsNullOrWhiteSpace(x.Reason) && x.Reason.Length <= 2000;
    private static bool Safe(long n) => n is >= 0 and <= 9007199254740991;
    internal static IResult Error(int status, string code) => Results.Problem(statusCode: status, title: status switch
    {
        400 => "ข้อมูลการจ่ายเงินไม่ถูกต้อง",
        401 => "กรุณาเข้าสู่ระบบใหม่",
        403 => "ไม่มีสิทธิ์ดำเนินการรายการนี้",
        409 => "สถานะหรือรุ่นข้อมูลเปลี่ยน กรุณาโหลดใหม่",
        413 or 422 => "หลักฐานไม่ถูกต้องหรือเกินขนาดที่กำหนด",
        _ => "ยังยืนยันผลไม่ได้ กรุณาตรวจสถานะด้วยรหัสคำสั่งเดิม"
    }, extensions: new Dictionary<string, object?> { ["code"] = code });
    internal static IResult Result<T>(FinanceResult<T> result) => result.Status is >= 200 and < 300
        ? Results.Json(result.Value, statusCode: result.Status) : Error(result.Status, result.Code);
    internal static IResult Protect(Guid actor, Guid requestId, IResult result, string action = "read") =>
        new ProtectedResult(actor, [requestId], result, action);
    internal static IResult Protect(Guid actor, Guid[] requestIds, IResult result, string action = "read") => new ProtectedResult(actor, requestIds, result, action);
    // A final-role assignment authorizes only the disposition decision context. It never grants source-request or evidence access.
    internal static async Task<bool> AssignedDisposition(Tpr10DbContext db, PaymentAccess access, Guid actor, Guid request, CancellationToken ct) =>
        await db.Set<AdvancePaymentProposal>().AsNoTracking().AnyAsync(x => x.RequestId == request && x.Kind == "PaymentDisposition"
            && (x.State == "Pending" || x.State == "Approved" && db.Set<AdvancePaymentHold>().Any(h => h.RequestId == request
                && h.Code == "PaymentTermination" && h.ReleasedAtUtc == null)), ct)
        && (await access.CheckAsync(actor, request, "terminate-confirm", ct)).Allowed;
    internal static async Task<bool> Allowed(Tpr10DbContext db, PaymentAccess access, Guid actor, Guid request, string action, CancellationToken ct) => action switch
    {
        "disposition-read" => await AssignedDisposition(db, access, actor, request, ct),
        "catalog-read" => (await access.CheckAsync(actor, request, "read", ct)).Allowed || await AssignedDisposition(db, access, actor, request, ct),
        "queue-read" => (await access.CheckAsync(actor, request, "prepare", ct)).Allowed || (await access.CheckAsync(actor, request, "check", ct)).Allowed
            || await AssignedDisposition(db, access, actor, request, ct),
        _ => (await access.CheckAsync(actor, request, action, ct)).Allowed
    };

    // Keep current identity/rights locked until serialization/download completes, not only until the handler returns.
    private sealed class ProtectedResult(Guid actor, Guid[] requests, IResult result, string action) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(5)); var ct = deadline.Token;
            var delivering = false;
            try
            {
                // Serialize before the last authorization check. Expiry can cross serialization too.
                await using var bytes = new MemoryStream();
                var prepared = new DefaultHttpContext { RequestServices = context.RequestServices, RequestAborted = ct };
                prepared.Request.Method = context.Request.Method; prepared.Response.Body = bytes;
                await result.ExecuteAsync(prepared).WaitAsync(ct);
                var db = context.RequestServices.GetRequiredService<Tpr10DbContext>();
                var access = context.RequestServices.GetRequiredService<PaymentAccess>();
                var sessions = context.RequestServices.GetRequiredService<ScopeAccess>();
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct);
                var identity = await sessions.ValidateSessionAsync(true, ct);
                var allowed = identity.Status is null && identity.ActorId == actor && actor != Guid.Empty;
                foreach (var id in requests.Distinct().Order())
                    if (allowed && !await Allowed(db, access, actor, id, action, ct)) allowed = false;
                identity = await sessions.ValidateSessionAsync(true, ct);
                allowed &= identity.Status is null && identity.ActorId == actor;
                context.Response.Headers.CacheControl = "private, no-store";
                if (!allowed) { await Error(403, "finance-access-denied").ExecuteAsync(context).WaitAsync(ct); return; }
                context.Response.StatusCode = prepared.Response.StatusCode;
                foreach (var header in prepared.Response.Headers) context.Response.Headers[header.Key] = header.Value;
                context.Response.Headers.CacheControl = "private, no-store";
                if (!HttpMethods.IsHead(context.Request.Method)) context.Response.ContentLength = bytes.Length;
                ct.ThrowIfCancellationRequested(); delivering = true;
                try
                {
                    bytes.Position = 0; await bytes.CopyToAsync(context.Response.Body, ct).WaitAsync(ct);
                    await context.Response.Body.FlushAsync(ct).WaitAsync(ct);
                    await context.Response.CompleteAsync().WaitAsync(ct);
                }
                catch { context.Abort(); throw; } // Abort before transaction disposal releases the authorization lock.
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException)
            {
                // Never append an error to a partially delivered protected response.
                if (delivering || context.Response.HasStarted || ct.IsCancellationRequested) context.Abort();
                else { context.Response.Clear(); await Error(503, "payment-response-unavailable").ExecuteAsync(context); }
            }
        }
    }
}
