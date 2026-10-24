using System.Security.Cryptography;
using TPR10.Api.FieldAdvances.Payments;

namespace TPR10.Api.FieldAdvances.Evidence;

public sealed class FolderFinanceEvidenceStore(FinanceEvidenceOptions options) : IFinanceEvidenceStore
{
    public async Task<EvidenceTicket> WriteImmutableAsync(Guid storageId, string key, Stream content, CancellationToken ct)
    {
        options.Validate(); ct.ThrowIfCancellationRequested();
        await using var file = EvidenceFileHandles.Open(Root(storageId), key, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536]; long length = 0;
        int read;
        while ((read = await content.ReadAsync(buffer, ct)) != 0)
        {
            length = checked(length + read);
            if (length > options.MaxFileBytes) throw new IOException("Evidence exceeds the byte limit.");
            hash.AppendData(buffer, 0, read); await file.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        if (length == 0) throw new IOException("Empty evidence.");
        await file.FlushAsync(ct); file.Flush(true);
        return new(Guid.ParseExact(key, "N"), Convert.ToHexStringLower(hash.GetHashAndReset()), length, "application/octet-stream", "Ready");
    }
    public async Task<byte[]> ReadVerifiedAsync(Guid storageId, string key, string sha256, long length, CancellationToken ct)
    {
        options.Validate();
        if (length <= 0 || length > options.MaxFileBytes || sha256 is not { Length: 64 }) throw new IOException("Invalid evidence metadata.");
        await using var file = EvidenceFileHandles.Open(Root(storageId), key, false);
        if (file.Length != length) throw new IOException("Evidence length mismatch.");
        var bytes = new byte[(int)length]; await file.ReadExactlyAsync(bytes, ct);
        if (file.ReadByte() != -1 || Convert.ToHexStringLower(SHA256.HashData(bytes)) != sha256) throw new IOException("Evidence digest mismatch.");
        return bytes;
    }
    public IEnumerable<EvidenceLocator> Enumerate()
    {
        options.Validate();
        foreach (var root in options.Roots)
        {
            foreach (var name in EvidenceFileHandles.Names(root.Value)) yield return new(root.Key, name);
        }
    }
    private string Root(Guid id) => id != Guid.Empty && options.Roots.TryGetValue(id, out var root) ? root : throw new IOException("Unknown storage identifier.");
}
