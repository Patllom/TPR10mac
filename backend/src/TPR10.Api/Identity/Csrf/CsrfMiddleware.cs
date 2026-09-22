using TPR10.Api.Auditing;
using TPR10.Api.Data;

namespace TPR10.Api.Identity.Csrf;

public sealed class CsrfMiddleware(RequestDelegate next)
{
    public static bool IsUnsafe(string method) => !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method)
        && !HttpMethods.IsOptions(method) && !HttpMethods.IsTrace(method);
    public static bool IsIssuer(HttpContext context) => context.GetEndpoint()?.Metadata.GetMetadata<CsrfIssuerMetadata>() is not null;

    public async Task InvokeAsync(HttpContext context, CsrfService csrf, Tpr10DbContext db, IAuditEventWriter audit)
    {
        var issuer = IsIssuer(context);
        var unsafeMethod = IsUnsafe(context.Request.Method);
        if (issuer || unsafeMethod) context.Response.Headers.CacheControl = "no-store";
        var valid = unsafeMethod ? await csrf.ValidateAsync(context, context.RequestAborted)
            : !issuer || csrf.HasValidTransport(context, requireOrigin: false);
        if (valid) { await next(context); return; }
        // Denial is committed before dispatch; no endpoint mutation has run.
        await using var transaction = await db.Database.BeginTransactionAsync(context.RequestAborted);
        await audit.WriteAsync("security.csrf.denied", Guid.Empty,
            new Dictionary<string, string> { ["outcome"] = "denied", ["reason"] = "invalid-csrf-or-transport" }, context.RequestAborted);
        await db.SaveChangesAsync(context.RequestAborted);
        await transaction.CommitAsync(context.RequestAborted);
        await Results.Problem(statusCode: StatusCodes.Status403Forbidden, type: "urn:tpr10:csrf-invalid",
            title: "คำขอไม่ผ่านการตรวจสอบความปลอดภัย").ExecuteAsync(context);
    }
}
