using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace TPR10.Api.Attendance.Storage;

public sealed class StorageRuntime
{
    private readonly IOptionsMonitor<StorageOptions> options;
    private readonly TimeProvider clock;
    private readonly Func<Guid, StorageDefinition, IStorageAdapter> adapterFactory;
    public StorageRuntime(IOptionsMonitor<StorageOptions> options, TimeProvider clock)
        : this(options, clock, (id, definition) => new FolderStorageAdapter(id, definition, clock)) { }
    internal StorageRuntime(IOptionsMonitor<StorageOptions> options, TimeProvider clock, Func<Guid, StorageDefinition, IStorageAdapter> adapterFactory)
    { this.options = options; this.clock = clock; this.adapterFactory = adapterFactory; }
    private readonly ConcurrentDictionary<Guid, (string Fingerprint, IStorageAdapter Adapter)> adapters = new();
    private readonly ConcurrentDictionary<Guid, Receipt> receipts = new();
    private sealed record Receipt(long Version, string Fingerprint, StorageHealthView Health);
    internal SemaphoreSlim ScanGate { get; } = new(1, 1);
    internal SemaphoreSlim RefreshGate { get; } = new(1, 1);
    internal Guid? ScanStorageAfter { get; set; }
    internal ConcurrentDictionary<Guid, ManifestCursor> Scans { get; } = new();
    internal sealed record ManifestCursor(Guid? After, int Missing, int CompletedMissing, int Orphans, bool InProgress);

    public StorageDefinition[] Definitions()
    {
        var definitions = options.CurrentValue.Locations?.ToArray() ?? throw new IOException("storage-config-invalid");
        if (definitions.Any(x => x is null || !ValidAlias(x.Alias) || x.Kind is not ("local-folder" or "nas-mounted-folder")
            || string.IsNullOrWhiteSpace(x.RootPath) || string.IsNullOrWhiteSpace(x.ExpectedVolumeId) || x.MarkerId == Guid.Empty)
            || definitions.Select(x => x.Alias).Distinct(StringComparer.Ordinal).Count() != definitions.Length)
            throw new IOException("storage-config-invalid");
        return definitions;
    }

    internal static bool ValidAlias(string? value) => value is { Length: > 0 and <= 100 }
        && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
        && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    internal static string Fingerprint(StorageDefinition definition) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(definition)));

    public StorageDefinition? Find(string alias) => Definitions().SingleOrDefault(x => x.Alias == alias);

    public bool Matches(StorageLocation row)
    {
        var definition = Find(row.Alias);
        return definition is not null && definition.Kind == row.Kind && Fingerprint(definition) == row.ConfigFingerprint;
    }

    public IStorageAdapter Resolve(StorageLocation row)
    {
        var definition = Find(row.Alias);
        if (definition is null || definition.Kind != row.Kind || Fingerprint(definition) != row.ConfigFingerprint)
            throw new IOException("storage-config-changed");
        var entry = adapters.GetOrAdd(row.Id, _ => (row.ConfigFingerprint, adapterFactory(row.Id, definition)));
        if (entry.Fingerprint != row.ConfigFingerprint) throw new IOException("storage-config-changed");
        return entry.Adapter;
    }

    public StorageHealthView Health(StorageLocation row)
    {
        if (!Matches(row)) return MergeIntegrity(new(row.Id, "unavailable", null, null, 0, 0, row.CheckedAtUtc ?? clock.GetUtcNow(), "storage-config-changed"));
        if (receipts.TryGetValue(row.Id, out var receipt) && receipt.Version == row.Version && receipt.Fingerprint == row.ConfigFingerprint
            && receipt.Health.CheckedAtUtc <= clock.GetUtcNow() && receipt.Health.CheckedAtUtc > clock.GetUtcNow().AddSeconds(-60))
            return MergeIntegrity(receipt.Health);
        return MergeIntegrity(new(row.Id, "unknown", row.FreeBytes, row.TotalBytes, 0, 0, row.CheckedAtUtc ?? clock.GetUtcNow(), "readiness-unknown"));
    }

    internal StorageHealthView MergeIntegrity(StorageHealthView health)
    {
        var state = Scans.GetValueOrDefault(health.StorageId);
        var missing = state is null ? 0 : state.InProgress ? Math.Max(state.CompletedMissing, state.Missing) : state.CompletedMissing;
        var orphans = state?.Orphans ?? 0;
        var writable = health.Status is "healthy" or "ready" or "warning";
        return health with
        {
            MissingObjects = missing,
            OrphanObjects = orphans,
            Status = writable && (missing > 0 || orphans > 0) ? "warning" : health.Status == "healthy" ? "ready" : health.Status,
            ErrorCode = !writable ? health.ErrorCode ?? (health.Status == "unknown" ? "capacity-unknown" : "storage-unavailable")
                : missing > 0 ? "manifest-copy-unverified" : orphans > 0 ? "orphan-evidence" : state?.InProgress == true ? "manifest-scan-in-progress" : health.ErrorCode
        };
    }

    public bool Ready(StorageLocation row) => row.AcceptWrites && Health(row).Status is "ready" or "warning";
    internal void Remember(StorageLocation row, StorageHealthView health) => receipts[row.Id] = new(row.Version, row.ConfigFingerprint, health);
    internal void Invalidate(Guid id) => receipts.TryRemove(id, out _);
}
