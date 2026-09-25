using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TPR10.Api.Attendance.Storage;

public sealed record MountIdentity(string VolumeId, bool IsNetwork, string InstanceId)
{
    // Deployment diagnostic only: no path or mounted source is returned to HTTP clients.
    public static string GetVolumeId(string root)
    {
        var fs = new SafeFileHandles();
        using var handle = fs.OpenRoot(root);
        return Capture(fs, handle).VolumeId;
    }

    internal static MountIdentity Capture(SafeFileHandles fs, SafeFileHandle handle)
    {
        var status = fs.Inspect(handle);
        if (OperatingSystem.IsLinux())
        {
            foreach (var line in File.ReadLines("/proc/self/mountinfo"))
            {
                var fields = line.Split(' ');
                if (fields.Length < 10 || fields[0] != status.MountId.ToString(System.Globalization.CultureInfo.InvariantCulture)) continue;
                var separator = Array.IndexOf(fields, "-");
                if (separator < 6 || separator + 3 >= fields.Length) break;
                if (fields[2] != $"{status.Device >> 32}:{status.Device & uint.MaxValue}") break;
                var type = fields[separator + 1];
                var source = fields[separator + 2];
                return new(Fingerprint($"linux|{fields[2]}|{type}|{source}"), type is "nfs" or "nfs4" or "cifs" or "smb3", fields[0]);
            }
            throw new IOException("storage-mount-unavailable");
        }
        var mount = fs.DarwinMount(handle);
        var fsid = SafeFileHandles.U64(mount, 48);
        var fsType = Text(mount, 72, 16);
        var mountedSource = Text(mount, 1112, 1024);
        var network = (SafeFileHandles.U32(mount, 64) & 0x1000) == 0 && fsType is "smbfs" or "nfs";
        return new(Fingerprint($"darwin|{status.Device}|{fsid}|{fsType}|{mountedSource}"), network, fsid.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string Fingerprint(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Text(byte[] value, int offset, int count) => Encoding.UTF8.GetString(value, offset, count).TrimEnd('\0');
}
