using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Access;
using TPR10.Api.Attendance.Corrections;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Attendance.Recording;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Scopes;

namespace TPR10.Api.Attendance.History;

public sealed class AttendanceHistory(Tpr10DbContext db, AttendanceReadScope scope, IAttendanceAccess policy,
    ScopeAccess identity, RequestSession current, IDataProtectionProvider protection, IAuditEventWriter audit, TimeProvider clock)
{
    private readonly IDataProtector cursorProtector = protection.CreateProtector("TPR10.Attendance.HistoryCursor.v1");
    private static readonly JsonSerializerOptions CursorJson = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private sealed record Cursor(int Version, Guid Actor, Guid Session, AttendanceAudience Audience, DateOnly From, DateOnly To,
        int PageSize, DateOnly WorkDate, Guid Id);

    public Task<RecordingResult<CurrentAttendanceView>> CurrentAsync(CancellationToken ct) => RunAsync(async token =>
    {
        var own = await scope.VisibleDaysAsync(AttendanceAudience.Mine, token);
        var now = DirectoryQueries.Now(clock); var today = AttendancePairing.WorkDate(now);
        var open = await own.SingleOrDefaultAsync(d => d.EffectiveOutUtc == null, token);
        var completed = await own.AnyAsync(d => d.WorkDate == today && d.EffectiveOutUtc != null, token);
        var rows = await ProjectAsync(open is null ? [] : [open], token);
        return new CurrentAttendanceView(rows.SingleOrDefault(), completed, now);
    }, ct);

    public Task<RecordingResult<AttendancePage>> ListAsync(AttendanceAudience audience, AttendanceListQuery query, CancellationToken ct) => RunAsync(async token =>
    {
        var visible = await scope.VisibleDaysAsync(audience, token);
        if (query is null || query.From == default || query.To == default || query.From > query.To
            || query.To.DayNumber - query.From.DayNumber >= 31 || query.PageSize is < 1 or > 100) throw new AttendanceReadDeniedException(400);
        var days = visible.Where(d => d.WorkDate >= query.From && d.WorkDate <= query.To);
        if (query.Cursor is not null)
        {
            var cursor = ReadCursor(audience, query);
            days = days.Where(d => d.WorkDate < cursor.WorkDate || d.WorkDate == cursor.WorkDate && d.Id.CompareTo(cursor.Id) < 0);
        }
        var window = await days.OrderByDescending(d => d.WorkDate).ThenByDescending(d => d.Id).Take(query.PageSize + 1).ToArrayAsync(token);
        var page = window.Take(query.PageSize).ToArray();
        var rows = await ProjectAsync(page, token);
        string? next = null;
        if (window.Length > query.PageSize)
        {
            var last = page[^1];
            next = cursorProtector.Protect(JsonSerializer.Serialize(new Cursor(1, current.Entity!.UserId, current.Entity.Id,
                audience, query.From, query.To, query.PageSize, last.WorkDate, last.Id), CursorJson));
        }
        return new AttendancePage(rows, next);
    }, ct);

    public Task<RecordingResult<AttendanceDayView>> GetAsync(Guid dayId, CancellationToken ct) => RunAsync(async token =>
    {
        var visible = await scope.ReadableDaysAsync(token);
        var day = await visible.SingleOrDefaultAsync(d => d.Id == dayId, token) ?? throw new AttendanceReadDeniedException(404);
        return (await ProjectAsync([day], token))[0];
    }, ct);

    private Cursor ReadCursor(AttendanceAudience audience, AttendanceListQuery query)
    {
        if (query.Cursor!.Length is < 1 or > 4096) throw new AttendanceReadDeniedException(400);
        try
        {
            var value = JsonSerializer.Deserialize<Cursor>(cursorProtector.Unprotect(query.Cursor), CursorJson);
            if (value is null || value.Version != 1 || value.Actor != current.Entity!.UserId || value.Session != current.Entity.Id
                || value.Audience != audience || value.From != query.From || value.To != query.To || value.PageSize != query.PageSize
                || value.WorkDate < query.From || value.WorkDate > query.To || value.Id == Guid.Empty) throw new AttendanceReadDeniedException(400);
            return value;
        }
        catch (Exception error) when (error is CryptographicException or JsonException or FormatException) { throw new AttendanceReadDeniedException(400); }
    }

    private async Task<AttendanceDayView[]> ProjectAsync(AttendanceDay[] days, CancellationToken ct)
    {
        var ids = days.Select(d => d.Id).ToArray();
        var events = await db.Set<AttendanceEvent>().AsNoTracking().Where(e => ids.Contains(e.DayId)).ToArrayAsync(ct);
        var rows = new List<AttendanceDayView>(days.Length);
        foreach (var day in days)
        {
            var decision = await policy.ReadAsync(new(day.MembershipId, day.EmployeeId, day.WorkspaceId, day.DepartmentId, day.SnapshotAtUtc), ct);
            if (decision.Status is { } denied || !decision.CanRead) throw new AttendanceReadDeniedException(decision.Status ?? 404);
            var entered = events.SingleOrDefault(e => e.DayId == day.Id && e.Action == EvidenceAction.CheckIn);
            var exited = events.SingleOrDefault(e => e.DayId == day.Id && e.Action == EvidenceAction.CheckOut);
            var adjustments = await db.Set<AttendanceAdjustment>().AsNoTracking().Where(x => x.DayId == day.Id)
                .OrderBy(x => x.AfterVersion).ToArrayAsync(ct);
            var last = adjustments.LastOrDefault();
            if (entered is null && last is null) throw new AttendanceReadDeniedException(503);
            var revision = last is null ? null : await db.Set<CorrectionRevision>().AsNoTracking()
                .SingleAsync(x => x.RequestId == last.RequestId && x.Revision == last.Revision, ct);
            var originalIn = entered is null ? null : Event(entered, decision.CanReadPhoto);
            var originalOut = exited is null ? null : Event(exited, decision.CanReadPhoto);
            rows.Add(new(day.Id, day.WorkDate, day.Version, day.EmployeeId, originalIn, originalOut,
                decision.Basis, day.EffectiveInUtc, day.EffectiveOutUtc)
            {
                EffectiveInSite = revision is null ? originalIn?.Site : new(revision.InWorkspaceId, revision.InProjectId, revision.InSiteId),
                EffectiveOutSite = day.EffectiveOutUtc is null ? null : revision is null || day.Version > last!.AfterVersion
                    ? originalOut?.Site : new(revision.OutWorkspaceId!.Value, revision.OutProjectId, revision.OutSiteId),
                Adjustments = adjustments.Select(x => new AttendanceAdjustmentView(x.RequestId, x.Revision, x.AppliedAtUtc,
                    x.BeforeInUtc, x.BeforeOutUtc, x.AfterInUtc, x.AfterOutUtc)).ToArray()
            });
            await audit.WriteAsync(new SecurityAuditRequest(current.Entity!.UserId, null, day.WorkspaceId, null, null,
                "attendance.history.read", "attendance-day", day.Id, "success", new Dictionary<string, string> { ["source"] = decision.Basis.ToString() }), ct);
        }
        // Time-bound grants can expire while projection/auditing awaits, even under the identity lock.
        for (var index = 0; index < days.Length; index++)
        {
            var day = days[index];
            var latest = await policy.ReadAsync(new(day.MembershipId, day.EmployeeId, day.WorkspaceId, day.DepartmentId, day.SnapshotAtUtc), ct);
            if (latest.Status is not null || !latest.CanRead || latest.Basis != rows[index].ReadBasis
                || rows[index].In is { } entered && latest.CanReadPhoto != (entered.Photo is not null)
                || rows[index].Out is { } exited && latest.CanReadPhoto != (exited.Photo is not null))
                throw new AttendanceReadDeniedException(latest.Status ?? 404);
        }
        return rows.ToArray();
    }
    private static AttendanceEventView Event(AttendanceEvent value, bool photo) => new(value.Id, value.OccurredAtUtc,
        new(value.WorkspaceId, value.ProjectId, value.SiteId), new(value.Latitude, value.Longitude, value.AccuracyMeters, value.GpsSampledAtUtc),
        photo ? new(value.EvidenceId) : null);

    private async Task<RecordingResult<T>> RunAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await using var tx = await ScopeOperation.BeginAsync(db, deadline.Token);
            var result = await read(deadline.Token);
            var latest = await identity.ValidateSessionAsync(false, deadline.Token);
            if (latest.Status is { } denied) throw new AttendanceReadDeniedException(denied);
            await db.SaveChangesAsync(deadline.Token); await tx.CommitAsync(deadline.Token);
            return new(result, 200, null, null);
        }
        catch (AttendanceReadDeniedException error) { return Failure<T>(error.Status); }
        catch (Exception error) when (ScopeOperation.IsDatabaseFault(error)) { return Failure<T>(503); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Failure<T>(503); }
        finally { db.ChangeTracker.Clear(); }
    }
    private static RecordingResult<T> Failure<T>(int status) => new(default, status, ScopeOperation.ProblemType(status), null);
}
