using System.Net;
using System.Runtime.Versioning;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Attendance.Recording;
using TPR10.Api.Attendance.Storage;
using TPR10.Api.Identity.Csrf;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Sessions;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
[UnsupportedOSPlatform("windows")]
public sealed class AttendanceRecordingApiTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(true, 11534336L)]
    [InlineData(false, 30000000L)]
    public void KestrelBodyLimitIsScopedToCaptureEndpoints(bool upload, long expected)
    {
        var http = new DefaultHttpContext(); var feature = new BodyLimit();
        http.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(upload ? new object[] { new CaptureUploadBoundary() } : []), "test"));
        RecordingEndpoints.ApplyCaptureBodyLimit(http);
        Assert.Equal(expected, feature.MaxRequestBodySize);
    }
    private sealed class BodyLimit : IHttpMaxRequestBodySizeFeature
    { public bool IsReadOnly => false; public long? MaxRequestBodySize { get; set; } = 30000000; }
    [Fact]
    public async Task ImageBudget429PreservesRetryAfterAndSession()
    {
        await using var f = await RecordingHttpFixture.CreateAsync(postgres.ConnectionString,
            services => services.AddSingleton<IImageStampService>(new BusyImageService()));
        using var response = await f.Upload(await f.Metadata());
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(5), response.Headers.RetryAfter!.Delta);
        using var current = await f.Client.GetAsync("/api/v1/attendance/current"); Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        await using var db = f.Recording.Capture.Data.Database.CreateContext(); Assert.False(await db.Set<AttendanceEvent>().AnyAsync());
    }
    private sealed class BusyImageService : IImageStampService
    {
        public Task<StampedImage> StampAsync(Stream source, StampRequest stamp, CancellationToken ct) => throw new ImageProcessingException("image-busy");
        public StampedImage Thumbnail(StampedImage full) => throw new InvalidOperationException("must not publish");
    }
    [Theory]
    [InlineData("from=2026-09-01&to=2026-10-02")]
    [InlineData("from=2026-09-01&to=2026-09-26&pageSize=101")]
    [InlineData("from=2026-09-01&to=2026-09-26&ownerId=other")]
    [InlineData("from=2026-09-01&from=2026-09-02&to=2026-09-26")]
    public async Task HistoryHttpRejectsUnboundedOrAmbiguousQuery(string query)
    {
        await using var f = await RecordingHttpFixture.CreateAsync(postgres.ConnectionString);
        using var response = await f.Client.GetAsync("/api/v1/attendance/mine?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
    [Fact]
    public async Task HttpCaptureReplayHistoryAndCheckoutUseRealPublication()
    {
        await using var f = await RecordingHttpFixture.CreateAsync(postgres.ConnectionString);
        var metadata = await f.Metadata();
        using var first = await f.Upload(metadata);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var receipt = await first.Content.ReadFromJsonAsync<CaptureReceipt>(); Assert.NotNull(receipt);
        using var replay = await f.Upload(metadata); Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(receipt, await replay.Content.ReadFromJsonAsync<CaptureReceipt>());
        using var status = await f.Client.GetAsync($"/api/v1/attendance/operations/{metadata.IdempotencyKey}?action=CheckIn");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal("Committed", (await status.Content.ReadFromJsonAsync<CaptureOperationView>())!.State);
        using var current = await f.Client.GetAsync("/api/v1/attendance/current"); Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        using var history = await f.Client.GetAsync("/api/v1/attendance/mine?from=2026-09-26&to=2026-09-26"); Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var page = await history.Content.ReadFromJsonAsync<JsonElement>(); Assert.Single(page.GetProperty("items").EnumerateArray());
        using var photo = await f.Client.GetAsync($"/api/v1/attendance/evidence/{receipt.EvidenceId}"); Assert.Equal(HttpStatusCode.OK, photo.StatusCode);
        f.Recording.Capture.Clock.Advance(TimeSpan.FromSeconds(1));
        using var checkout = await f.Upload(await f.Metadata("CheckOut"), action: "check-outs"); Assert.Equal(HttpStatusCode.Created, checkout.StatusCode);
        Assert.Equal(receipt.DayId, (await checkout.Content.ReadFromJsonAsync<CaptureReceipt>())!.DayId);
        Assert.True(first.Headers.CacheControl!.NoStore);
    }

    [Theory]
    [InlineData("missing-metadata", 400)]
    [InlineData("missing-photo", 400)]
    [InlineData("duplicate-metadata", 400)]
    [InlineData("duplicate-photo", 400)]
    [InlineData("extra", 400)]
    [InlineData("unknown-json", 400)]
    [InlineData("duplicate-json", 400)]
    [InlineData("large-metadata", 413)]
    [InlineData("large-photo", 413)]
    [InlineData("fake-jpeg", 415)]
    [InlineData("huge-pixels", 415)]
    [InlineData("base64", 415)]
    public async Task MultipartRejectsInvalidPartsWithoutAttendance(string mode, int expected)
    {
        await using var f = await RecordingHttpFixture.CreateAsync(postgres.ConnectionString);
        var metadata = await f.Metadata();
        using var body = f.Parts(metadata, mode);
        using var response = await f.Send(body);
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        await using var db = f.Recording.Capture.Data.Database.CreateContext();
        Assert.False(await db.Set<AttendanceEvent>().AnyAsync());
    }

    [Fact]
    public async Task ForgedCsrfCannotCreateChallenge()
    {
        await using var f = await RecordingHttpFixture.CreateAsync(postgres.ConnectionString);
        using var request = IdentityTestDriver.Mutation("forged", "/api/v1/attendance/capture-challenges");
        request.Content = JsonContent.Create(new { action = "CheckIn", site = CaptureFixture.Site, idempotencyKey = Guid.NewGuid() });
        using var response = await f.Client.SendAsync(request); Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var db = f.Recording.Capture.Data.Database.CreateContext(); Assert.False(await db.Set<CaptureChallenge>().AnyAsync());
    }

    [Fact]
    public async Task UnauthenticatedHistoryCannotReadAndAdminIsNotImplicitHr()
    {
        await using var f = await RecordingHttpFixture.CreateAsync(postgres.ConnectionString);
        using var anonymous = f.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443") });
        using var denied = await anonymous.GetAsync("/api/v1/attendance/current"); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var hr = await f.Client.GetAsync("/api/v1/attendance/hr?from=2026-09-01&to=2026-09-26"); Assert.Equal(HttpStatusCode.Forbidden, hr.StatusCode);
        using var unknown = await f.Client.GetAsync($"/api/v1/attendance/days/{Guid.NewGuid()}"); Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Theory]
    [InlineData("ownerId=x")]
    [InlineData("action=CheckIn&action=CheckOut")]
    [InlineData("action=0")]
    public async Task OperationReadRejectsUnknownOrAmbiguousQuery(string query)
    {
        await using var f = await RecordingHttpFixture.CreateAsync(postgres.ConnectionString);
        using var response = await f.Client.GetAsync($"/api/v1/attendance/operations/{Guid.NewGuid()}?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task StorageUnavailableIs503NotLogout()
    {
        await using var f = await RecordingHttpFixture.CreateAsync(postgres.ConnectionString);
        var metadata = await f.Metadata();
        await using var db = f.Recording.Capture.Data.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("UPDATE attendance_storage_locations SET accept_writes=false,version=version+1");
        using var response = await f.Upload(metadata); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(x => x.Contains("max-age=0")));
        using var current = await f.Client.GetAsync("/api/v1/attendance/current"); Assert.Equal(HttpStatusCode.OK, current.StatusCode);
    }
}

[UnsupportedOSPlatform("windows")]
internal sealed class RecordingHttpFixture(OnlineRecordingFixture recording, ApiFactory factory, HttpClient client) : IAsyncDisposable
{
    internal OnlineRecordingFixture Recording { get; } = recording;
    internal ApiFactory Factory { get; } = factory;
    internal HttpClient Client { get; } = client;
    internal static async Task<RecordingHttpFixture> CreateAsync(string connection, Action<IServiceCollection>? configure = null)
    {
        var recording = await OnlineRecordingFixture.CreateAsync(connection);
        var factory = new ApiFactory(recording.Capture.Data.Database.ConnectionString, clock: recording.Capture.Clock, settings: recording.Storage.Settings, configureServices: configure);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost:4443"), AllowAutoRedirect = false });
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        await using var db = recording.Capture.Data.Database.CreateContext();
        var session = await db.Set<IdentitySession>().SingleAsync(x => x.Id == CaptureFixture.Session);
        session.TokenHash = SessionService.HashToken(token)!; await db.SaveChangesAsync();
        client.DefaultRequestHeaders.Add("Cookie", CsrfService.SessionCookieName + "=" + token);
        var runtime = factory.Services.GetRequiredService<StorageRuntime>();
        runtime.Remember(recording.Location, await runtime.Resolve(recording.Location).ProbeAsync(default));
        return new(recording, factory, client);
    }
    internal async Task<CaptureMetadata> Metadata(string action = "CheckIn")
    {
        var key = Guid.NewGuid();
        using var request = IdentityTestDriver.Mutation(await IdentityTestDriver.TokenAsync(Client), "/api/v1/attendance/capture-challenges");
        request.Content = JsonContent.Create(new { action, site = CaptureFixture.Site, idempotencyKey = key });
        using var response = await Client.SendAsync(request); Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var challenge = await response.Content.ReadFromJsonAsync<CaptureChallengeView>(); Assert.NotNull(challenge);
        var now = Recording.Capture.Clock.GetUtcNow();
        return new(challenge.Id, key, CaptureFixture.Site, now, new(13, 100, 10, now), false, action == "CheckOut" ? 1 : null);
    }
    internal MultipartFormDataContent Parts(CaptureMetadata metadata, string mode = "valid")
    {
        var body = new MultipartFormDataContent();
        var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (mode == "unknown-json") json = json[..^1] + ",\"actorId\":\"unexpected\"}";
        if (mode == "duplicate-json") json = json[..^1] + ",\"challengeId\":\"" + metadata.ChallengeId + "\"}";
        if (mode == "large-metadata") json = new string(' ', 16385);
        if (mode != "missing-metadata") body.Add(new StringContent(json, Encoding.UTF8, "application/json"), "metadata");
        if (mode == "duplicate-metadata") body.Add(new StringContent(json, Encoding.UTF8, "application/json"), "metadata");
        var bytes = mode switch
        {
            "large-photo" => new byte[10 * 1024 * 1024 + 1],
            "fake-jpeg" => Encoding.UTF8.GetBytes("not-a-jpeg"),
            // PNG with valid CRC, exactly 20,000,001 declared pixels; rejected before allocation.
            "huge-pixels" => StampFixtures.Invalid("pixel-limit"),
            _ => EvidencePublicationTests.Photo()
        };
        if (mode != "missing-photo")
        {
            var photo = new ByteArrayContent(bytes); photo.Headers.ContentType = new("image/jpeg");
            if (mode == "base64") photo.Headers.Add("Content-Transfer-Encoding", "base64");
            body.Add(photo, "photo", "capture.jpg");
        }
        if (mode == "duplicate-photo") body.Add(new ByteArrayContent(bytes), "photo", "second.jpg");
        if (mode == "extra") body.Add(new StringContent("no"), "url");
        return body;
    }
    internal async Task<HttpResponseMessage> Upload(CaptureMetadata metadata, string action = "check-ins")
    { using var body = Parts(metadata); return await Send(body, action); }
    internal async Task<HttpResponseMessage> Send(HttpContent content, string action = "check-ins")
    {
        using var request = IdentityTestDriver.Mutation(await IdentityTestDriver.TokenAsync(Client), "/api/v1/attendance/" + action);
        request.Content = content; return await Client.SendAsync(request);
    }
    public async ValueTask DisposeAsync() { Client.Dispose(); await Factory.DisposeAsync(); await Recording.DisposeAsync(); }
}
