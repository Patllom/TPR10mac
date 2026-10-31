using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Requests;
using TPR10.Api.Identity.Sessions;

namespace TPR10.Api.FieldAdvances.Payments;

// Internal server history, never a grant or a projection to an API client.
public sealed class FamilyParticipation(Tpr10DbContext db, RequestSession session, FinanceAccess access,
    FinancePersonResolver people, TimeProvider clock)
{
    public Task<RequestParticipationHistory> ReadAsync(Guid familyId, CancellationToken ct)
        => ReadHistoryAsync(db, familyId, ct);

    public async Task RecordAsync(Guid familyId, Guid requestId, Guid personId, string role, CancellationToken ct)
    {
        var request = await LockRequestAsync(db, requestId, ct);
        var actor = session.Entity?.UserId;
        var capability = RequestSeparation.CapabilityForRole(role);
        if (request is null || request.FamilyId != familyId || actor is null || capability is null
            || await people.ResolveAsync(actor.Value, request.WorkspaceId, ct) != personId
            || !(await access.CheckAsync(actor.Value, new(request.WorkspaceId, request.ProjectId), capability, ct)).Allowed)
            throw new InvalidOperationException("ไม่มีสิทธิ์บันทึกประวัติหน้าที่");
        await RecordAuthorizedAsync(db, request, personId, role, actor.Value, clock.GetUtcNow(), ct);
    }

    // The request review service has already validated the current session and exact assignment.
    // The public path above obtains CreatedBy from that session, never from another account.
    internal static async Task RecordAuthorizedAsync(Tpr10DbContext db, AdvanceRequest request, Guid person,
        string role, Guid actor, DateTimeOffset at, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null || actor == Guid.Empty)
            throw new InvalidOperationException("ต้องบันทึกหน้าที่ในธุรกรรม");
        var locked = await LockRequestAsync(db, request.Id, ct);
        var capability = RequestSeparation.CapabilityForRole(role);
        if (locked is null || locked.FamilyId != request.FamilyId || capability is null
            || !RequestSeparation.Check(person, capability, await ReadHistoryAsync(db, request.FamilyId, ct), []).Allowed)
            throw new InvalidOperationException("หน้าที่ขัดกันในชุดคำขอ");
        if (await db.Set<AdvancePaymentParticipation>().AnyAsync(x => x.FamilyId == request.FamilyId
            && x.PersonId == person && x.Role == role, ct)) return;
        db.Add(new AdvancePaymentParticipation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = request.WorkspaceId,
            ProjectId = request.ProjectId,
            FamilyId = request.FamilyId,
            PersonId = person,
            Role = role,
            CreatedBy = actor,
            CreatedAtUtc = at
        });
        var family = await db.Set<AdvanceFamily>().SingleAsync(x => x.Id == request.FamilyId, ct);
        family.Version = checked(family.Version + 1);
        await db.SaveChangesAsync(ct);
    }

    // Every writer (including 7B) uses the same lock order. Ownership stays with the caller.
    internal static async Task<AdvanceRequest?> LockRequestAsync(Tpr10DbContext db, Guid requestId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("ต้องล็อกภายในธุรกรรม");
        if (requestId == Guid.Empty) return null;
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct);
        var identity = await db.Set<AdvanceRequest>().AsNoTracking().Where(x => x.Id == requestId)
            .Select(x => new { x.FamilyId, x.WorkspaceId, x.ProjectId, x.OwnerPersonId }).SingleOrDefaultAsync(ct);
        if (identity is null) return null;
        var family = await db.Set<AdvanceFamily>().FromSqlInterpolated(
            $"SELECT * FROM field_advance_families WHERE id={identity.FamilyId} FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(ct);
        if (family is null || family.WorkspaceId != identity.WorkspaceId || family.ProjectId != identity.ProjectId
            || family.OwnerPersonId != identity.OwnerPersonId) return null;
        var request = await db.Set<AdvanceRequest>().FromSqlInterpolated(
            $"SELECT * FROM field_advance_requests WHERE id={requestId} FOR UPDATE").SingleOrDefaultAsync(ct);
        return request is not null && request.FamilyId == family.Id && request.WorkspaceId == family.WorkspaceId
            && request.ProjectId == family.ProjectId && request.OwnerPersonId == family.OwnerPersonId ? request : null;
    }

    internal static async Task<RequestParticipationHistory> ReadHistoryAsync(Tpr10DbContext db, Guid familyId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("ต้องอ่านประวัติในธุรกรรม");
        RequestParticipationHistory invalid = new([], [], [], [], []);
        var family = await db.Set<AdvanceFamily>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == familyId, ct);
        if (family is null || familyId == Guid.Empty) return invalid;
        // SourceRequestId deliberately does not participate: copies have independent families.
        var requests = await db.Set<AdvanceRequest>().AsNoTracking().Where(x => x.FamilyId == familyId).ToArrayAsync(ct);
        if (requests.Length == 0 || requests.Any(x => x.WorkspaceId != family.WorkspaceId
            || x.ProjectId != family.ProjectId || x.OwnerPersonId != family.OwnerPersonId)) return invalid;
        var ids = requests.Select(x => x.Id).ToArray();
        var old = await db.Set<AdvanceRequestParticipant>().AsNoTracking().Where(x => ids.Contains(x.RequestId))
            .Select(x => new { x.PersonId, x.Role }).ToArrayAsync(ct);
        var payments = await db.Set<AdvancePaymentParticipation>().AsNoTracking().Where(x => x.FamilyId == familyId)
            .Select(x => new { x.PersonId, x.Role }).ToArrayAsync(ct);
        var rows = old.Concat(payments).ToArray();
        if (rows.Any(x => x.PersonId == Guid.Empty || x.Role is not ("Owner" or "Delegate" or "Approver" or "Checker" or "Payer"))) return invalid;
        var decisions = await db.Set<AdvanceApprovalStep>().AsNoTracking()
            .Where(x => ids.Contains(x.RequestId) && x.Outcome != null)
            .Select(x => x.DecidedByPersonId ?? Guid.Empty).ToArrayAsync(ct);
        Guid[] Role(string role) => rows.Where(x => x.Role == role).Select(x => x.PersonId).Distinct().ToArray();
        return new(Role("Owner").Append(family.OwnerPersonId).Concat(requests.Select(x => x.OwnerPersonId)).Distinct().ToArray(),
            Role("Delegate").Concat(requests.Where(x => x.DelegatePersonId != null).Select(x => x.DelegatePersonId!.Value)).Distinct().ToArray(),
            Role("Approver").Concat(decisions).Distinct().ToArray(), Role("Checker"), Role("Payer"));
    }
}
