using System.Globalization;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Attendance.Recording;

namespace TPR10.Api.Attendance.History;

public static class HistoryEndpoints
{
    public static IEndpointRouteBuilder MapAttendanceHistory(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/attendance").RequireAuthorization();
        group.MapGet("/current", (HttpContext http) => http.Request.Query.Count != 0 ? RecordingEndpoints.Problem(400)
            : new AttendanceJsonResult((s, ct) => s.CurrentAsync(ct))).Produces<CurrentAttendanceView>()
            .WithMetadata(new DirectoryEndpointMetadata("attendance-history", "", false));
        group.MapGet("/days/{dayId:guid}", (Guid dayId, HttpContext http) => http.Request.Query.Count != 0 ? RecordingEndpoints.Problem(400)
            : new AttendanceJsonResult((s, ct) => s.DayAsync(dayId, ct))).Produces<AttendanceDayView>()
            .WithMetadata(new DirectoryEndpointMetadata("attendance-history", "", false));
        Map("/mine", AttendanceAudience.Mine, ""); Map("/team", AttendanceAudience.Team, "attendance:team-read"); Map("/hr", AttendanceAudience.Hr, "attendance:hr-read");
        return endpoints;
        void Map(string path, AttendanceAudience audience, string capability) => group.MapGet(path, (HttpContext http) =>
        {
            var values = http.Request.Query;
            if (values.Any(x => x.Key is not ("from" or "to" or "pageSize" or "cursor") || x.Value.Count != 1)
                || !DateOnly.TryParseExact(values["from"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
                || !DateOnly.TryParseExact(values["to"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to)) return RecordingEndpoints.Problem(400);
            var size = 20;
            if (values.ContainsKey("pageSize") && !int.TryParse(values["pageSize"], NumberStyles.None, CultureInfo.InvariantCulture, out size)) return RecordingEndpoints.Problem(400);
            var query = new AttendanceListQuery(from, to, size, values.ContainsKey("cursor") ? values["cursor"].ToString() : null);
            return new AttendanceJsonResult((s, ct) => s.ListAsync(audience, query, ct));
        }).Produces<AttendancePage>().WithMetadata(new DirectoryEndpointMetadata("attendance-history", capability, audience != AttendanceAudience.Mine));
    }
}
