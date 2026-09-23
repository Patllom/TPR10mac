namespace TPR10.Api.Identity;

public enum SessionStage { PasswordChangeRequired, MfaEnrollmentRequired, MfaChallengeRequired, Active }
public sealed record SessionView(Guid UserId, SessionStage Stage, string[] Permissions, DateTimeOffset? MfaVerifiedAtUtc);
public sealed record IssuedSession(string Token, SessionView View);
public sealed record PasswordCheck(Guid UserId, bool MustChangePassword);
public interface IPasswordHasher
{
    Task<string> HashAsync(string password, CancellationToken ct);
    Task<bool> VerifyAsync(string password, string encoded, CancellationToken ct);
}
public interface IIdentityProvider
{
    Task<PasswordCheck?> VerifyAsync(string username, string password, CancellationToken ct);
}
public interface ISessionService
{
    Task<IssuedSession> IssueAsync(Guid userId, SessionStage stage, CancellationToken ct);
    Task<SessionView?> ValidateAsync(string token, CancellationToken ct);
    Task RevokeUserAsync(Guid userId, string reason, CancellationToken ct);
    Task<IssuedSession> RotateAsync(string token, SessionStage stage, DateTimeOffset? mfaVerifiedAtUtc, CancellationToken ct);
}
public interface IResetDelivery
{
    Task EnqueueAsync(Guid requestId, string recipient, string protectedPayload, CancellationToken ct);
}
