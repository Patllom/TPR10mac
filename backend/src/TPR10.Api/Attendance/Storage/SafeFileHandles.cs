using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TPR10.Api.Attendance.Storage;

internal sealed record FileIdentity(ulong Device, ulong Inode, uint Mode, uint Owner, uint Links, long Length, ulong MountId)
{
    public bool SameNode(FileIdentity other) => Device == other.Device && Inode == other.Inode && MountId == other.MountId;
}

// All path components are opened individually relative to a live directory descriptor.
// The explicit status layouts are limited to 64-bit Linux statx and Darwin stat64 ABIs.
internal class SafeFileHandles
{
    private static bool Mac => OperatingSystem.IsMacOS();
    private static bool MacArm => Mac && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
    private static bool Arm64 => RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
    private static int DirectoryFlags => Mac ? 0x100000 : Arm64 ? 0x4000 : 0x10000;
    private static int SafeFlags => Mac ? 0x100 | 0x1000000 | 4 : (Arm64 ? 0x8000 : 0x20000) | 0x80000 | 0x800;
    internal static uint CurrentUser => geteuid();

    public static void RequireSupported()
    {
        if ((!OperatingSystem.IsLinux() && !Mac) || RuntimeInformation.ProcessArchitecture is not (Architecture.Arm64 or Architecture.X64))
            throw new IOException("storage-runtime-unsupported");
    }

    public SafeFileHandle OpenRoot(string path)
    {
        RequireSupported();
        if (!path.StartsWith('/') || path == "/" || path.EndsWith('/') || path.Contains('\\') || path.Contains('\0'))
            throw new IOException("storage-root-invalid");
        var parts = path[1..].Split('/');
        if (parts.Any(p => p is "" or "." or "..")) throw new IOException("storage-root-invalid");
        var current = OpenAt(Mac ? -2 : -100, "/", SafeFlags | DirectoryFlags, 0);
        try
        {
            foreach (var part in parts)
            {
                var next = OpenDirectory(current, part, false);
                current.Dispose();
                current = next;
            }
            return current;
        }
        catch { current.Dispose(); throw; }
    }

    public virtual SafeFileHandle OpenDirectory(SafeFileHandle parent, string name, bool create)
    {
        if (create && mkdirat(parent, name, 448) != 0 && Marshal.GetLastPInvokeError() != 17) Fail();
        return OpenAt(Fd(parent), name, SafeFlags | DirectoryFlags, 0);
    }

    public virtual SafeFileHandle OpenFile(SafeFileHandle parent, string name, bool create)
    {
        var flags = SafeFlags | (create ? 1 | (Mac ? 0x200 | 0x800 : 0x40 | 0x80) : 0);
        return OpenAt(Fd(parent), name, flags, create ? 384u : 0u);
    }

    private static SafeFileHandle OpenAt(int parent, string name, int flags, uint mode)
    {
        // Darwin arm64 passes variadic mode on the stack, unlike fixed arguments.
        var fd = MacArm ? openat_arm64(parent, name, flags, 0, 0, 0, 0, 0, mode) : openat(parent, name, flags, mode);
        if (fd < 0) Fail();
        return new SafeFileHandle((IntPtr)fd, ownsHandle: true);
    }

    public virtual FileIdentity Inspect(SafeFileHandle handle)
    {
        RequireSupported();
        var data = new byte[256];
        if (Mac)
        {
            if ((MacArm ? fstat(handle, data) : fstat_inode64(handle, data)) != 0) Fail();
            return new(U32(data, 0), U64(data, 8), U16(data, 4), U32(data, 16), U16(data, 6), I64(data, 96), 0);
        }
        if (statx(handle, "", 0x1000, 0x17ff, data) != 0) Fail();
        if ((U32(data, 0) & 0x171f) != 0x171f) throw new IOException("storage-runtime-unsupported");
        return new(((ulong)U32(data, 136) << 32) | U32(data, 140), U64(data, 32), U16(data, 28), U32(data, 20), U32(data, 16), I64(data, 40), U64(data, 144));
    }

    public virtual byte[] DarwinMount(SafeFileHandle handle)
    {
        var buffer = new byte[2168];
        if ((MacArm ? fstatfs(handle, buffer) : fstatfs_inode64(handle, buffer)) != 0) Fail();
        return buffer;
    }

    public virtual int Read(SafeFileHandle file, Span<byte> bytes, long offset) => RandomAccess.Read(file, bytes, offset);
    public virtual void Write(SafeFileHandle file, ReadOnlySpan<byte> bytes, long offset) => RandomAccess.Write(file, bytes, offset);
    public virtual void Sync(SafeFileHandle file) { if (fsync(file) != 0) Fail(); }

    public virtual (long? Free, long? Total) Capacity(SafeFileHandle root)
    {
        var data = Mac ? DarwinMount(root) : new byte[256];
        if (!Mac && fstatvfs(root, data) != 0) return (null, null);
        var blockSize = Mac ? U32(data, 0) : U64(data, 8);
        var totalBlocks = U64(data, Mac ? 8 : 16);
        var available = U64(data, Mac ? 24 : 32);
        if (blockSize == 0 || totalBlocks > long.MaxValue / blockSize || available > long.MaxValue / blockSize) return (null, null);
        return ((long)(available * blockSize), (long)(totalBlocks * blockSize));
    }

    public virtual void RemoveProbe(SafeFileHandle root, string name)
    {
        if (!name.StartsWith(".probe-", StringComparison.Ordinal) || name.Contains('/') || name.Contains('\\'))
            throw new IOException("storage-probe-invalid");
        if (unlinkat(root, name, 0) != 0) Fail();
    }

    public virtual bool RenameNoReplace(SafeFileHandle directory, string source, string destination)
    {
        var result = Mac ? renameatx_np(directory, source, directory, destination, 4) : renameat2(directory, source, directory, destination, 1);
        if (result == 0) return true;
        if (Marshal.GetLastPInvokeError() == 17) return false;
        Fail();
        return false;
    }

    public static void RequirePrivate(FileIdentity identity, bool directory, bool write = false)
    {
        var type = directory ? 0x4000u : 0x8000u;
        var required = directory ? (write ? 448u : 320u) : 256u;
        if ((identity.Mode & 0xf000) != type || identity.Owner != CurrentUser || (identity.Mode & 63) != 0 ||
            (identity.Mode & required) != required || (!directory && identity.Links != 1))
            throw new IOException("storage-permission-invalid");
    }

    public void RequirePrivateAcl(SafeFileHandle handle)
    {
        // Darwin extended ACLs can grant access beyond private BSD mode bits.
        // Reject every extended entry, including inherited/deny entries, rather than
        // guessing effective access. An unsupported ACL query also fails closed.
        if (!Mac) return;
        var acl = acl_get_fd_np(handle, 0x100);
        if (acl == IntPtr.Zero)
        {
            // filesec_get_property(FILESEC_ACL) reports ENOENT when no ACL exists
            // on this already-open descriptor. Other errors are not absence.
            if (Marshal.GetLastPInvokeError() == 2) return;
            Fail();
        }
        try
        {
            if (acl_get_entry(acl, 0, out _) == 0) throw new IOException("storage-acl-unsupported");
            if (Marshal.GetLastPInvokeError() != 22) Fail(); // Darwin reports EINVAL for an empty ACL.
        }
        finally { acl_free(acl); }
    }

    private static int Fd(SafeFileHandle handle) => checked((int)handle.DangerousGetHandle());
    private static void Fail() => throw new IOException("storage-io-failed:" + Marshal.GetLastPInvokeError());
    internal static uint U32(byte[] b, int p) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));
    internal static ulong U64(byte[] b, int p) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p));
    private static ushort U16(byte[] b, int p) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p));
    private static long I64(byte[] b, int p) => BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(p));

    [DllImport("libc", SetLastError = true)] private static extern int openat(int fd, string path, int flags, uint mode);
    [DllImport("libc", SetLastError = true)] private static extern IntPtr acl_get_fd_np(SafeFileHandle fd, int type);
    [DllImport("libc", SetLastError = true)] private static extern int acl_get_entry(IntPtr acl, int entryId, out IntPtr entry);
    [DllImport("libc", SetLastError = true)] private static extern int acl_free(IntPtr acl);
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)] private static extern int openat_arm64(int fd, string path, int flags, nint p0, nint p1, nint p2, nint p3, nint p4, uint mode);
    [DllImport("libc", SetLastError = true)] private static extern int mkdirat(SafeFileHandle fd, string path, uint mode);
    [DllImport("libc", SetLastError = true)] private static extern int fsync(SafeFileHandle fd);
    [DllImport("libc", SetLastError = true)] private static extern int unlinkat(SafeFileHandle fd, string name, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int fstatvfs(SafeFileHandle fd, [Out] byte[] data);
    [DllImport("libc", SetLastError = true)] private static extern int fstat(SafeFileHandle fd, [Out] byte[] data);
    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)] private static extern int fstat_inode64(SafeFileHandle fd, [Out] byte[] data);
    [DllImport("libc", SetLastError = true)] private static extern int fstatfs(SafeFileHandle fd, [Out] byte[] data);
    [DllImport("libc", EntryPoint = "fstatfs$INODE64", SetLastError = true)] private static extern int fstatfs_inode64(SafeFileHandle fd, [Out] byte[] data);
    [DllImport("libc", SetLastError = true)] private static extern int statx(SafeFileHandle fd, string path, int flags, uint mask, [Out] byte[] data);
    [DllImport("libc")] private static extern uint geteuid();
    [DllImport("libc", SetLastError = true)] private static extern int renameat2(SafeFileHandle oldFd, string oldPath, SafeFileHandle newFd, string newPath, uint flags);
    [DllImport("libc", SetLastError = true)] private static extern int renameatx_np(SafeFileHandle oldFd, string oldPath, SafeFileHandle newFd, string newPath, uint flags);
}
