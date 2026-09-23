using TPR10.Api.Identity.Sessions;

namespace TPR10.Api.Identity.Mfa;

public sealed record MfaCodeRequest(string Code);

public static class MfaEndpoints
{
    public static void MapMfaEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/auth/mfa").RequireAuthorization();
        group.MapPost("/enroll", (HttpContext context, MfaService mfa, CancellationToken ct) => mfa.EnrollAsync(context, ct))
            .WithMetadata(new AllowedSessionStages(SessionStage.MfaEnrollmentRequired), new IdentityProblemTypes(409)).Produces<MfaEnrollmentResponse>();
        group.MapPost("/confirm", (MfaCodeRequest request, HttpContext context, MfaService mfa, CancellationToken ct) => mfa.ConfirmAsync(request.Code, context, ct))
            .WithMetadata(new AllowedSessionStages(SessionStage.MfaEnrollmentRequired)).Produces<MfaConfirmationResponse>();
        group.MapPost("/challenge", (MfaCodeRequest request, HttpContext context, MfaService mfa, CancellationToken ct) => mfa.ChallengeAsync(request.Code, context, ct))
            .WithMetadata(new AllowedSessionStages(SessionStage.MfaChallengeRequired)).Produces<MfaSessionResponse>();
        group.MapPost("/recover", (MfaCodeRequest request, HttpContext context, MfaService mfa, CancellationToken ct) => mfa.RecoverAsync(request.Code, context, ct))
            .WithMetadata(new AllowedSessionStages(SessionStage.MfaChallengeRequired)).Produces<MfaSessionResponse>();
    }
}
