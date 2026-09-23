namespace TPR10.Api.Identity.Sessions;

public sealed record AllowedSessionStages(params SessionStage[] Stages)
{
    public static readonly AllowedSessionStages Common = new(Enum.GetValues<SessionStage>());
}

public sealed class RestrictedSessionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, RequestSession session)
    {
        if (session.View is { Stage: not SessionStage.Active } view && context.GetEndpoint() is { } endpoint
            && endpoint.Metadata.GetMetadata<AllowedSessionStages>()?.Stages.Contains(view.Stage) != true)
        {
            context.Response.Headers.CacheControl = "no-store";
            await Results.Problem(statusCode: 403, title: "ต้องทำขั้นตอนยืนยันตัวตนให้ครบก่อนใช้งาน").ExecuteAsync(context);
            return;
        }
        await next(context);
    }
}
