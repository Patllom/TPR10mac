namespace TPR10.Api.Identity.Data;

// A bounded, expiring identifier bucket exists even when no account exists.
public sealed class LoginAttemptWindow
{
    public required byte[] IdentifierHash { get; set; }
    public int FailedAttempts { get; set; }
    public DateTimeOffset WindowStartedAtUtc { get; set; }
    public DateTimeOffset? LockedUntilUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
}
