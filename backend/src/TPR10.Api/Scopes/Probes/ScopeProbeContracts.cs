using System.Text.Json;
using System.Text.Json.Serialization;
using TPR10.Api.Scopes.Data;

namespace TPR10.Api.Scopes.Probes;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateScopeRecord([property: JsonRequired] string Note, JsonElement RestrictedNote);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateScopeRecord([property: JsonRequired] string Note, JsonElement RestrictedNote, [property: JsonRequired] long ExpectedVersion);
public sealed record PublicRecordView(Guid Id, string Note, long Version, DateTimeOffset CreatedAtUtc, DateTimeOffset? UpdatedAtUtc);
public sealed record RestrictedRecordView(Guid Id, string Note, long Version, DateTimeOffset CreatedAtUtc, DateTimeOffset? UpdatedAtUtc, string? RestrictedNote);
public sealed record WrittenRecordView(Guid Id, long Version);

public static class ScopeRecordProjection
{
    public static object Select(ScopeContext context, ScopeProbeRecord row) => context.CanReadRestricted
        ? new RestrictedRecordView(row.Id, row.Note, row.Version, row.CreatedAtUtc, row.UpdatedAtUtc, row.RestrictedNote)
        : new PublicRecordView(row.Id, row.Note, row.Version, row.CreatedAtUtc, row.UpdatedAtUtc);
}

public sealed record ScopeProbeBoundary(string Capability, string Level);
