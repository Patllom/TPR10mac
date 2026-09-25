namespace TPR10.Api.Attendance.Storage;

public interface IStorageAdapter
{
    Task<StoredCopy> WriteImmutableAsync(string key, ReadOnlyMemory<byte> bytes, CancellationToken ct);
    Task<byte[]> ReadVerifiedAsync(string key, string sha256, long length, CancellationToken ct);
    Task<StorageHealthView> ProbeAsync(CancellationToken ct);
}
