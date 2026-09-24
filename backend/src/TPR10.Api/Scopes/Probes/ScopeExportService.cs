using System.Globalization;
using TPR10.Api.Auditing;

namespace TPR10.Api.Scopes.Probes;

public sealed class ScopeExportService(ScopeOperation operation, ScopeProbeRepository repository, IAuditEventWriter audit)
{
    public Task<IResult> ExportAsync(ScopeKey key, ExportScopeRecords request, CancellationToken ct) =>
        operation.RunAsync(key, "scope-probe:export", true, async (context, token) =>
        {
            if (request.CreatedFrom is { Offset: var fromOffset } && fromOffset != TimeSpan.Zero
                || request.CreatedTo is { Offset: var toOffset } && toOffset != TimeSpan.Zero
                || request.CreatedFrom is { } from && request.CreatedTo is { } to && from >= to)
                return ScopeOperation.Problem(400);
            var rows = await repository.ExportAsync(context, request.CreatedFrom, request.CreatedTo, token);
            if (rows.Length > 100) return ScopeOperation.Problem(400);
            // Eager projection: no file, streaming callback or external destination escapes the transaction.
            var response = new ExportRecordPage<object>(rows.Select(r => ScopeRecordProjection.Select(context, r)).ToArray(), rows.Length);
            await audit.WriteAsync(new SecurityAuditRequest(context.ActorId, context.ActingRoleId, key.WorkspaceId, key.ProjectId, key.SiteId,
                "scope.record.export", "scope-probe-record", null, "success", new Dictionary<string, string>
                {
                    ["capability"] = context.Capability,
                    ["scope-validation"] = "authorized",
                    ["assignment-id"] = context.AssignmentId.ToString(),
                    ["row-count"] = rows.Length.ToString(CultureInfo.InvariantCulture),
                    ["destination-type"] = "response-json",
                    ["filter-from"] = request.CreatedFrom?.ToString("O", CultureInfo.InvariantCulture) ?? "unbounded",
                    ["filter-to"] = request.CreatedTo?.ToString("O", CultureInfo.InvariantCulture) ?? "unbounded"
                }), token);
            return Results.Ok(response);
        }, ct);
}
