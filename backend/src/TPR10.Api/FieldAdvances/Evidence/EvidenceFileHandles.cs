using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TPR10.Api.FieldAdvances.Evidence;

// Walk every directory with openat/O_NOFOLLOW, then open the object relative to the pinned directory.
// This also closes the check/open symlink race. Roots must be provisioned by the trusted operator.
internal static class EvidenceFileHandles
{
    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)]
    private static extern int openat(SafeFileHandle directory, string path, int flags, uint mode);

    // Darwin ARM64 puts variadic arguments on the stack (after eight argument registers).
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int openatArm64(SafeFileHandle directory, string path, int flags,
        nint pad0, nint pad1, nint pad2, nint pad3, nint pad4, uint mode);
    [DllImport("libc", SetLastError = true)] private static extern int dup(SafeFileHandle fd);
    [DllImport("libc", SetLastError = true)] private static extern IntPtr fdopendir(int fd);
    [DllImport("libc", SetLastError = true)] private static extern IntPtr readdir(IntPtr directory);
    [DllImport("libc")] private static extern int closedir(IntPtr directory);

    internal static IEnumerable<string> Names(string root)
    {
        using var handle = OpenRoot(root); using var copy = Handle(dup(handle));
        var directory = fdopendir(copy.DangerousGetHandle().ToInt32());
        if (directory == IntPtr.Zero) throw new IOException("Cannot enumerate evidence root.");
        copy.SetHandleAsInvalid(); // fdopendir/closedir now own this duplicate.
        try
        {
            IntPtr entry;
            while ((entry = readdir(directory)) != IntPtr.Zero)
            {
                var name = Marshal.PtrToStringUTF8(entry + (OperatingSystem.IsMacOS() ? 21 : 19));
                if (name is not null && EvidenceLocator.IsKey(name)) yield return name;
            }
        }
        finally { closedir(directory); }
    }
    internal static SafeFileHandle OpenRoot(string root)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) throw new IOException("Unsupported secure filesystem adapter.");
        if (!Path.IsPathFullyQualified(root) || root.Split('/').Any(x => x is "." or "..")) throw new IOException("Invalid storage root.");
        var current = Handle(open("/", DirectoryFlags));
        try
        {
            foreach (var part in root.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var next = Handle(openat(current, part, DirectoryFlags, 0)); current.Dispose(); current = next;
            }
            return current;
        }
        catch { current.Dispose(); throw; }
    }
    internal static FileStream Open(string root, string key, bool create)
    {
        if (!EvidenceLocator.IsKey(key)) throw new IOException("Invalid object identifier.");
        using var directory = OpenRoot(root);
        var flags = NoFollow | CloseOnExec | (create ? 1 | (OperatingSystem.IsMacOS() ? 0x200 | 0x800 : 0x40 | 0x80) : 0);
        var fd = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? openatArm64(directory, key, flags, 0, 0, 0, 0, 0, 0x180) : openat(directory, key, flags, 0x180);
        var handle = Handle(fd); // 0600; exclusive create, never overwrite.
        try { return new FileStream(handle, create ? FileAccess.Write : FileAccess.Read); }
        catch { handle.Dispose(); throw; }
    }
    // Linux ARM64 UAPI overrides asm-generic flags (arch/arm64/include/uapi/asm/fcntl.h).
    // Keep Darwin and Linux x64 behavior; never substitute O_DIRECT for O_DIRECTORY on ARM64.
    private static bool LinuxArm64 => OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
    private static int NoFollow => OperatingSystem.IsMacOS() ? 0x100 : LinuxArm64 ? 0x8000 : 0x20000;
    private static int CloseOnExec => OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;
    private static int DirectoryFlags => NoFollow | CloseOnExec | (OperatingSystem.IsMacOS() ? 0x100000 : LinuxArm64 ? 0x4000 : 0x10000);
    private static SafeFileHandle Handle(int fd) => fd < 0 ? throw new IOException("Secure evidence filesystem operation failed.", Marshal.GetLastPInvokeError()) : new(new IntPtr(fd), true);
}
