using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;

namespace TPR10.Api.FieldAdvances.Requests;

// Reads current directory/grant evidence only; never treats a candidate as the caller.
internal sealed class ApprovalCandidateReader(Tpr10DbContext db, FinancePersonResolver people, TimeProvider clock)
{
    internal async Task<RoutePerson> PrimaryAsync(FinanceScope scope, string level, CancellationToken ct)
    {
        // Determine incumbency before amount/separation eligibility. An ineligible primary
        // must not silently disappear and let another person win an ambiguous role.
        var grants = await FinanceGrantQueries.Current(db, scope, "request.approve", clock.GetUtcNow())
            .Where(x => x.PrimaryApprovalLevels.Contains(level) && x.ApprovalLevels.Contains(level))
            .Select(x => new { x.RecipientId, x.RecipientPersonId }).Distinct().ToArrayAsync(ct);
        var persons = new HashSet<Guid>();
        foreach (var grant in grants)
        {
            var person = await people.ResolveAsync(grant.RecipientId, scope.WorkspaceId, ct);
            if (person is null || person != grant.RecipientPersonId) return new(null, "route-identity-unavailable");
            persons.Add(person.Value);
        }
        return Unique(persons);
    }

    internal async Task<RoutePerson> SupervisorAsync(Guid person, Guid workspace, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var accounts = await db.Set<FinanceAccountLink>().AsNoTracking()
            .Where(x => x.WorkspaceId == workspace && x.PersonId == person && x.CreatedAtUtc <= now && x.EndedAtUtc == null)
            .Select(x => x.UserId).Distinct().ToArrayAsync(ct);
        var activeAccounts = new List<Guid>();
        foreach (var account in accounts)
            if (await people.ResolveAsync(account, workspace, ct) == person) activeAccounts.Add(account);
        if (activeAccounts.Count == 0) return new(null, "route-identity-unavailable");

        // Keep unresolved supervisor identities visible instead of filtering them out via
        // DirectoryQueries.Reporting and accidentally choosing another alias's supervisor.
        var supervisors = await (from line in db.Set<ReportingLine>().AsNoTracking()
                                 join member in DirectoryQueries.Employment(db, now) on line.EmployeeMembershipId equals member.Id
                                 where member.WorkspaceId == workspace && member.UserId == line.EmployeeUserId
                                     && activeAccounts.Contains(line.EmployeeUserId)
                                     && line.ValidFromUtc <= now && (line.ValidToUtc == null || now < line.ValidToUtc)
                                 select line.SupervisorUserId).Distinct().ToArrayAsync(ct);
        var persons = new HashSet<Guid>();
        foreach (var account in supervisors)
        {
            var supervisor = await people.ResolveAsync(account, workspace, ct);
            if (supervisor is null) return new(null, "route-identity-unavailable");
            persons.Add(supervisor.Value);
        }
        return Unique(persons);
    }

    internal async Task<Guid?> EligibleAccountAsync(FinanceScope scope, Guid person, string level, decimal total, CancellationToken ct)
    {
        // A single current grant supplies capability, exact level and the full total.
        var accounts = await FinanceGrantQueries.Current(db, scope, "request.approve", clock.GetUtcNow())
            .Where(x => x.RecipientPersonId == person && x.ApprovalLevels.Contains(level)
                && x.ApprovalLimit != null && x.ApprovalLimit >= total)
            .Select(x => x.RecipientId).Distinct().OrderBy(x => x).ToArrayAsync(ct);
        foreach (var account in accounts)
            if (await people.ResolveAsync(account, scope.WorkspaceId, ct) == person) return account;
        return null;
    }

    private static RoutePerson Unique(HashSet<Guid> persons) => persons.Count switch
    {
        1 => new(persons.Single(), null),
        0 => new(null, "route-no-candidate"),
        _ => new(null, "route-ambiguous-role")
    };
}
