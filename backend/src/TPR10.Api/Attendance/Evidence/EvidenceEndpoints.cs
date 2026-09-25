using TPR10.Api.Attendance.Directory;

namespace TPR10.Api.Attendance.Evidence;

public static class EvidenceEndpoints
{
    public static IEndpointRouteBuilder MapAttendanceEvidence(this IEndpointRouteBuilder endpoints)
    {
        Map("/api/v1/attendance/evidence/{id:guid}", "full", false);
        Map("/api/v1/attendance/evidence/{id:guid}/thumbnail", "thumbnail", false);
        Map("/api/v1/attendance/evidence/{id:guid}/download", "full", true);
        return endpoints;

        void Map(string route, string variant, bool download)
        {
            async Task<IResult> Read(Guid id, EvidenceReader reader, HttpContext http, CancellationToken ct)
            {
                var result = await reader.ReadAsync(id, variant, download, ct);
                return HttpMethods.IsHead(http.Request.Method) && result is IStatusCodeHttpResult { StatusCode: { } status }
                    ? Results.StatusCode(status) : result;
            }
            endpoints.MapGet(route, Read).RequireAuthorization().Produces(200, contentType: "image/jpeg").Produces(416)
                .WithMetadata(new DirectoryEndpointMetadata("attendance-evidence", "", false));
            endpoints.MapMethods(route, ["HEAD"], Read).RequireAuthorization().ExcludeFromDescription();
        }
    }
}
