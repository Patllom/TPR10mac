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
