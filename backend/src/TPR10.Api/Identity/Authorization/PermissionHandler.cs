using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.Data;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Sessions;

namespace TPR10.Api.Identity.Authorization;

public sealed class PermissionHandler(Tpr10DbContext db, RequestSession session, PermissionContext permission, TimeProvider clock)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var ct = (context.Resource as HttpContext)?.RequestAborted ?? default;
        var view = session.View;
        var entity = session.Entity;
        string? denial = null;
        if (view is null || entity is null) denial = "session-required";
        else if (view.Stage != SessionStage.Active)
            denial = entity.Stage == SessionStage.Active && entity.MfaVerifiedAtUtc is not null ? "mfa-required" : "stage-restricted";
        else
        {
            var role = await (from ur in db.Set<UserRole>()
                              join rp in db.Set<RolePermission>() on ur.RoleId equals rp.RoleId
                              join p in db.Set<IdentityPermission>() on rp.PermissionId equals p.Id
                              where ur.UserId == view.UserId && p.Capability == requirement.Capability && p.Domain == "system"
                              orderby ur.RoleId
                              select (Guid?)ur.RoleId).FirstOrDefaultAsync(ct);
            if (role is null) denial = "permission-denied";
            else if (requirement.RequireMfa && (view.MfaVerifiedAtUtc is not { } verified
                || verified > clock.GetUtcNow() || verified.AddMinutes(15) <= clock.GetUtcNow()
                || !await db.Set<MfaFactor>().AnyAsync(x => x.UserId == view.UserId && x.ConfirmedAtUtc != null && x.RevokedAtUtc == null, ct)))
                denial = "mfa-required";
            else permission.ActingRoleId = role;
        }
        if (denial is null) context.Succeed(requirement);
        else { permission.Denial = denial; context.Fail(); }
    }
}

public sealed class PermissionResultHandler : IAuthorizationMiddlewareResultHandler
{
    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (!policy.Requirements.OfType<PermissionRequirement>().Any())
        {
            await new AuthorizationMiddlewareResultHandler().HandleAsync(next, context, policy, result);
            return;
        }
        if (result.Succeeded) { await next(context); return; }
        var services = context.RequestServices;
        var permission = services.GetRequiredService<PermissionContext>();
        var session = services.GetRequiredService<RequestSession>();
        var reason = permission.Denial ?? (result.Challenged ? "session-required" : "permission-denied");
        var db = services.GetRequiredService<Tpr10DbContext>();
        await services.GetRequiredService<IAuditEventWriter>().WriteAsync(new SecurityAuditRequest(session.View?.UserId, null, null, null, null,
            "identity.authorization.denied", "endpoint", null, "denied", new Dictionary<string, string>
            { ["reason"] = reason, ["capability"] = string.Join(",", policy.Requirements.OfType<PermissionRequirement>().Select(x => x.Capability)) }), context.RequestAborted);
        await db.SaveChangesAsync(context.RequestAborted);
        context.Response.Headers.CacheControl = "no-store";
        await Results.Problem(statusCode: reason == "session-required" ? 401 : 403, type: "urn:tpr10:" + reason,
            title: "ไม่มีสิทธิ์หรือยังยืนยันตัวตนไม่ครบสำหรับคำขอนี้", extensions: new Dictionary<string, object?>
            { ["correlationId"] = services.GetRequiredService<ICorrelationContext>().CorrelationId.ToString("D") }).ExecuteAsync(context);
    }
}
