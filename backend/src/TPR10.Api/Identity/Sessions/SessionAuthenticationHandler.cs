using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using TPR10.Api.Identity.Csrf;

namespace TPR10.Api.Identity.Sessions;

public sealed class SessionAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, ISessionService sessions, CsrfService csrf)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TPR10.Session";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = Request.Cookies[CsrfService.SessionCookieName];
        if (token is null) return AuthenticateResult.NoResult();
        if (!csrf.HasTrustedTransport(Context, false)) return AuthenticateResult.Fail("Invalid session transport");
        var session = await sessions.ValidateAsync(token, Context.RequestAborted);
        if (session is null)
        {
            Response.Cookies.Delete(CsrfService.SessionCookieName, CookieOptions());
            return AuthenticateResult.Fail("Invalid session");
        }
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, session.UserId.ToString()),
            new Claim("session_stage", session.Stage.ToString())], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        Results.Problem(statusCode: 401, title: "กรุณาเข้าสู่ระบบ").ExecuteAsync(Context);

    public static CookieOptions CookieOptions() => new()
    { Secure = true, HttpOnly = true, Path = "/", SameSite = SameSiteMode.Lax, IsEssential = true };
}
