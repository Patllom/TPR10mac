using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Access;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Attendance.Recording;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.Identity.Authorization;
using TPR10.Api.Scopes;

namespace TPR10.Api.Attendance.Corrections;

public sealed class CorrectionQueries(Tpr10DbContext db, ScopeAccess identity, IAttendanceAccess policy,
    IAttendanceRouteResolver resolver, PermissionMutationGuard guard, IAuditEventWriter audit, TimeProvider clock)
{
    public Task<CorrectionResult<CorrectionPage>> ListAsync(string audience, int pageNumber, int pageSize, CancellationToken ct) =>
        RunAsync(token => PageAsync(audience, pageNumber, pageSize, token), audience, pageNumber, pageSize, ct);

    public Task<CorrectionResult<CorrectionView>> GetAsync(Guid id, CancellationToken ct) =>
        RunAsync(token => DetailAsync(id, true, token), null, 0, 0, ct);

    private async Task<CorrectionPage> PageAsync(string audience, int pageNumber, int pageSize, CancellationToken ct)
    {
        var session = await identity.ValidateSessionAsync(audience is "supervisor" or "hr", ct);
        if (session.Status is { } denied) throw new AttendanceReadDeniedException(denied);
        if (audience is not ("mine" or "supervisor" or "hr") || pageNumber < 1 || pageSize is < 1 or > 100
            || (long)(pageNumber - 1) * pageSize > int.MaxValue) throw new AttendanceReadDeniedException(400);
        IQueryable<CorrectionRequest> query;
        if (audience == "mine") query = ValidRequests().Where(x => x.EmployeeId == session.ActorId);
        else
        {
            var capability = audience == "supervisor" ? "attendance:approve-supervisor" : "attendance:approve-hr";
            if (!await AttendanceAccess.EligibleUsers(db, capability).ContainsAsync(session.ActorId, ct)) throw new AttendanceReadDeniedException(403);
            query = await WorkflowAsync(session.ActorId, audience, ct);
        }
        // All authorization, route, and state predicates precede both counting and paging.
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.OriginalWorkDate).ThenByDescending(x => x.Id)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).ToArrayAsync(ct);
        var views = new List<CorrectionView>(rows.Length);
        foreach (var row in rows) views.Add(await ViewAsync(row, session.ActorId, ct));
        return new(views.ToArray(), total, pageNumber, pageSize);
    }

    private async Task<CorrectionView> DetailAsync(Guid id, bool history, CancellationToken ct)
    {
        var session = await identity.ValidateSessionAsync(false, ct);
        if (session.Status is { } denied) throw new AttendanceReadDeniedException(denied);
        var row = await ValidRequests().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new AttendanceReadDeniedException(404);
        var view = await ViewAsync(row, session.ActorId, ct);
        view = view with { Comparison = await CompareAsync(row, view.BaselineDayVersion, ct) };
        if (!history) return view;
        var revisions = await db.Set<CorrectionRevision>().AsNoTracking().Where(x => x.RequestId == id).OrderBy(x => x.Revision).ToArrayAsync(ct);
        var routes = await db.Set<CorrectionRouteSnapshot>().AsNoTracking().Where(x => x.RequestId == id).OrderBy(x => x.Revision)
            .Select(x => new CorrectionRouteView(x.Revision, x.SupervisorId, x.HrCandidateIds, x.CreatedAtUtc)).ToArrayAsync(ct);
        var approvals = await db.Set<CorrectionApprovalAction>().AsNoTracking().Where(x => x.RequestId == id).OrderBy(x => x.Revision).ThenBy(x => x.OccurredAtUtc).ThenByDescending(x => x.Step)
            .Select(x => new CorrectionApprovalView(x.Revision, x.Step, x.ActorId, x.Outcome, x.Reason, x.OccurredAtUtc)).ToArrayAsync(ct);
        var commands = await db.Set<CorrectionCommandReceipt>().AsNoTracking().Where(x => x.RequestId == id && x.Action == "reroute")
            .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.CommandId).ToArrayAsync(ct);
        var reroutes = commands.Select(x =>
        {
            using var json = JsonDocument.Parse(x.ResultJson);
            return new CorrectionRerouteView(json.RootElement.GetProperty("CurrentRevision").GetInt32(), x.ActorId,
                json.RootElement.GetProperty("CommandReason").GetString()!, x.CreatedAtUtc);
        }).ToArray();
        return view with
        {
            History = new(revisions.Select(x => new CorrectionRevisionView(x.Revision, x.BaselineDayVersion,
            CorrectionApproval.Proposal(x), x.CreatedAtUtc)).ToArray(), routes, approvals, reroutes)
        };
    }

    private async Task<CorrectionComparison> CompareAsync(CorrectionRequest request, long? baseline, CancellationToken ct)
    {
        // Called only after request authorization, never as a general attendance read endpoint.
        var reference = request.DayId ?? await db.Set<AttendanceAdjustment>().AsNoTracking()
            .Where(x => x.RequestId == request.Id && x.Revision == request.CurrentRevision).Select(x => (Guid?)x.DayId).SingleOrDefaultAsync(ct);
        if (reference is not { } id) return new(null, baseline is null, null, null);
        var day = await db.Set<AttendanceDay>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.EmployeeId == request.EmployeeId, ct)
            ?? throw new AttendanceReadDeniedException(404);
        var events = await db.Set<AttendanceEvent>().AsNoTracking().Where(x => x.DayId == id).ToArrayAsync(ct);
        var entered = events.SingleOrDefault(x => x.Action == EvidenceAction.CheckIn);
        var exited = events.SingleOrDefault(x => x.Action == EvidenceAction.CheckOut);
        var last = await db.Set<AttendanceAdjustment>().AsNoTracking().Where(x => x.DayId == id).OrderByDescending(x => x.AfterVersion).FirstOrDefaultAsync(ct);
        var revision = last is null ? null : await db.Set<CorrectionRevision>().AsNoTracking()
            .SingleAsync(x => x.RequestId == last.RequestId && x.Revision == last.Revision, ct);
        CorrectionSite? input = revision is null ? entered is null ? null : new(entered.WorkspaceId, entered.ProjectId, entered.SiteId)
            : new(revision.InWorkspaceId, revision.InProjectId, revision.InSiteId);
        CorrectionSite? output = day.EffectiveOutUtc is null ? null : revision is null || day.Version > last!.AfterVersion
            ? exited is null ? null : new(exited.WorkspaceId, exited.ProjectId, exited.SiteId)
            : new(revision.OutWorkspaceId!.Value, revision.OutProjectId!.Value, revision.OutSiteId!.Value);
        return new(id, baseline == day.Version, entered is null && exited is null ? null : new(entered?.OccurredAtUtc, exited?.OccurredAtUtc),
            new(day.Version, day.WorkDate, day.EffectiveInUtc, day.EffectiveOutUtc, input, output));
    }

    private async Task<CorrectionView> ViewAsync(CorrectionRequest row, Guid actor, CancellationToken ct)
    {
        var subject = new EmploymentSnapshot(row.MembershipId, row.EmployeeId, row.WorkspaceId, row.DepartmentId, row.SnapshotAtUtc);
        var read = await policy.ReadAsync(subject, ct);
        if (read.Status is 401 or 403) throw new AttendanceReadDeniedException(read.Status.Value);
        var own = actor == row.EmployeeId;
        var submitted = await db.Set<CorrectionRouteSnapshot>().AnyAsync(x => x.RequestId == row.Id, ct);
        var head = !own && await (await WorkflowAsync(actor, "supervisor", ct)).AnyAsync(x => x.Id == row.Id, ct);
        var hr = !own && await (await WorkflowAsync(actor, "hr", ct)).AnyAsync(x => x.Id == row.Id, ct);
        if (!own && !(submitted && read.CanRead) && !head && !hr) throw new AttendanceReadDeniedException(404);
        var basis = read.CanRead ? read.Basis.ToString() : head ? "WorkflowSupervisor" : "WorkflowHr";
        var state = row.State is CorrectionState.Draft or CorrectionState.Returned && clock.GetUtcNow() >= row.DeadlineUtc
            ? CorrectionState.Expired : row.State;
        var actions = new List<string>();
        if (own && state is CorrectionState.Draft or CorrectionState.Returned && (await policy.DescribeAsync(ct)).CanRecord)
            actions.AddRange(["save", "submit"]);
        if (head) actions.AddRange(["supervisor-approve", "return"]);
        if (hr) actions.AddRange(["hr-approve", "return"]);
        if (row.State is CorrectionState.PendingSupervisor or CorrectionState.PendingHr
            && await new CorrectionRouting(db, guard, resolver, clock).CanRerouteAsync(actor, row, ct)) actions.Add("reroute");
        var revision = await db.Set<CorrectionRevision>().AsNoTracking().SingleAsync(x => x.RequestId == row.Id && x.Revision == row.CurrentRevision, ct);
        return new(row.Id, row.EmployeeId, row.OriginalWorkDate, row.Version, state, row.DeadlineUtc,
            row.CurrentRevision, CorrectionApproval.Proposal(revision), revision.BaselineDayVersion, actions.ToArray(), basis);
    }

    private IQueryable<CorrectionRequest> ValidRequests()
    {
        var now = DirectoryQueries.Now(clock);
        return db.Set<CorrectionRequest>().AsNoTracking().Where(r => r.SnapshotAtUtc <= now && db.Set<EmployeeMembership>().Any(m =>
            m.Id == r.MembershipId && m.UserId == r.EmployeeId && m.WorkspaceId == r.WorkspaceId && m.DepartmentId == r.DepartmentId
            && m.ValidFromUtc <= r.SnapshotAtUtc && (m.ValidToUtc == null || r.SnapshotAtUtc < m.ValidToUtc)));
    }

    private async Task<IQueryable<CorrectionRequest>> WorkflowAsync(Guid actor, string audience, CancellationToken ct)
    {
        var now = DirectoryQueries.Now(clock);
        var lines = DirectoryQueries.Reporting(db, now);
        var graph = await lines.ToDictionaryAsync(x => x.EmployeeUserId, x => x.SupervisorUserId, ct);
        var cycles = graph.Where(x => DirectoryRules.CreatesCycle(x.Key, x.Value, graph)).Select(x => x.Key).ToArray();
        var employment = DirectoryQueries.Employment(db, now);
        var hr = DirectoryQueries.Hr(db, now);
        var supervisors = AttendanceAccess.EligibleUsers(db, "attendance:approve-supervisor");
        var reviewers = AttendanceAccess.EligibleUsers(db, "attendance:approve-hr");
        var supervisor = audience == "supervisor";
        return ValidRequests().Where(r => r.EmployeeId != actor && !cycles.Contains(r.EmployeeId)
            && r.State == (supervisor ? CorrectionState.PendingSupervisor : CorrectionState.PendingHr)
            && db.Set<CorrectionRouteSnapshot>().Any(route => route.RequestId == r.Id && route.Revision == r.CurrentRevision
                && employment.Any(m => m.UserId == r.EmployeeId && m.WorkspaceId == r.WorkspaceId && m.DepartmentId == r.DepartmentId
                    && lines.Any(line => line.EmployeeMembershipId == m.Id && line.SupervisorUserId == route.SupervisorId))
                && route.SupervisorId != r.EmployeeId && supervisors.Contains(route.SupervisorId)
                && hr.Any(h => h.WorkspaceId == r.WorkspaceId && h.DepartmentId == r.DepartmentId
                    && h.UserId != r.EmployeeId && h.UserId != route.SupervisorId && reviewers.Contains(h.UserId))
                && (supervisor ? route.SupervisorId == actor : route.SupervisorId != actor && route.HrCandidateIds.Contains(actor)
                    && reviewers.Contains(actor) && hr.Any(h => h.UserId == actor && h.WorkspaceId == r.WorkspaceId && h.DepartmentId == r.DepartmentId)
                    && db.Set<CorrectionApprovalAction>().Any(a => a.RequestId == r.Id && a.Revision == r.CurrentRevision
                        && a.Step == "Supervisor" && a.Outcome == "Approved" && a.ActorId == route.SupervisorId))));
    }

    internal async Task<int?> RevalidateAsync(object value, string? audience, int page, int size, CancellationToken ct)
    {
        try
        {
            object fresh = value switch
            {
                CorrectionPage => await PageAsync(audience!, page, size, ct),
                CorrectionView v => await DetailAsync(v.Id, false, ct),
                _ => throw new AttendanceReadDeniedException(404)
            };
            var original = value is CorrectionView detail ? detail with { History = null } : value;
            return JsonSerializer.Serialize(original) == JsonSerializer.Serialize(fresh) ? null : 409;
        }
        catch (AttendanceReadDeniedException error) { return error.Status; }
    }

    private async Task<CorrectionResult<T>> RunAsync<T>(Func<CancellationToken, Task<T>> read, string? audience, int page, int size, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await using var tx = await ScopeOperation.BeginAsync(db, deadline.Token);
            var value = await read(deadline.Token);
            var actor = await identity.ValidateSessionAsync(audience is "supervisor" or "hr", deadline.Token);
            if (actor.Status is { } denied) throw new AttendanceReadDeniedException(denied);
            var views = value switch { CorrectionPage p => p.Items, CorrectionView v => [v], _ => Array.Empty<CorrectionView>() };
            foreach (var v in views)
                await audit.WriteAsync(new SecurityAuditRequest(actor.ActorId, null, null, null, null, "attendance.correction.read",
                    "correction-request", v.Id, "success", new Dictionary<string, string> { ["source"] = v.ReadBasis }), deadline.Token);
            if (await RevalidateAsync(value!, audience, page, size, deadline.Token) is { } changed) throw new AttendanceReadDeniedException(changed);
            await db.SaveChangesAsync(deadline.Token); await tx.CommitAsync(deadline.Token);
            return new(value, 200, null);
        }
        catch (AttendanceReadDeniedException error) { return new(default, error.Status, ScopeOperation.ProblemType(error.Status)); }
        catch (Exception error) when (ScopeOperation.IsDatabaseFault(error) || error is JsonException or KeyNotFoundException)
        { return new(default, 503, ScopeOperation.ProblemType(503)); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(default, 503, ScopeOperation.ProblemType(503)); }
        finally { db.ChangeTracker.Clear(); }
    }
}
