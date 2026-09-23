namespace TPR10.Api.Identity.Data;

public sealed class MfaAttemptState
{
    public Guid UserId { get; set; }
    public int FailedAttempts { get; set; }
    public DateTimeOffset WindowStartedAtUtc { get; set; }
    public DateTimeOffset? LockedUntilUtc { get; set; }
}
