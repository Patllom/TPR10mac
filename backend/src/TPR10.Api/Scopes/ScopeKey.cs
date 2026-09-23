namespace TPR10.Api.Scopes;

public sealed record ScopeKey(Guid WorkspaceId, Guid? ProjectId = null, Guid? SiteId = null)
{
    public bool IsValid => WorkspaceId != Guid.Empty
        && ProjectId != Guid.Empty && SiteId != Guid.Empty
        && (SiteId is null || ProjectId is not null);
}
