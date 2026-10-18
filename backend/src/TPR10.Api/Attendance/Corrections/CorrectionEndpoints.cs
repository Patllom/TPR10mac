using System.Globalization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Attendance.History;
using TPR10.Api.Attendance.Recording;
using TPR10.Api.Scopes;

namespace TPR10.Api.Attendance.Corrections;

public sealed record CreateCorrectionRequest(Guid CommandId, SaveCorrection Input);
public sealed record ReviseCorrectionRequest(SaveCorrection Input, CorrectionCommand Command);
// A command acknowledgment is not a grant to read the request after its workflow stage changes.
public sealed record CorrectionAcknowledgment(Guid Id, Guid CommandId);
public sealed record CorrectionBodyBoundary;

public static class CorrectionEndpoints
{
    public const int BodyLimit = 64 * 1024;
    public static void ApplyBodyLimit(HttpContext http)
    {
        if (http.GetEndpoint()?.Metadata.GetMetadata<CorrectionBodyBoundary>() is not null
            && http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            feature.MaxRequestBodySize = BodyLimit;
    }
    public static IEndpointRouteBuilder MapAttendanceCorrections(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/attendance/corrections").RequireAuthorization();
        group.MapGet("", (HttpContext http) => List(http, false)).Produces<CorrectionPage>()
            .WithMetadata(Metadata("", false));
        group.MapGet("/approvals", (HttpContext http) => List(http, true)).Produces<CorrectionPage>()
            .WithMetadata(Metadata("", true));
        group.MapGet("/sites", SiteChoices).Produces<CorrectionSitePage>()
            .WithMetadata(Metadata("attendance:record", false));
        group.MapGet("/{id:guid}", (Guid id, HttpContext http) => http.Request.Query.Count != 0 ? Problem(400)
            : new AttendanceJsonResult((s, ct) => s.CorrectionAsync(id, ct))).Produces<CorrectionView>()
            .WithMetadata(Metadata("", false));
        group.MapPost("", (Delegate)((HttpContext http) => Mutation(http, null, "create")))
            .Produces<CorrectionAcknowledgment>(201).Produces<CorrectionAcknowledgment>()
            .WithMetadata(Metadata("attendance:record", false), new CorrectionBodyBoundary());
        foreach (var action in new[] { "save", "submit", "supervisor-approve", "hr-approve", "return", "reroute" })
        {
            var captured = action;
            var capability = action switch { "save" or "submit" => "attendance:record", "supervisor-approve" => "attendance:approve-supervisor", "hr-approve" => "attendance:approve-hr", "reroute" => "attendance:directory-manage", _ => "" };
            group.MapPost("/{id:guid}/" + (action == "save" ? "revisions" : action), (Guid id, HttpContext http) => Mutation(http, id, captured))
                .Produces<CorrectionAcknowledgment>().WithMetadata(Metadata(capability, action is not ("save" or "submit")), new CorrectionBodyBoundary());
        }
        return endpoints;
    }
    private static DirectoryEndpointMetadata Metadata(string capability, bool mfa) => new("attendance-corrections", capability, mfa);
    private static IResult SiteChoices(HttpContext http)
    {
        var q = http.Request.Query;
        if (q.Any(x => x.Value.Count != 1 || x.Key is not ("at" or "page" or "pageSize"))
            || !DateTimeOffset.TryParseExact(q["at"], ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) return Problem(400);
        var page = 1; var size = 100;
        if (q.ContainsKey("page") && !int.TryParse(q["page"], NumberStyles.None, CultureInfo.InvariantCulture, out page)
            || q.ContainsKey("pageSize") && !int.TryParse(q["pageSize"], NumberStyles.None, CultureInfo.InvariantCulture, out size)
            || page < 1 || size is < 1 or > 100 || page > int.MaxValue / size) return Problem(400);
        return new AttendanceJsonResult((s, ct) => s.CorrectionSitesAsync(at, page, size, ct));
    }
    private static IResult List(HttpContext http, bool queue)
    {
        var query = http.Request.Query;
        if (query.Any(x => x.Value.Count != 1 || x.Key is not ("page" or "pageSize") && !(queue && x.Key == "stage"))) return Problem(400);
        var audience = queue ? query["stage"].ToString() : "mine";
        if (queue && audience is not ("supervisor" or "hr" or "routing")) return Problem(400);
        var page = 1; var size = 20;
        if (query.ContainsKey("page") && !int.TryParse(query["page"], NumberStyles.None, CultureInfo.InvariantCulture, out page)
            || query.ContainsKey("pageSize") && !int.TryParse(query["pageSize"], NumberStyles.None, CultureInfo.InvariantCulture, out size)
            || page < 1 || size is < 1 or > 100 || page > int.MaxValue / size) return Problem(400);
        return new AttendanceJsonResult((s, ct) => s.CorrectionsAsync(audience, page, size, ct));
    }
    private static async Task<IResult> Mutation(HttpContext http, Guid? id, string action)
    {
        if (http.Request.Query.Count != 0) return Problem(400);
        if (!MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var media) || media.MediaType != "application/json") return Problem(415);
        if (http.Request.ContentLength > BodyLimit) return Problem(413);
        try
        {
            var bytes = await CaptureHttpInput.ReadAsync(http.Request.Body, BodyLimit, http.RequestAborted);
            SaveCorrection? input = null; CorrectionCommand command;
            if (action == "create")
            {
                var request = CaptureHttpInput.Parse<CreateCorrectionRequest>(bytes); input = request.Input;
                command = new(request.CommandId, 0, 0, null);
            }
            else if (action == "save")
            {
                var request = CaptureHttpInput.Parse<ReviseCorrectionRequest>(bytes); input = request.Input; command = request.Command;
            }
            else command = CaptureHttpInput.Parse<CorrectionCommand>(bytes);
            if (command is null || command.CommandId == Guid.Empty
                || action is "create" or "save" && input?.Proposal is null) return Problem(400);
            return new AttendanceJsonResult((s, ct) => s.MutateCorrectionAsync(id, action, input, command, ct));
        }
        catch (CaptureInputException error) { return Problem(error.Status); }
        catch (BadHttpRequestException error) when (error.StatusCode == 413) { return Problem(413); }
    }
    public static IResult Problem(int status) => Results.Problem(statusCode: status, type: ScopeOperation.ProblemType(status), title: status switch
    {
        400 => "ข้อมูลคำร้องไม่ถูกต้องหรือพ้นกำหนดยื่น",
        401 => "กรุณาเข้าสู่ระบบ",
        403 => "ไม่มีสิทธิ์หรือจำเป็นต้องยืนยัน MFA",
        404 => "ไม่พบคำร้องที่เข้าถึงได้",
        409 => "ข้อมูลหรือเส้นทางเปลี่ยนแล้ว กรุณาโหลดใหม่ก่อนดำเนินการ",
        413 => "ข้อมูลคำร้องใหญ่เกิน 64 KiB",
        415 => "รองรับข้อมูล application/json เท่านั้น",
        429 => "ทำรายการบ่อยเกินไป กรุณารอแล้วตรวจสถานะ",
        _ => "ระบบไม่พร้อม กรุณาตรวจสถานะก่อนส่งซ้ำ"
    });
}
