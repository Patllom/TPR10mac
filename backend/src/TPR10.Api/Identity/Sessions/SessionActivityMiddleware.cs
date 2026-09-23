using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.Identity.Data;

namespace TPR10.Api.Identity.Sessions;

public sealed class SessionActivityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, RequestSession current, Tpr10DbContext db, TimeProvider clock)
    {
        if (current.Entity is { } session)
        {
            var now = clock.GetUtcNow();
            var idle = now.AddMinutes(-30);
            // Only successful transport/CSRF/authorization guards may renew idle time.
            var updated = await db.Set<IdentitySession>().Where(x => x.Id == session.Id && x.RevokedAtUtc == null
                && x.ExpiresAtUtc > now && x.LastSeenAtUtc > idle
                && db.Set<IdentityUser>().Any(u => u.Id == x.UserId && u.IsActive && u.SecurityVersion == x.SecurityVersion))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSeenAtUtc, now), context.RequestAborted);
            if (updated != 1) { await Results.Problem(statusCode: 401, title: "กรุณาเข้าสู่ระบบ").ExecuteAsync(context); return; }
        }
        await next(context);
    }
}
