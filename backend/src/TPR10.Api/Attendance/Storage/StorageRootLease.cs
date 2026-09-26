using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TPR10.Api.Attendance.Storage;

internal sealed class StorageRootLease : IDisposable
{
    private readonly SafeFileHandles fs;
    private readonly StorageDefinition definition;
    private readonly bool write;
    private readonly FileIdentity markerIdentity;
    private readonly MountIdentity mount;
    public SafeFileHandle Handle { get; }
    public FileIdentity Identity { get; }

    public StorageRootLease(SafeFileHandles fs, StorageDefinition definition, bool write)
    {
        this.fs = fs;
        this.definition = definition;
        this.write = write;
        Handle = fs.OpenRoot(definition.RootPath);
        try
        {
            Identity = fs.Inspect(Handle);
            SafeFileHandles.RequirePrivate(Identity, directory: true, write);
            fs.RequirePrivateAcl(Handle);
            mount = MountIdentity.Capture(fs, Handle);
            ValidateMount(mount);
            markerIdentity = ReadMarker(Handle);
        }
        catch { Handle.Dispose(); throw; }
    }

    public void Validate()
    {
        using var current = fs.OpenRoot(definition.RootPath);
        var now = fs.Inspect(current);
        SafeFileHandles.RequirePrivate(now, directory: true, write);
        fs.RequirePrivateAcl(current);
        if (!Identity.SameNode(now) || MountIdentity.Capture(fs, current) != mount || !markerIdentity.SameNode(ReadMarker(current)))
            throw new IOException("storage-identity-changed");
        ValidateMount(MountIdentity.Capture(fs, Handle));
        if (!Identity.SameNode(fs.Inspect(Handle))) throw new IOException("storage-identity-changed");
    }

    public void ValidateChild(SafeFileHandle child, bool directory)
    {
        var identity = fs.Inspect(child);
        SafeFileHandles.RequirePrivate(identity, directory, write && directory);
        fs.RequirePrivateAcl(child);
        if (identity.Device != Identity.Device || identity.MountId != Identity.MountId)
            throw new IOException("storage-volume-changed");
    }

    private void ValidateMount(MountIdentity current)
    {
        if (definition.Kind is not ("local-folder" or "nas-mounted-folder") || current.VolumeId != definition.ExpectedVolumeId ||
            (definition.Kind == "nas-mounted-folder") != current.IsNetwork)
            throw new IOException("storage-mount-unavailable");
    }

    private FileIdentity ReadMarker(SafeFileHandle root)
    {
        using var file = fs.OpenFile(root, ".tpr10-storage-id", false);
        var info = fs.Inspect(file);
        SafeFileHandles.RequirePrivate(info, directory: false);
        fs.RequirePrivateAcl(file);
        if (info.Length != 36 || info.Device != Identity.Device || info.MountId != Identity.MountId)
            throw new IOException("storage-marker-invalid");
        var bytes = new byte[37];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = fs.Read(file, bytes.AsSpan(count), count);
            if (read == 0) break;
            count += read;
        }
        if (count != 36 || Encoding.ASCII.GetString(bytes, 0, count) != definition.MarkerId.ToString("D"))
            throw new IOException("storage-marker-invalid");
        return info;
    }

    public void Dispose() => Handle.Dispose();
}
