using System.Globalization;
using System.Text.Json;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.Scopes.Data;

namespace TPR10.Api.Scopes.Probes;

public sealed class ScopeProbeService(Tpr10DbContext db, ScopeOperation operation, ScopeAccess access,
    ScopeProbeRepository repository, IAuditEventWriter audit, TimeProvider clock)
{
    public Task<IResult> ListAsync(ScopeKey key, int page, int pageSize, CancellationToken ct) =>
        operation.RunAsync(key, "scope-probe:read", false, async (context, token) =>
        {
            pageSize = Math.Min(pageSize, 100);
            if (page < 1 || pageSize < 1 || (long)(page - 1) * pageSize > int.MaxValue) return ScopeOperation.Problem(400);
            var result = await repository.ListAsync(context, page, pageSize, token);
            var response = new Page<object>(result.Items.Select(r => ScopeRecordProjection.Select(context, r)).ToArray(), result.Total, page, pageSize);
            await AuditAsync(context, "list", null, result.Items.Length, token);
            return Results.Ok(response);
        }, ct);

    public Task<IResult> DetailAsync(ScopeKey key, Guid id, CancellationToken ct) =>
        operation.RunAsync(key, "scope-probe:read", false, async (context, token) =>
        {
            if (id == Guid.Empty) return ScopeOperation.Problem(400);
            var row = await repository.FindAsync(context, id, token);
            if (row is null) return ScopeOperation.Problem(404);
            var response = ScopeRecordProjection.Select(context, row);
            await AuditAsync(context, "detail", id, 1, token);
            return Results.Ok(response);
        }, ct);

    public Task<IResult> CreateAsync(ScopeKey key, CreateScopeRecord request, CancellationToken ct) =>
        operation.RunAsync(key, "scope-probe:write", request.RestrictedNote.ValueKind != JsonValueKind.Undefined, async (context, token) =>
        {
            if (!Valid(request.Note, request.RestrictedNote)) return ScopeOperation.Problem(400);
            if (request.RestrictedNote.ValueKind != JsonValueKind.Undefined && !context.CanReadRestricted) return ScopeOperation.Problem(403);
            var row = new ScopeProbeRecord
            {
                Id = Guid.NewGuid(),
                WorkspaceId = context.Key.WorkspaceId,
                ProjectId = context.Key.ProjectId,
                SiteId = context.Key.SiteId,
                Note = request.Note,
                RestrictedNote = Restricted(request.RestrictedNote),
                CreatedBy = context.ActorId,
                CreatedAtUtc = clock.GetUtcNow()
            };
            db.Add(row);
            var response = await WriteViewAsync(context, row, token);
            await AuditAsync(context, "create", row.Id, 1, token);
            return Results.Created(Path(key, row.Id), response);
        }, ct);

    public Task<IResult> UpdateAsync(ScopeKey key, Guid id, UpdateScopeRecord request, CancellationToken ct) =>
        operation.RunAsync(key, "scope-probe:write", request.RestrictedNote.ValueKind != JsonValueKind.Undefined, async (context, token) =>
        {
            if (id == Guid.Empty || request.ExpectedVersion < 1 || !Valid(request.Note, request.RestrictedNote)) return ScopeOperation.Problem(400);
            if (request.RestrictedNote.ValueKind != JsonValueKind.Undefined && !context.CanReadRestricted) return ScopeOperation.Problem(403);
            var row = await repository.FindAsync(context, id, token);
            if (row is null) return ScopeOperation.Problem(404);
            if (row.Version != request.ExpectedVersion || row.Version == long.MaxValue) return ScopeOperation.Problem(409);
            var restricted = request.RestrictedNote.ValueKind == JsonValueKind.Undefined ? row.RestrictedNote : Restricted(request.RestrictedNote);
            if (row.Note != request.Note || row.RestrictedNote != restricted)
            {
                row.Note = request.Note; row.RestrictedNote = restricted; row.Version++;
                row.UpdatedBy = context.ActorId; row.UpdatedAtUtc = clock.GetUtcNow();
            }
            var response = await WriteViewAsync(context, row, token);
            await AuditAsync(context, "update", id, 1, token);
            return new UpdatedRecordResult(response, Path(key, id));
        }, ct);

    public Task<IResult> RejectBindingAsync(ScopeKey key, string capability, CancellationToken ct) =>
        operation.RunAsync(key, capability, false, (_, _) => Task.FromResult(ScopeOperation.Problem(400)), ct);

    private async Task<object> WriteViewAsync(ScopeContext context, ScopeProbeRecord row, CancellationToken ct)
    {
        var read = await access.ResolveAsync(context.Key, "scope-probe:read", false, ct);
        return read.Context is { } allowed ? ScopeRecordProjection.Select(allowed, row) : new WrittenRecordView(row.Id, row.Version);
    }
    private Task AuditAsync(ScopeContext context, string action, Guid? id, int count, CancellationToken ct) =>
        audit.WriteAsync(new SecurityAuditRequest(context.ActorId, context.ActingRoleId, context.Key.WorkspaceId, context.Key.ProjectId, context.Key.SiteId,
            "scope.record." + action, "scope-probe-record", id, "success", new Dictionary<string, string>
            {
                ["row-count"] = count.ToString(CultureInfo.InvariantCulture),
                ["capability"] = context.Capability,
                ["scope-validation"] = "authorized",
                ["assignment-id"] = context.AssignmentId.ToString()
            }), ct);
    private static bool Text(string? value) => value is not null && value.Length <= 500 && !value.Any(char.IsControl);
    private static bool Valid(string? note, JsonElement restricted) => Text(note) && (restricted.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
        || restricted.ValueKind == JsonValueKind.String && Text(restricted.GetString()));
    private static string? Restricted(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    internal static string Path(ScopeKey key, Guid id) => "/api/v1/workspaces/" + key.WorkspaceId
        + (key.ProjectId is { } p ? "/projects/" + p : "") + (key.SiteId is { } s ? "/sites/" + s : "") + "/scope-probe-records/" + id;

    private sealed class UpdatedRecordResult(object value, string location) : IResult, IStatusCodeHttpResult
    {
        public int? StatusCode => StatusCodes.Status200OK;
        public Task ExecuteAsync(HttpContext context)
        {
            context.Response.Headers.Location = location;
            return Results.Ok(value).ExecuteAsync(context);
        }
    }
}
