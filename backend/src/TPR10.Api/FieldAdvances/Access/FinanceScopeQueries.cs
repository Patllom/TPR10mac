using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.Organization.Data;

namespace TPR10.Api.FieldAdvances.Access;

internal static class FinanceScopeQueries
{
    // Names are candidate data, disclosed only after current capability checks and public paging.
    internal static IQueryable<FinanceScopeCandidate> Candidates(Tpr10DbContext db, Guid actor, bool projectsOnly = false) =>
        from scope in db.Set<GrantProposal>().AsNoTracking().Where(x => x.RecipientId == actor && (!projectsOnly || x.ProjectId != null))
            .Select(x => new { x.WorkspaceId, x.ProjectId }).Distinct()
        join workspace in db.Set<Workspace>() on scope.WorkspaceId equals workspace.Id
        join project in db.Set<Project>() on new { scope.WorkspaceId, Id = scope.ProjectId }
            equals new { project.WorkspaceId, Id = (Guid?)project.Id } into projects
        from project in projects.DefaultIfEmpty()
        orderby scope.WorkspaceId, scope.ProjectId
        select new FinanceScopeCandidate(scope.WorkspaceId, scope.ProjectId, workspace.Name, project == null ? null : project.Name);
}

internal sealed record FinanceScopeCandidate(Guid WorkspaceId, Guid? ProjectId, string WorkspaceName, string? ProjectName);
