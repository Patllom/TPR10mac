using System.Collections.Concurrent;

namespace TPR10.Api.Attendance.Storage;

public sealed class StorageRootResolver
{
    private readonly IReadOnlyDictionary<string, StorageDefinition> definitions;
    private readonly ConcurrentDictionary<Guid, (string Alias, IStorageAdapter Adapter)> bindings = new();
    private readonly TimeProvider clock;

    public StorageRootResolver(IEnumerable<StorageDefinition> definitions, TimeProvider clock)
    {
        var entries = definitions.ToArray();
        if (entries.Any(d => string.IsNullOrWhiteSpace(d.Alias) || d.MarkerId == Guid.Empty ||
            d.Kind is not ("local-folder" or "nas-mounted-folder")) || entries.Select(d => d.Alias).Distinct(StringComparer.Ordinal).Count() != entries.Length)
            throw new IOException("storage-config-invalid");
        this.definitions = entries.ToDictionary(d => d.Alias, StringComparer.Ordinal);
        this.clock = clock;
    }

    public IStorageAdapter Resolve(Guid storageId, string alias)
    {
        if (storageId == Guid.Empty || !definitions.TryGetValue(alias, out var definition)) throw new IOException("storage-alias-unknown");
        var binding = bindings.GetOrAdd(storageId, _ => (alias, new FolderStorageAdapter(storageId, definition, clock)));
        if (binding.Alias != alias) throw new IOException("storage-binding-changed");
        return binding.Adapter;
    }
}
