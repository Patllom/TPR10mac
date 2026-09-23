using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TPR10.Api.Data;
using TPR10.Api.Health;
using TPR10.Api.Correlation;
using TPR10.Api.Auditing;
using TPR10.Api.TechnicalProbes;
using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using TPR10.Api.Identity;
using TPR10.Api.Identity.Csrf;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Identity.Accounts;

if (args.Any(x => x.StartsWith("--bootstrap-admin", StringComparison.Ordinal)))
{
    Environment.ExitCode = await BootstrapCommand.RunAsync(args);
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    options.KnownProxies.Add(IPAddress.Loopback);
    options.KnownProxies.Add(IPAddress.IPv6Loopback);
    options.ForwardLimit = 1;
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddIdentityFoundation();
builder.Services.AddPreAuthCsrf(builder.Configuration, builder.Environment);
builder.Services.AddScoped<CorrelationContext>();
builder.Services.AddScoped<ICorrelationContext>(services => services.GetRequiredService<CorrelationContext>());
builder.Services.AddScoped<IAuditEventWriter, AuditEventWriter>();
builder.Services.AddDbContext<Tpr10DbContext>((services, options) =>
    options.UseNpgsql(services.GetRequiredService<IConfiguration>()["TPR10_CONNECTION_STRING"]
        ?? "Host=127.0.0.1;Port=54329;Database=tpr10;Username=tpr10_app;Timeout=1"));
builder.Services.AddScoped<DbContext>(services => services.GetRequiredService<Tpr10DbContext>());
builder.Services.AddHealthChecks().AddCheck<DatabaseReadinessHealthCheck>("database");
var app = builder.Build();
app.UseForwardedHeaders();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseRouting();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/v1/auth"))
        context.Response.OnStarting(() => { context.Response.Headers.CacheControl = "no-store"; return Task.CompletedTask; });
    try { await next(context); }
    catch (Exception error) when ((error is Npgsql.NpgsqlException or DbUpdateException
        || error is InvalidOperationException { InnerException: Npgsql.NpgsqlException })
        && (context.Request.Path.StartsWithSegments("/api/v1/auth") || context.Request.Cookies.ContainsKey(CsrfService.SessionCookieName)))
    {
        if (context.Response.HasStarted) throw;
        context.Response.Clear();
        await Results.Problem(statusCode: 503, title: "ระบบยืนยันตัวตนไม่พร้อมใช้งาน กรุณาลองใหม่ภายหลัง").ExecuteAsync(context);
    }
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<CsrfMiddleware>();
app.UseAuthorization();
app.UseMiddleware<RestrictedSessionMiddleware>();
app.UseMiddleware<SessionActivityMiddleware>();
app.MapAuthEndpoints();
app.MapGet("/api/health/live", () => Results.Ok(new { status = "live" }))
    .ExcludeFromDescription();
app.MapOpenApi("/api/openapi/{documentName}.json");
app.MapHealthChecks("/api/health/ready", new HealthCheckOptions
{
    ResponseWriter = (context, report) => context.Response.WriteAsJsonAsync(
        new { status = report.Status == HealthStatus.Healthy ? "ready" : "unavailable" })
});
if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
    app.MapTechnicalProbeEndpoints();
app.Run();
