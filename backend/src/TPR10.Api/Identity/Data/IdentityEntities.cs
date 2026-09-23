namespace TPR10.Api.Identity.Data;

public sealed class IdentityUser
{
    public Guid Id { get; set; }
    public required string Username { get; set; }
    public required string NormalizedUsername { get; set; }
    public string? Email { get; set; }
    public bool IsActive { get; set; } = true;
    public long SecurityVersion { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? UpdatedAtUtc { get; set; }
}

public sealed class LocalCredential
{
    public Guid UserId { get; set; }
    public required string PasswordHash { get; set; }
    public bool MustChangePassword { get; set; } = true;
    public int FailedAttempts { get; set; }
    public DateTimeOffset? FailureWindowStartedAtUtc { get; set; }
    public DateTimeOffset? LockedUntilUtc { get; set; }
    public DateTimeOffset PasswordChangedAtUtc { get; set; }
}

public sealed class ExternalIdentity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string Provider { get; set; }
    public required string Subject { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class IdentityRole
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string RoleClass { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class IdentityPermission
{
    public Guid Id { get; set; }
    public required string Capability { get; set; }
}

public sealed class RolePermission
{
    public Guid RoleId { get; set; }
    public Guid PermissionId { get; set; }
}

public sealed class UserRole
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class MfaFactor
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string ProtectedSecret { get; set; }
    public Guid? EnrollmentSessionId { get; set; }
    public long? LastUsedStep { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ConfirmedAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
}

public sealed class IdentitySession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required byte[] TokenHash { get; set; }
    public SessionStage Stage { get; set; }
    public long SecurityVersion { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset LastSeenAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? MfaVerifiedAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
}

public sealed class PasswordResetRequest
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required byte[] TokenHash { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? ConsumedAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
}

public sealed class PreAuthFlow
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public required byte[] TokenHash { get; set; }
    public required string Purpose { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? ConsumedAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
}

public sealed class MfaRecoveryCode
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required byte[] CodeHash { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ConsumedAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
}

public sealed class IdentityDeliveryOutbox
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public required string Recipient { get; set; }
    public required string ProtectedPayload { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? NextAttemptAtUtc { get; set; }
    public DateTimeOffset? DeliveredAtUtc { get; set; }
}
