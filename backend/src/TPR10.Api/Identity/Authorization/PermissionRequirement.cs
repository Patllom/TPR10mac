using Microsoft.AspNetCore.Authorization;

namespace TPR10.Api.Identity.Authorization;

public sealed record PermissionRequirement(string Capability, bool RequireMfa) : IAuthorizationRequirement;

public sealed class PermissionContext
{
    public Guid? ActingRoleId { get; set; }
    public string? Denial { get; set; }
}
