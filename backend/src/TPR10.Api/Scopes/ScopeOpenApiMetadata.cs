namespace TPR10.Api.Scopes;

// Documentation only; never use this metadata as a runtime authorization decision.
public sealed record ScopeEndpointMetadata(string Mode, string? Capability, bool RequireMfa, string Level);
