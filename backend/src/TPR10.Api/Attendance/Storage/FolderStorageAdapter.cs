using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using TPR10.Api.Attendance.Evidence;

namespace TPR10.Api.Attendance.Storage;

public sealed class FolderStorageAdapter : IStorageAdapter
{
    private readonly StorageDefinition definition;
    private readonly SafeFileHandles fs;
    private readonly Guid storageId;
    private readonly TimeProvider clock;
    private readonly object identityLock = new();
    private FileIdentity? pinnedRoot;

    public FolderStorageAdapter(Guid storageId, StorageDefinition definition, TimeProvider timeProvider)
        : this(storageId, definition, timeProvider, new SafeFileHandles()) { }

    internal FolderStorageAdapter(Guid storageId, StorageDefinition definition, TimeProvider timeProvider, SafeFileHandles fs)
    {
        if (storageId == Guid.Empty) throw new IOException("storage-binding-invalid");
        ArgumentNullException.ThrowIfNull(timeProvider);
        this.definition = definition;
        this.fs = fs;
        this.storageId = storageId;
        clock = timeProvider;
    }

    public Task<StorageHealthView> ProbeAsync(CancellationToken ct) => StorageWorkBudget.Run(clock, ct, token =>
    {
        token.ThrowIfCancellationRequested();
        using var root = Acquire(write: true);
        var name = ".probe-" + Guid.NewGuid().ToString("N");
        var final = name + ".ready";
        var owned = false;
        var renamed = false;
        try
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            using (var file = fs.OpenFile(root.Handle, name, create: true))
            {
                owned = true;
                root.ValidateChild(file, directory: false);
                fs.Write(file, bytes, 0);
                fs.Sync(file);
            }
            token.ThrowIfCancellationRequested();
            root.Validate();
            if (!fs.RenameNoReplace(root.Handle, name, final)) throw new IOException("storage-probe-collision");
            renamed = true;
            fs.Sync(root.Handle);
            using var published = fs.OpenFile(root.Handle, final, create: false);
            _ = ReadCopy(root, published, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, token);
            root.Validate();
            var capacity = fs.Capacity(root.Handle);
            var status = capacity.Free is null or < 0 || capacity.Total is null or <= 0 || capacity.Free > capacity.Total
                ? "unknown"
                : capacity.Free < 10L * 1024 * 1024 * 1024 || (decimal)capacity.Free < (decimal)capacity.Total / 10
                    ? "warning" : "healthy";
            return new StorageHealthView(storageId, status, capacity.Free, capacity.Total, 0, 0, clock.GetUtcNow());
        }
        finally
        {
            // Only this operation's random synthetic probe can be removed; never evidence/staging.
            if (owned) { fs.RemoveProbe(root.Handle, renamed ? final : name); fs.Sync(root.Handle); }
        }
    });

    public Task<StoredCopy> WriteImmutableAsync(string key, ReadOnlyMemory<byte> bytes, CancellationToken ct) => StorageWorkBudget.Run(clock, ct, token =>
    {
        token.ThrowIfCancellationRequested();
        var parts = StorageObjectKey.Parse(key);
        if (bytes.Length is <= 0 or > ImageLimits.MaxBytes) throw new IOException("storage-length-invalid");
        var owned = bytes.ToArray();
        var sha = Convert.ToHexStringLower(SHA256.HashData(owned));
        using var root = Acquire(write: true);
        using var parent = OpenParent(root, parts, create: true);
        using var existing = TryOpen(parent, parts[3]);
        if (existing is not null)
        {
            _ = ReadCopy(root, existing, sha, owned.Length, token);
            fs.Sync(existing);
            fs.Sync(parent);
            ValidateObjectPath(root, parts, parent, existing);
            root.Validate();
            return new StoredCopy(sha, owned.Length);
        }
        var partial = ".partial-" + Guid.NewGuid().ToString("N");
        ValidateObjectPath(root, parts, parent);
        using (var file = fs.OpenFile(parent, partial, create: true))
        {
            root.ValidateChild(file, directory: false);
            fs.Write(file, owned, 0);
            fs.Sync(file);
        }
        token.ThrowIfCancellationRequested();
        root.Validate();
        ValidateObjectPath(root, parts, parent);
        fs.RenameNoReplace(parent, partial, parts[3]);
        fs.Sync(parent);
        using var published = fs.OpenFile(parent, parts[3], create: false);
        _ = ReadCopy(root, published, sha, owned.Length, token);
        ValidateObjectPath(root, parts, parent, published);
        root.Validate();
        return new StoredCopy(sha, owned.Length);
    });

    public Task<byte[]> ReadVerifiedAsync(string key, string sha256, long length, CancellationToken ct) => StorageWorkBudget.Run(clock, ct, token =>
    {
        token.ThrowIfCancellationRequested();
        var parts = StorageObjectKey.Parse(key);
        using var root = Acquire(write: false);
        using var parent = OpenParent(root, parts, create: false);
        using var file = fs.OpenFile(parent, parts[3], create: false);
        var bytes = ReadCopy(root, file, sha256, length, token);
        ValidateObjectPath(root, parts, parent, file);
        root.Validate();
        return bytes;
    });

    private StorageRootLease Acquire(bool write)
    {
        var root = new StorageRootLease(fs, definition, write);
        lock (identityLock)
        {
            if (pinnedRoot is not null && !pinnedRoot.SameNode(root.Identity))
            {
                root.Dispose();
                throw new IOException("storage-identity-changed");
            }
            pinnedRoot ??= root.Identity;
        }
        return root;
    }

    private SafeFileHandle OpenParent(StorageRootLease root, string[] parts, bool create)
    {
        var current = fs.OpenDirectory(root.Handle, parts[0], create);
        try
        {
            if (create) fs.Sync(root.Handle);
            root.ValidateChild(current, directory: true);
            foreach (var part in parts.Skip(1).Take(2))
            {
                var next = fs.OpenDirectory(current, part, create);
                try { if (create) fs.Sync(current); }
                catch { next.Dispose(); throw; }
                current.Dispose();
                current = next;
                root.ValidateChild(current, directory: true);
            }
            return current;
        }
        catch { current.Dispose(); throw; }
    }

    private SafeFileHandle? TryOpen(SafeFileHandle parent, string name)
    {
        try { return fs.OpenFile(parent, name, false); }
        catch (IOException error) when (error.Message == "storage-io-failed:2") { return null; }
    }

    private void ValidateObjectPath(StorageRootLease root, string[] parts, SafeFileHandle parent, SafeFileHandle? file = null)
    {
        using var current = OpenParent(root, parts, create: false);
        if (!fs.Inspect(current).SameNode(fs.Inspect(parent))) throw new IOException("storage-identity-changed");
        if (file is null) return;
        using var currentFile = fs.OpenFile(current, parts[3], create: false);
        root.ValidateChild(currentFile, directory: false);
        if (!fs.Inspect(currentFile).SameNode(fs.Inspect(file))) throw new IOException("storage-identity-changed");
    }

    private byte[] ReadCopy(StorageRootLease root, SafeFileHandle file, string sha256, long length, CancellationToken ct)
    {
        if (length is <= 0 or > ImageLimits.MaxBytes || sha256.Length != 64 || sha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new IOException("storage-metadata-invalid");
        root.ValidateChild(file, directory: false);
        var before = fs.Inspect(file);
        if (before.Length != length) throw new IOException("storage-checksum-mismatch");
        var bytes = new byte[(int)length];
        var position = 0;
        while (position < bytes.Length)
        {
            ct.ThrowIfCancellationRequested();
            var count = fs.Read(file, bytes.AsSpan(position), position);
            if (count == 0) throw new IOException("storage-read-incomplete");
            position += count;
        }
        if (fs.Read(file, new byte[1], length) != 0 || fs.Inspect(file).Length != length ||
            Convert.ToHexStringLower(SHA256.HashData(bytes)) != sha256)
            throw new IOException("storage-checksum-mismatch");
        return bytes;
    }
}
