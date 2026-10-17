using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Attendance.History;

namespace TPR10.Api.Attendance.Recording;

public sealed record CaptureUploadBoundary;

public static class RecordingEndpoints
{
    public static IEndpointRouteBuilder MapAttendanceRecording(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/attendance").RequireAuthorization()
            .WithMetadata(new DirectoryEndpointMetadata("attendance-recording", "attendance:record", false));
        group.MapPost("/capture-challenges", IssueAsync).Produces<CaptureChallengeView>(200).Produces<CaptureChallengeView>(201);
        group.MapPost("/check-ins", (HttpContext http, AttendanceRecorder recorder, CancellationToken ct) => UploadAsync(http, recorder, EvidenceAction.CheckIn, ct))
            .WithMetadata(new CaptureUploadBoundary()).Produces<CaptureReceipt>(200).Produces<CaptureReceipt>(201);
        group.MapPost("/check-outs", (HttpContext http, AttendanceRecorder recorder, CancellationToken ct) => UploadAsync(http, recorder, EvidenceAction.CheckOut, ct))
            .WithMetadata(new CaptureUploadBoundary()).Produces<CaptureReceipt>(200).Produces<CaptureReceipt>(201);
        group.MapGet("/operations/{idempotencyKey:guid}", (Guid idempotencyKey, HttpContext http) =>
        {
            var query = http.Request.Query;
            if (query.Count != 1 || !query.TryGetValue("action", out var value) || value.Count != 1 || value[0] is not ("CheckIn" or "CheckOut")) return Problem(400);
            var action = value[0] == "CheckIn" ? EvidenceAction.CheckIn : EvidenceAction.CheckOut;
            return new AttendanceJsonResult((services, ct) => services.OperationAsync(idempotencyKey, action, ct));
        }).Produces<CaptureOperationView>();
        return endpoints;
    }

    public static void ApplyCaptureBodyLimit(HttpContext http)
    {
        if (http.GetEndpoint()?.Metadata.GetMetadata<CaptureUploadBoundary>() is not null
            && http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            feature.MaxRequestBodySize = CaptureHttpInput.RequestLimit;
    }
    private static async Task<IResult> IssueAsync(HttpContext http, CaptureChallengeService service, CancellationToken ct)
    {
        try
        {
            if (!MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var type) || type.MediaType != "application/json") return Problem(415);
            var input = CaptureHttpInput.Parse<CaptureChallengeRequest>(await CaptureHttpInput.ReadAsync(http.Request.Body, CaptureHttpInput.MetadataLimit, ct));
            return Result(await service.IssueAsync(input, ct), http);
        }
        catch (CaptureInputException error) { return Problem(error.Status); }
        catch (BadHttpRequestException error) { return Problem(error.StatusCode); }
    }
    private static async Task<IResult> UploadAsync(HttpContext http, AttendanceRecorder service, EvidenceAction action, CancellationToken ct)
    {
        try
        {
            var input = await CaptureHttpInput.MultipartAsync(http.Request, ct);
            using var image = new MemoryStream(input.Photo, writable: false);
            var result = await service.RecordAsync(action, input.Metadata, image, ct);
            if (result.Value is null) return Result(result, http);
            // Re-read the committed receipt under the delivery lock, never emit a stale receipt.
            return new AttendanceJsonResult((services, token) => services.ReceiptAsync(input.Metadata.IdempotencyKey, action, result.Status, token));
        }
        catch (CaptureInputException error) { return Problem(error.Status); }
        catch (BadHttpRequestException error) { return Problem(error.StatusCode); }
        catch (IOException) { return Problem(400); }
    }
    internal static IResult Result<T>(RecordingResult<T> result, HttpContext http)
    {
        if (result.RetryAfterSeconds is { } seconds) http.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return result.Value is null ? Problem(result.Status, result.Code) : Results.Json(result.Value, statusCode: result.Status);
    }
    internal static IResult Problem(int status, string? code = null) => Results.Problem(statusCode: status,
        type: code is null ? null : code.StartsWith("urn:", StringComparison.Ordinal) ? code : "urn:tpr10:attendance:" + code,
        title: status switch
        {
            400 => "ข้อมูลลงเวลาไม่ถูกต้อง กรุณาตรวจข้อมูลและถ่ายใหม่",
            401 => "กรุณาเข้าสู่ระบบใหม่",
            403 => "ไม่มีสิทธิ์หรือจำเป็นต้องยืนยันตัวตนเพิ่มเติม",
            404 => "ไม่พบข้อมูลที่มีสิทธิ์เข้าถึง",
            409 => "ข้อมูลเปลี่ยนแปลงหรือกำลังประมวลผล กรุณาตรวจสถานะ",
            413 => "ภาพหรือข้อมูลมีขนาดใหญ่เกินกำหนด",
            415 => "ภาพหรือชนิดข้อมูลไม่รองรับ",
            429 => "คำขอมากเกินไป กรุณาลองใหม่ภายหลัง",
            _ => "บริการลงเวลาไม่พร้อมใช้งาน กรุณาตรวจสถานะก่อนลองใหม่"
        });
}
