using System.Runtime.Versioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Attendance.Recording;
using TPR10.Api.Attendance.Storage;
using TPR10.Api.Identity.Authorization;

namespace TPR10.Api.IntegrationTests;

[UnsupportedOSPlatform("windows")]
internal sealed class OnlineRecordingFixture(CaptureFixture capture, StorageTestFixture storage, RecordingStamp images) : IAsyncDisposable
{
    internal CaptureFixture Capture { get; } = capture;
    internal StorageTestFixture Storage { get; } = storage;
    internal RecordingStamp Images { get; } = images;
    internal StorageLocation Location { get; private set; } = null!;
    internal static async Task<OnlineRecordingFixture> CreateAsync(string connection, Action<IServiceCollection>? configure = null)
    {
        var storage = new StorageTestFixture();
        var images = new RecordingStamp();
        CaptureFixture? capture = null;
        try
        {
            capture = await CaptureFixture.CreateAsync(connection, services =>
            {
                services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(storage.Settings).Build());
                services.AddScoped<PermissionContext>(); services.AddScoped<PermissionMutationGuard>();
                services.AddSingleton<IImageStampService>(images);
                configure?.Invoke(services);
            });
            images.Inner = new ImageStampService(capture.Clock);
            var f = new OnlineRecordingFixture(capture, storage, images);
            var runtime = capture.Services.GetRequiredService<StorageRuntime>();
            var definition = runtime.Find("local-0")!;
            f.Location = new StorageLocation
            {
                Id = Guid.NewGuid(),
                Alias = definition.Alias,
                Kind = definition.Kind,
                ConfigFingerprint = StorageRuntime.Fingerprint(definition),
                AcceptWrites = true,
                CreatedAtUtc = capture.Clock.GetUtcNow()
            };
            await using var db = capture.Data.Database.CreateContext();
            db.Add(f.Location);
            db.Add(new StorageWriteTarget { StorageId = f.Location.Id });
            await db.SaveChangesAsync(); await f.RefreshAsync();
            return f;
        }
        catch { if (capture is not null) await capture.DisposeAsync(); storage.Dispose(); throw; }
    }
    internal async Task RefreshAsync()
    {
        await using var db = Capture.Data.Database.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sessions SET last_seen_at_utc={Capture.Clock.GetUtcNow()},expires_at_utc={Capture.Clock.GetUtcNow().AddDays(2)}");
        var runtime = Capture.Services.GetRequiredService<StorageRuntime>();
        runtime.Remember(Location, await runtime.Resolve(Location).ProbeAsync(default));
    }
    internal async Task<RecordingResult<CaptureReceipt>> Record(EvidenceAction action, CaptureMetadata metadata, byte[]? bytes = null)
    {
        await using var scope = await Capture.ScopeAsync();
        using var photo = new MemoryStream(bytes ?? EvidencePublicationTests.Photo(), writable: false);
        return await CaptureFixture.Call<RecordingResult<CaptureReceipt>>(scope.ServiceProvider, "AttendanceRecorder", "RecordAsync",
            action, metadata, photo, CancellationToken.None);
    }
    internal async Task<CaptureReceipt> CheckIn()
    {
        var result = await Record(EvidenceAction.CheckIn, await Capture.Metadata());
        Assert.Equal(201, result.Status); return result.Value!;
    }
    public async ValueTask DisposeAsync() { await Capture.DisposeAsync(); Storage.Dispose(); }
}

internal sealed class RecordingStamp : IImageStampService
{
    internal IImageStampService Inner { get; set; } = null!;
    internal Func<Task>? Before { get; set; }
    public async Task<StampedImage> StampAsync(Stream source, StampRequest stamp, CancellationToken ct)
    { if (Before is { } before) await before(); return await Inner.StampAsync(source, stamp, ct); }
    public StampedImage Thumbnail(StampedImage full) => Inner.Thumbnail(full);
}
