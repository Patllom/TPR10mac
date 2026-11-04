using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.Scopes;
using TPR10.Api.FieldAdvances.Data;
namespace TPR10.Api.FieldAdvances.Settlements;

public sealed record SettlementBody<T>(T Input, SettlementCommand Meta);
public sealed record SettlementEmpty;
public sealed record SettlementDate(DateOnly ActualEnd);
public sealed record SettlementDeclaration(bool Complete);
public sealed record SettlementDuplicate(bool SameDocument);
public sealed record SettlementTrack(Guid DocumentId);
public sealed record SettlementAmount(decimal Amount);
public sealed record SettlementRecovery(decimal Amount, string Source, Guid[] EvidenceIds);
public sealed record SettlementVersion(long RequestVersion, long FamilyVersion);
public sealed record SettlementSummaryHttp(Guid RequestId, string State, SettlementTotals Totals, string[] Holds,
    string[] AllowedActions, long RequestVersion, long FamilyVersion, SettlementDeclarationHttp? Declaration = null);
// PageTotals and PageSettled describe only this scan window's visible requests, never the whole family.
public sealed record SettlementFamilyPage(Guid RequestId, SettlementSummary[] Requests, SettlementTotals PageTotals,
    string TotalsScope, bool PageComplete, bool PageSettled, string? NextCursor, long FamilyVersion);
public sealed record SettlementRow(Guid Id, Guid RequestId, long Version, string State, string Reason, SettlementDocumentHttp? Document = null,
    SettlementRefundHttp? Refund = null, SettlementRoundInfoHttp? Round = null, SettlementAdjustmentHttp? Adjustment = null,
    SettlementIncidentHttp? Incident = null, SettlementCorrectionHttp? Correction = null, SettlementPaymentHttp? Payment = null,
    SettlementExcessHttp? Excess = null, SettlementRecoveryHttp? Recovery = null, SettlementCompletionHttp? Completion = null,
    SettlementDuplicateHttp? Duplicate = null, string[]? AllowedActions = null, DateTimeOffset? SubmittedAtUtc = null, bool? SubmissionOnTime = null);
public sealed record SettlementPage(SettlementRow[] Items, string? NextCursor, long RequestVersion, long FamilyVersion);
public sealed record SettlementAllocationHttp(Guid DocumentId, string Amount);
public sealed record SettlementLineHttp(Guid Id, Guid? SourceLineId, Guid CategoryId, string Description, DateOnly IncurredOn,
    string? Quantity, string? Rate, string? LumpSum, string Amount, SettlementAllocationHttp[] Allocations);
public sealed record SettlementDocumentHttp(Guid Id, long Version, string State, string Issuer, string? DocumentNumber,
    bool HasNoNumber, DateOnly IssuedOn, string Amount, Guid[] EvidenceIds, string? EvidenceType = null);
public sealed record SettlementProofHttp(Guid Id, string State, string Sha256, long Length, string ContentType);
public sealed record SettlementPreviewHttp(Guid RequestId, Guid RoundId, Guid RevisionId, long RequestVersion, long FamilyVersion,
    long RoundVersion, long RevisionVersion, string ConfirmationHash, string Total, string Approved,
    SettlementLineHttp[] Lines, SettlementDocumentHttp[] Documents, SettlementProofHttp[] Evidence);
public sealed record SettlementRoundHttp(Guid RequestId, Guid RoundId, Guid RevisionId, long RequestVersion, long FamilyVersion,
    long RoundVersion, long RevisionVersion, string State, Guid? CurrentStepId, long? CurrentStepVersion, string Total,
    SettlementLineHttp[] Lines, SettlementDocumentHttp[] Documents, SettlementProofHttp[] Evidence,
    string[]? AllowedActions = null, SettlementReturnReviewHttp? ReturnReview = null, DateTimeOffset? SubmittedAtUtc = null, bool? SubmissionOnTime = null);
public sealed record SettlementDeclarationHttp(Guid Id, long Version, bool IsWithdrawal, DateTimeOffset DeclaredAtUtc, Guid WorkCompletionId, bool? CompletionOnTime);
public sealed record SettlementReturnReviewHttp(Guid RevisionId, Guid StepId, long StepVersion, string Reason, Guid[] AffectedLineIds, DateTimeOffset? ReturnedAtUtc, DateOnly? CorrectionDueOn);
public sealed record SettlementRefundHttp(string Amount, DateOnly ReturnedOn, string Channel, string CompanyRecipient, Guid[] EvidenceIds, Guid? AdjustmentCaseId);
public sealed record SettlementRoundInfoHttp(string Kind, Guid? SourceRoundId, Guid? AdjustmentCaseId, Guid? RevisionId, long? RevisionVersion, string? Total);
public sealed record SettlementAdjustmentHttp(string Kind, Guid? RootRefundId, Guid? RootExpenseRoundId, long? BaselineVersion, string? BaselineAmount, string ExternalPaidAmount, Guid[] EvidenceIds);
public sealed record SettlementIncidentHttp(Guid InstallmentId, Guid AttemptId, string OwnerReceived, string WrongRecipientReceived, Guid[] EvidenceIds);
public sealed record SettlementCorrectionHttp(Guid RefundId, Guid? SupersedesId, long BaseVersion, string BeforeAmount, string AfterAmount, string Delta, Guid? AdjustmentCaseId, Guid[] EvidenceIds);
public sealed record SettlementPaymentHttp(Guid ParentId, Guid? SupersedesId, string Amount, DateOnly PaidOn, Guid[] EvidenceIds);
public sealed record SettlementExcessHttp(Guid DocumentId, Guid SourceRevisionId, string Amount,
    string ClaimedAmount, string CapAmount, Guid[] AcceptedPaymentIds);
public sealed record SettlementRecoveryHttp(Guid ResolutionId, string ReturnKind, string Amount, DateOnly ReturnedOn, Guid[] EvidenceIds);
public sealed record SettlementCompletionHttp(DateOnly CompletedOn, DateOnly ClearingDueOn, DateOnly RefundDueOn,
    int ClearingDays, Guid? SupersedesId, DateOnly? PreviousCompletedOn);
public sealed record SettlementDuplicateHttp(Guid DocumentId, long DocumentVersion);
public sealed class SettlementApiMetadata;
internal static class SettlementHttp
{
    internal enum ProjectionOutcome { Current, Stale, Denied }
    internal static readonly JsonSerializerOptions Json = Options();
    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
        options.Converters.Add(new DecimalString()); return options;
    }
    private sealed class DecimalString : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException("ต้องใช้ทศนิยมแบบข้อความ");
            var text = reader.GetString();
            if (text is null || !System.Text.RegularExpressions.Regex.IsMatch(text, @"^-?\d+(\.\d{1,4})?$")
                || !decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) throw new JsonException();
            return value;
        }
        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString("0.00##", CultureInfo.InvariantCulture));
    }
    internal static bool Valid(SettlementCommand? x) => x is not null && x.CommandId != Guid.Empty && Safe(x.RequestVersion) && Safe(x.FamilyVersion)
        && (x.EntityVersion is null || Safe(x.EntityVersion.Value)) && !string.IsNullOrWhiteSpace(x.Reason) && x.Reason.Length <= 2000;
    private static bool Safe(long x) => x is >= 0 and <= 9007199254740991;
    internal static IResult Error(int status, string code) => Results.Problem(statusCode: status, title: status switch
    {
        400 => "ข้อมูลเคลียร์ค่าใช้จ่ายไม่ถูกต้อง",
        401 => "กรุณาเข้าสู่ระบบใหม่",
        403 => "ไม่มีสิทธิ์ดำเนินการรายการนี้",
        409 => "ข้อมูลเปลี่ยน กรุณาโหลดใหม่",
        413 or 415 or 422 => "หลักฐานหรือข้อมูลไม่ถูกต้อง",
        _ => "ยังยืนยันผลไม่ได้ กรุณาตรวจสถานะด้วยรหัสคำสั่งเดิม"
    }, extensions: new Dictionary<string, object?> { ["code"] = code });
    internal static IResult Protect(Guid actor, Guid request, IResult result, Func<CancellationToken, Task<ProjectionOutcome>> validate, bool registry = false)
        => new ProtectedResult(actor, [request], result, validate, registry, true);
    // A receipt is authorized by its exact operation, not by an unrelated report grant.
    // Keep this boundary typed: it cannot carry a financial read projection or file payload.
    internal static IResult ProtectReceipt(Guid actor, Guid request, SettlementCommandResult receipt, int status,
        Func<CancellationToken, Task<ProjectionOutcome>> validate, bool registry = false)
        => new ProtectedResult(actor, [request], Results.Json(receipt, Json, statusCode: status), validate, registry, false);
    internal static IResult ProtectReceiptLookup(Guid actor, Guid request, SettlementReceiptHttp receipt,
        Func<CancellationToken, Task<ProjectionOutcome>> validate)
        => new ProtectedResult(actor, [request], Results.Json(receipt, Json), validate, true, false);
    internal static IResult ProtectRound(Guid actor, Guid request, SettlementRoundHttp value, Func<CancellationToken, Task<ProjectionOutcome>> validate)
        => new ProtectedResult(actor, [request], Results.Json(value, Json), validate, true, false);
    internal static IResult ProtectWork(Guid actor, IResult value, Func<CancellationToken, Task<ProjectionOutcome>> validate)
        => new ProtectedResult(actor, [], value, validate, true, false);
    internal static IResult ProtectRoundProof(Guid actor, Guid request, byte[] bytes, string contentType, Guid evidence,
        Func<CancellationToken, Task<ProjectionOutcome>> validate)
        => new ProtectedResult(actor, [request], Results.File(bytes, contentType, "settlement-evidence-" + evidence.ToString("N"), enableRangeProcessing: false), validate, true, false);
    private sealed class ProtectedResult(Guid actor, Guid[] requests, IResult result, Func<CancellationToken, Task<ProjectionOutcome>> validate, bool registry, bool requireRead) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(5)); var ct = deadline.Token;
            var delivering = false;
            try
            {
                var db = context.RequestServices.GetRequiredService<Tpr10DbContext>();
                var access = context.RequestServices.GetRequiredService<SettlementAccess>();
                var sessions = context.RequestServices.GetRequiredService<ScopeAccess>();
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                if (registry)
                {
                    // Queue delivery has no request ID. Await registry before identity,
                    // exactly as document writers do; validation retains scoped row checks.
                    await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241005)", ct);
                    await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct);
                }
                else await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct);
                var identity = await sessions.ValidateSessionAsync(true, ct);
                var allowed = identity.Status is null && identity.ActorId == actor && actor != Guid.Empty;
                if (requireRead)
                    foreach (var id in requests.Distinct().Order())
                        if (allowed && !(await access.CheckAsync(actor, id, SettlementActions.Read, null, ct)).Allowed) allowed = false;
                var projection = allowed ? await validate(ct) : ProjectionOutcome.Denied;
                context.Response.Headers.CacheControl = "private, no-store";
                if (!allowed || projection == ProjectionOutcome.Denied) { await Error(403, "finance-access-denied").ExecuteAsync(context); return; }
                if (projection == ProjectionOutcome.Stale) { await Error(409, "settlement-projection-changed").ExecuteAsync(context); return; }
                await using var bytes = new MemoryStream();
                var prepared = new DefaultHttpContext { RequestServices = context.RequestServices, RequestAborted = ct };
                prepared.Request.Method = context.Request.Method; prepared.Response.Body = bytes;
                await result.ExecuteAsync(prepared).WaitAsync(ct);
                if (requireRead)
                    foreach (var id in requests)
                        if (!(await access.CheckAsync(actor, id, SettlementActions.Read, null, ct)).Allowed) allowed = false;
                projection = allowed ? await validate(ct) : ProjectionOutcome.Denied;
                allowed &= projection != ProjectionOutcome.Denied;
                identity = await sessions.ValidateSessionAsync(true, ct);
                allowed &= identity.Status is null && identity.ActorId == actor;
                context.Response.Headers.CacheControl = "private, no-store";
                if (!allowed) { await Error(403, "finance-access-denied").ExecuteAsync(context).WaitAsync(ct); return; }
                if (projection == ProjectionOutcome.Stale) { await Error(409, "settlement-projection-changed").ExecuteAsync(context).WaitAsync(ct); return; }
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
                else { context.Response.Clear(); await Error(503, "settlement-response-unavailable").ExecuteAsync(context); }
            }
        }
    }
}

public sealed record SettlementReceiptHttp(Guid CommandId, string Operation, Guid? OperationEntityId, SettlementCommandResult Result);
