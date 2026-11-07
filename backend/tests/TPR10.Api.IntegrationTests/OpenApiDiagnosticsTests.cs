using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using TPR10.Api.Correlation;
using TPR10.Api.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TPR10.Api.IntegrationTests;

public sealed class OpenApiDiagnosticsTests
{
    private const string Category = "TPR10.Api.Diagnostics.OpenApiDiagnosticsMiddleware";
    private const string Secret = "sensitive-value-must-not-be-logged";

    [Fact]
    public async Task Failed_document_logs_safe_exception_causes_and_preserves_generic_problem()
    {
        var logs = new CapturedLogs();
        await using var baseFactory = new OpenApiFactory();
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
            builder.ConfigureServices(services => services.Configure<Microsoft.AspNetCore.OpenApi.OpenApiOptions>("v1", options =>
                options.AddDocumentTransformer((_, _, _) => ThrowDocumentFailure())));
        });
        using var client = factory.CreateClient();
        var correlationId = Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/openapi/v1.json?token=" + Secret);
        request.Headers.Add("X-Correlation-ID", correlationId.ToString());
        request.Headers.Add("Authorization", "Bearer " + Secret);
        request.Headers.Add("Cookie", "diagnostic-secret=" + Secret);
        request.Content = new StringContent(Secret);
        var before = DateTimeOffset.UtcNow;
        using var response = await client.SendAsync(request);
        var after = DateTimeOffset.UtcNow;
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(correlationId.ToString(), Assert.Single(response.Headers.GetValues("X-Correlation-ID")));
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(500, problem.RootElement.GetProperty("status").GetInt32());
        Assert.False(problem.RootElement.TryGetProperty("exception", out _));
        Assert.DoesNotContain(Secret, body);
        Assert.DoesNotContain(nameof(ThrowDocumentFailure), body);

        var entry = Assert.Single(logs.Entries, entry => entry.Category == Category);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("OpenApiGenerationFailed", entry.Event.Name);
        Assert.Null(entry.Exception); // Never pass the raw exception to any provider.
        Assert.Equal(correlationId, entry.State["CorrelationId"]);
        var timestamp = Assert.IsType<DateTimeOffset>(entry.State["TimestampUtc"]);
        Assert.Equal(TimeSpan.Zero, timestamp.Offset);
        Assert.InRange(timestamp, before, after);
        Assert.Equal("/api/openapi/{documentName}.json", entry.State["Endpoint"]);
        Assert.Equal(typeof(AggregateException).FullName, entry.State["ExceptionType"]);
        Assert.Contains(nameof(ThrowDocumentFailure), Assert.IsType<string>(entry.State["StackTrace"]));
        var causes = Assert.IsType<string>(entry.State["ExceptionCauses"]);
        Assert.Contains(typeof(InvalidOperationException).FullName!, causes);
        Assert.Contains(typeof(ArgumentException).FullName!, causes);
        Assert.Contains(nameof(ThrowInnerFailure), causes);
        Assert.DoesNotContain(Secret, entry.Message + JsonSerializer.Serialize(entry.State));
        // The existing handler still sees the original exception: this feature does not sanitize its logs.
        Assert.Contains(logs.Entries, e => e.Category.StartsWith("Microsoft.AspNetCore.Diagnostics.ExceptionHandler")
            && e.Exception is AggregateException);
    }

    [Theory]
    [InlineData("/api/health/live", HttpStatusCode.OK)]
    [InlineData("/api/openapi/v1.json", HttpStatusCode.OK)]
    [InlineData("/api/openapi/unknown-secret.json", HttpStatusCode.NotFound)]
    [InlineData("/api/openapi/unknown-secret/other", HttpStatusCode.NotFound)]
    public async Task Successful_or_unmatched_requests_do_not_emit_failure_diagnostics(string path, HttpStatusCode status)
    {
        var logs = new CapturedLogs();
        await using var baseFactory = new OpenApiFactory();
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(logs)));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.Equal(status, response.StatusCode);
        Assert.DoesNotContain(logs.Entries, entry => entry.Category == Category);
    }

    [Fact]
    public async Task Unmarked_failure_is_rethrown_unchanged_without_diagnostics_even_on_openapi_like_path()
    {
        var logs = new CapturedLogs();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var failure = new InvalidOperationException(Secret);
        var middleware = new OpenApiDiagnosticsMiddleware(_ => throw failure,
            loggerFactory.CreateLogger<OpenApiDiagnosticsMiddleware>());
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/openapi/" + Secret;
        var caught = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            middleware.InvokeAsync(context, new CorrelationContext(), TimeProvider.System));
        Assert.Same(failure, caught);
        Assert.Empty(logs.Entries);
    }

    [Fact]
    public async Task Cause_capture_is_bounded_and_does_not_trust_exception_stacktrace_override()
    {
        var logs = new CapturedLogs();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var failure = new AggregateException(Secret,
            Enumerable.Range(0, 20).Select(_ => new UnsafeStackException()));
        var middleware = new OpenApiDiagnosticsMiddleware(_ => throw failure,
            loggerFactory.CreateLogger<OpenApiDiagnosticsMiddleware>());
        var context = new DefaultHttpContext();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new OpenApiDiagnosticsMetadata()), "test"));
        var caught = await Assert.ThrowsAsync<AggregateException>(() =>
            middleware.InvokeAsync(context, new CorrelationContext(), TimeProvider.System));
        Assert.Same(failure, caught);
        var entry = Assert.Single(logs.Entries);
        Assert.Equal(true, entry.State["CausesTruncated"]);
        using var causes = JsonDocument.Parse(Assert.IsType<string>(entry.State["ExceptionCauses"]));
        Assert.Equal(8, causes.RootElement.GetArrayLength());
        Assert.Equal(JsonValueKind.Null, causes.RootElement[0].GetProperty("ParentIndex").ValueKind);
        Assert.Equal(0, causes.RootElement[1].GetProperty("ParentIndex").GetInt32());
        Assert.DoesNotContain(Secret, entry.Message + JsonSerializer.Serialize(entry.State));
        Assert.Null(entry.Exception);
    }

    private sealed class UnsafeStackException : Exception
    {
        public override string StackTrace => Secret;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task ThrowDocumentFailure()
    {
        try { ThrowInnerFailure(); }
        catch (Exception inner)
        {
            var error = new AggregateException(Secret, inner, new ArgumentException(Secret));
            error.Data[Secret] = Secret;
            throw error;
        }
        return Task.CompletedTask;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInnerFailure() => throw new InvalidOperationException(Secret);

    private sealed record Entry(string Category, LogLevel Level, EventId Event, Exception? Exception,
        Dictionary<string, object?> State, string Message);

    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<Entry> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, Entries);
        public void Dispose() { }
    }

    private sealed class CaptureLogger(string category, ConcurrentQueue<Entry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => entries.Enqueue(new Entry(category, logLevel, eventId,
                exception, state is IEnumerable<KeyValuePair<string, object?>> values
                    ? values.ToDictionary(pair => pair.Key, pair => pair.Value) : new(), formatter(state, exception)));
    }
}
