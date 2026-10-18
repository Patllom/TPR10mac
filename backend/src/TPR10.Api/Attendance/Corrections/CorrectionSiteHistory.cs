using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Data;
using TPR10.Api.Scopes.Data;

namespace TPR10.Api.Attendance.Corrections;

public sealed class CorrectionSiteHistory(Tpr10DbContext db)
{
    public async Task<int?> ValidateAsync(Guid employeeId, CorrectionProposal proposal, CancellationToken ct)
    {
        if (!await Memberships(employeeId, proposal.InUtc).AnyAsync(ct)
            || !await Assigned(employeeId, proposal.InSite, proposal.InUtc).AnyAsync(ct)) return 409;
        if (proposal.OutUtc is { } at && (proposal.OutSite is null
            || !await Memberships(employeeId, at).AnyAsync(ct)
            || !await Assigned(employeeId, proposal.OutSite, at).AnyAsync(ct))) return 409;
        return null;
    }

    internal IQueryable<EmployeeMembership> Memberships(Guid employeeId, DateTimeOffset at)
    {
        at = at.ToUniversalTime();
        return db.Set<EmployeeMembership>().AsNoTracking().Where(x => x.UserId == employeeId && x.ValidFromUtc <= at
            && (x.ValidToUtc == null || at < x.ValidToUtc));
    }

    // The retained grant interval proves assignment, not today's role capabilities.
    private IQueryable<ScopeAssignment> Assigned(Guid employee, CorrectionSite site, DateTimeOffset at)
    {
        at = at.ToUniversalTime();
        return db.Set<ScopeAssignment>().AsNoTracking().Where(x => x.UserId == employee && x.WorkspaceId == site.WorkspaceId
            && x.ProjectId == site.ProjectId && x.SiteId == site.SiteId && x.CreatedAtUtc <= at
            && (x.RevokedAtUtc == null || at < x.RevokedAtUtc));
    }
}
