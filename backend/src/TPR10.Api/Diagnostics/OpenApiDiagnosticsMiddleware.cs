using System.Diagnostics;
using System.Text.Json;
using TPR10.Api.Correlation;

namespace TPR10.Api.Diagnostics;

// Applied only to the route registered by MapOpenApi, never inferred from user input.
public sealed class OpenApiDiagnosticsMetadata;

public sealed class OpenApiDiagnosticsMiddleware(RequestDelegate next, ILogger<OpenApiDiagnosticsMiddleware> logger)
{
    private const int MaxExceptions = 8;
    private const int MaxFrames = 32;
    private static readonly EventId FailureEvent = new(5001, "OpenApiGenerationFailed");

    public async Task InvokeAsync(HttpContext context, ICorrelationContext correlation, TimeProvider clock)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<OpenApiDiagnosticsMetadata>() is null)
        {
            await next(context);
            return;
        }

        try { await next(context); }
        catch (Exception exception)
        {
            try
            {
                var causes = new List<ExceptionCause>();
                var truncated = false;
                Capture(exception, null, causes, ref truncated);
                // Intentionally no exception argument: providers must not render Message, Data or ToString().
                logger.LogError(FailureEvent,
                    "OpenAPI generation failed at {TimestampUtc}; correlation {CorrelationId}; endpoint {Endpoint}; " +
                    "exception {ExceptionType}; stack {StackTrace}; causes {ExceptionCauses}; truncated {CausesTruncated}",
                    clock.GetUtcNow(), correlation.CorrelationId, "/api/openapi/{documentName}.json",
                    causes[0].ExceptionType, causes[0].StackTrace, JsonSerializer.Serialize(causes), truncated);
            }
            catch
            {
                // Diagnostics (including a failing provider) must not replace the original failure.
            }
            // Preserve the existing exception handler, response-started behavior and framework logging.
            throw;
        }
    }

    private static void Capture(Exception exception, int? parentIndex, List<ExceptionCause> causes, ref bool truncated)
    {
        if (causes.Count == MaxExceptions)
        {
            truncated = true;
            return;
        }

        var index = causes.Count;
        var frames = new StackTrace(exception, false).GetFrames();
        var stack = string.Join("\n", frames.Take(MaxFrames).Select(frame =>
        {
            var method = frame.GetMethod();
            return "at " + Bound(method?.DeclaringType?.FullName ?? "<unknown>") + "." + Bound(method?.Name ?? "<unknown>");
        }));
        if (frames.Length > MaxFrames) stack += "\n[frames truncated]";
        causes.Add(new ExceptionCause(index, parentIndex, Bound(exception.GetType().FullName ?? exception.GetType().Name), stack));

        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                if (causes.Count == MaxExceptions) { truncated = true; break; }
                Capture(inner, index, causes, ref truncated);
            }
        }
        else if (exception.InnerException is { } inner)
        {
            Capture(inner, index, causes, ref truncated);
        }
    }

    private static string Bound(string symbol) => symbol.Length <= 256 ? symbol : symbol[..256] + "[truncated]";

    private sealed record ExceptionCause(int Index, int? ParentIndex, string ExceptionType, string StackTrace);
}
