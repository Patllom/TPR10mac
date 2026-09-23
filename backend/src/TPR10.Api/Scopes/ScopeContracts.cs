using System.Text.Json.Serialization;

namespace TPR10.Api.Scopes;

public sealed record Page<T>(T[] Items, int Total, int PageNumber, int PageSize);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExpectedChange([property: JsonRequired] long ExpectedVersion, [property: JsonRequired] string Reason);
