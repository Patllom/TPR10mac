using System.Runtime.Versioning;
using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;
using TPR10.Api.Attendance.Storage;

namespace TPR10.Api.IntegrationTests;

[UnsupportedOSPlatform("windows")]
public sealed class StorageAdapterTests : IDisposable
{
    private readonly string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : "/tmp", "tpr10-storage-" + Guid.NewGuid().ToString("N"));
    private readonly Guid marker = Guid.Parse("b79fbb26-bc7e-4bdc-b564-bf042578fed8");
    private readonly Guid storageId = Guid.Parse("3961e25f-35af-47ec-b5dd-17e6c8905929");
    private const string Key = "objects/ab/abcdefabcdefabcdefabcdefabcdefab/full.jpg";
    private static readonly byte[] Bytes = [1, 2, 3, 4];

    public StorageAdapterTests()
    {
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(root, ".tpr10-storage-id"), marker.ToString("D"));
        File.SetUnixFileMode(Path.Combine(root, ".tpr10-storage-id"), UnixFileMode.UserRead);
    }

    [Fact]
    public async Task Immutable_write_reads_back_actual_checksum_and_retry_preserves_existing_bytes()
    {
        var adapter = Adapter();
        StoredCopy result = await adapter.WriteImmutableAsync(Key, Bytes, CancellationToken.None);
        Assert.Equal("9f64a747e1b97f131fabb6b447296c9b6f0201e79fb3c5356e6c77e89b6a806a", result.Sha256);
        Assert.Equal(4, result.Length);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(Path.Combine(root, Key)));
        byte[] actual = await adapter.ReadVerifiedAsync(Key, result.Sha256, 4L, CancellationToken.None);
        Assert.Equal(Bytes, actual);
        StoredCopy retry = await adapter.WriteImmutableAsync(Key, Bytes, CancellationToken.None);
        Assert.Equal(result, retry);
        await Assert.ThrowsAsync<IOException>(() => (Task)adapter.WriteImmutableAsync(Key, new ReadOnlyMemory<byte>([5, 6]), CancellationToken.None));
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(Path.Combine(root, Key)));
    }

    [Theory]
    [InlineData("../outside.jpg")]
    [InlineData("/tmp/outside.jpg")]
    [InlineData("\\\\server\\share\\image.jpg")]
    [InlineData("objects/%2f/abcdefabcdefabcdefabcdefabcdefab/full.jpg")]
    [InlineData("objects/ab/abcdefabcdefabcdefabcdefabcdefab/%2e%2e%2ffull.jpg")]
    [InlineData("objects/ab/abcdefabcdefabcdefabcdefabcdefab/../full.jpg")]
    [InlineData("objects/ff/abcdefabcdefabcdefabcdefabcdefab/full.jpg")]
    [InlineData("objects/ab/ABCDEFABCDEFABCDEFABCDEFABCDEFAB/full.jpg")]
    [InlineData("objects/ab/abcdefabcdefabcdefabcdefabcdefab/extra.jpg")]
    public async Task Unsafe_or_noncanonical_keys_never_create_objects(string key)
    {
        var adapter = Adapter();
        await Assert.ThrowsAsync<IOException>(() => (Task)adapter.WriteImmutableAsync(key, Bytes, CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(root, "objects")));
    }

    [Fact]
    public async Task Symlink_component_is_not_followed()
    {
        var outside = root + "-outside";
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "objects"), outside);
            var adapter = Adapter();
            await Assert.ThrowsAsync<IOException>(() => (Task)adapter.WriteImmutableAsync(Key, Bytes, CancellationToken.None));
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        }
        finally { Directory.Delete(outside); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_wrong_marker_is_not_repaired_and_prevents_write(bool wrong)
    {
        File.Delete(Path.Combine(root, ".tpr10-storage-id"));
        if (wrong) File.WriteAllText(Path.Combine(root, ".tpr10-storage-id"), Guid.NewGuid().ToString("D"));
        var adapter = Adapter();
        await Assert.ThrowsAsync<IOException>(() => (Task)adapter.WriteImmutableAsync(Key, Bytes, CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(root, "objects")));
        if (!wrong) Assert.False(File.Exists(Path.Combine(root, ".tpr10-storage-id")));
    }

    [Fact]
    public async Task Nas_definition_does_not_accept_an_unmounted_local_directory_even_with_marker()
    {
        var adapter = Adapter("nas-mounted-folder");
        await Assert.ThrowsAsync<IOException>(() => (Task)adapter.WriteImmutableAsync(Key, Bytes, CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(root, "objects")));
    }

    [Fact]
    public async Task Wrong_volume_identity_fails_without_creating_objects()
    {
        var adapter = Adapter(volume: "wrong-volume");
        await Assert.ThrowsAsync<IOException>(() => (Task)adapter.WriteImmutableAsync(Key, Bytes, CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(root, "objects")));
    }

    [Fact]
    public async Task Corrupted_copy_and_wrong_length_are_never_returned()
    {
        var adapter = Adapter();
        StoredCopy copy = await adapter.WriteImmutableAsync(Key, Bytes, CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => (Task)adapter.ReadVerifiedAsync(Key, copy.Sha256, 3L, CancellationToken.None));
        await File.WriteAllBytesAsync(Path.Combine(root, Key), [99, 22, 33, 44]);
        await Assert.ThrowsAsync<IOException>(() => (Task)adapter.ReadVerifiedAsync(Key, copy.Sha256, 4L, CancellationToken.None));
    }

    [Theory]
    [InlineData(511)]
    [InlineData(320)]
    public async Task Unsafe_or_nonwritable_root_mode_prevents_write(int mode)
    {
        File.SetUnixFileMode(root, (UnixFileMode)mode);
        var adapter = Adapter();
        await Assert.ThrowsAsync<IOException>(() => (Task)adapter.WriteImmutableAsync(Key, Bytes, CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(root, "objects")));
    }

    [Fact]
    public void Object_keys_are_canonical_and_share_evidence_id_between_variants()
    {
        var id = Guid.Parse("abcdefab-cdef-abcd-efab-cdefabcdefab");
        Assert.Equal(Key, StorageObjectKey.Create(id, "full"));
        Assert.Equal("objects/ab/abcdefabcdefabcdefabcdefabcdefab/thumbnail.jpg", StorageObjectKey.Create(id, "thumbnail"));
        Assert.Throws<IOException>(() => StorageObjectKey.Create(Guid.Empty, "full"));
    }

    [Fact]
    public async Task Concurrent_collision_has_one_winner_and_never_overwrites()
    {
        var adapter = Adapter();
        var first = adapter.WriteImmutableAsync(Key, Bytes, CancellationToken.None);
        var other = adapter.WriteImmutableAsync(Key, new byte[] { 9, 8, 7, 6 }, CancellationToken.None);
        var errors = await Task.WhenAll(Record.ExceptionAsync(() => first), Record.ExceptionAsync(() => other));
        Assert.Single(errors, e => e is null);
        Assert.Single(errors, e => e is IOException);
        var winner = first.IsCompletedSuccessfully ? Bytes : new byte[] { 9, 8, 7, 6 };
        Assert.Equal(winner, File.ReadAllBytes(Path.Combine(root, Key)));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(root, Key)));
    }

    [Theory]
    [InlineData("write")]
    [InlineData("sync")]
    [InlineData("rename")]
    public async Task Disk_or_flush_failure_never_reports_success_or_publishes_partial_bytes(string stage)
    {
        var adapter = FaultAdapter(new FaultFiles { Fail = stage });
        await Assert.ThrowsAsync<IOException>(() => adapter.WriteImmutableAsync(Key, Bytes, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(root, Key)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Short_reads_are_accumulated_but_premature_eof_is_rejected(bool premature)
    {
        var copy = await Adapter().WriteImmutableAsync(Key, Bytes, CancellationToken.None);
        var adapter = FaultAdapter(new FaultFiles { Partial = true, Premature = premature });
        if (premature) await Assert.ThrowsAsync<IOException>(() => adapter.ReadVerifiedAsync(Key, copy.Sha256, 4, CancellationToken.None));
        else Assert.Equal(Bytes, await adapter.ReadVerifiedAsync(Key, copy.Sha256, 4, CancellationToken.None));
    }

    [Fact]
    public async Task Parent_swapped_for_symlink_just_before_open_never_touches_outside()
    {
        var outside = root + "-outside";
        Directory.CreateDirectory(outside);
        try
        {
            var files = new FaultFiles
            {
                BeforeOpen = (name) =>
            {
                if (name == "ab")
                {
                    var parent = Path.Combine(root, "objects", "ab");
                    if (Directory.Exists(parent)) Directory.Move(parent, parent + "-old");
                    Directory.CreateSymbolicLink(parent, outside);
                }
            }
            };
            await Assert.ThrowsAsync<IOException>(() => FaultAdapter(files).WriteImmutableAsync(Key, Bytes, CancellationToken.None));
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        }
        finally { Directory.Delete(outside); }
    }

    [Fact]
    public async Task Probe_has_bound_storage_id_and_performs_real_durable_roundtrip()
    {
        var adapter = Adapter();
        var result = await adapter.ProbeAsync(CancellationToken.None);
        Assert.Equal(storageId, result.StorageId);
        Assert.Contains(result.Status, new[] { "healthy", "warning" });
        Assert.NotNull(result.FreeBytes);
        Assert.True(result.TotalBytes > 0);
        Assert.False(Directory.Exists(Path.Combine(root, "objects")));
        Assert.Single(Directory.EnumerateFiles(root)); // Only the provisioned marker remains.
    }

    private FolderStorageAdapter FaultAdapter(SafeFileHandles files)
    {
        return new FolderStorageAdapter(storageId, new StorageDefinition("test", "local-folder", root, MountIdentity.GetVolumeId(root), marker), TimeProvider.System, files);
    }

    [Theory]
    [InlineData(10737418239L, 21474836480L, "warning")]
    [InlineData(10737418240L, 107374182401L, "warning")]
    [InlineData(10737418240L, 107374182400L, "healthy")]
    [InlineData(null, null, "unknown")]
    [InlineData(1L, null, "unknown")]
    public async Task Probe_classifies_capacity_at_absolute_and_relative_boundaries(long? free, long? total, string expected)
    {
        var result = await FaultAdapter(new CapacityFiles(free, total)).ProbeAsync(CancellationToken.None);
        Assert.Equal(expected, result.Status);
        Assert.Equal(free, result.FreeBytes);
        Assert.Equal(total, result.TotalBytes);
    }

    private sealed class CapacityFiles(long? free, long? total) : SafeFileHandles
    {
        public override (long? Free, long? Total) Capacity(SafeFileHandle handle) => (free, total);
    }

    [Fact]
    public async Task Stalled_native_reads_timeout_without_freeing_live_workers()
    {
        var copy = await Adapter().WriteImmutableAsync(Key, Bytes, CancellationToken.None);
        using var release = new ManualResetEventSlim();
        using var entered = new CountdownEvent(2);
        var files = new FaultFiles { BeforeDataRead = () => { entered.Signal(); release.Wait(); } };
        var clock = new ManualClock();
        var adapter = new FolderStorageAdapter(storageId, new StorageDefinition("test", "local-folder", root, MountIdentity.GetVolumeId(root), marker), clock, files);
        var first = adapter.ReadVerifiedAsync(Key, copy.Sha256, 4, CancellationToken.None);
        var second = adapter.ReadVerifiedAsync(Key, copy.Sha256, 4, CancellationToken.None);
        var queued = new List<Task<byte[]>>();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            for (var i = 0; i < 8; i++) queued.Add(Adapter().ReadVerifiedAsync(Key, copy.Sha256, 4, CancellationToken.None));
            clock.Fire();
            var failure = await Assert.ThrowsAsync<IOException>(() => first.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal("storage-timeout", failure.Message);
            await Assert.ThrowsAsync<IOException>(() => second.WaitAsync(TimeSpan.FromSeconds(2)));
            var busy = await Assert.ThrowsAsync<IOException>(() => Adapter().ReadVerifiedAsync(Key, copy.Sha256, 4, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal("storage-busy", busy.Message);
        }
        finally
        {
            release.Set();
            await Record.ExceptionAsync(() => first);
            await Record.ExceptionAsync(() => second);
            await Task.WhenAll(queued).WaitAsync(TimeSpan.FromSeconds(8));
        }
    }

    [Fact]
    public void Resolver_accepts_only_configured_alias_and_pins_binding()
    {
        var definitions = new[]
        {
            new StorageDefinition("local", "local-folder", root, MountIdentity.GetVolumeId(root), marker),
            new StorageDefinition("other", "local-folder", root, MountIdentity.GetVolumeId(root), marker)
        };
        var resolver = new StorageRootResolver(definitions, TimeProvider.System);
        IStorageAdapter first = resolver.Resolve(storageId, "local");
        Assert.Same(first, (IStorageAdapter)resolver.Resolve(storageId, "local"));
        Assert.Throws<IOException>(() => resolver.Resolve(storageId, "other"));
        Assert.Throws<IOException>(() => resolver.Resolve(Guid.NewGuid(), root));
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly List<ManualTimer> timers = [];
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(TimeSpan.FromSeconds(10), dueTime);
            var timer = new ManualTimer(callback, state);
            lock (timers) timers.Add(timer);
            return timer;
        }
        public void Fire() { lock (timers) foreach (var timer in timers) timer.Fire(); }
        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            public void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private FolderStorageAdapter Adapter(string kind = "local-folder", string? volume = null)
    {
        volume ??= MountIdentity.GetVolumeId(root);
        return new FolderStorageAdapter(storageId, new StorageDefinition("test", kind, root, volume, marker), TimeProvider.System);
    }

    private sealed class FaultFiles : SafeFileHandles
    {
        public bool FailPublicationSync { get; set; }
        public int PublicationSyncAttempts { get; private set; }
        private ulong? publishedDirectory;
        public string? Fail { get; init; }
        public bool Partial { get; init; }
        public bool Premature { get; init; }
        public Action<string>? BeforeOpen { get; init; }
        public Action? BeforeDataRead { get; init; }
        public Action? AfterWrite { get; init; }
        public Action? AfterDataRead { get; init; }
        public ConcurrentBag<SafeFileHandle> OpenedDirectories { get; } = [];
        private int directorySyncs;
        public override SafeFileHandle OpenDirectory(SafeFileHandle parent, string name, bool create)
        {
            BeforeOpen?.Invoke(name);
            var result = base.OpenDirectory(parent, name, create);
            OpenedDirectories.Add(result);
            return result;
        }
        public override void Write(SafeFileHandle file, ReadOnlySpan<byte> bytes, long offset)
        {
            if (Fail == "write") throw new IOException("injected-ENOSPC");
            base.Write(file, bytes, offset);
            AfterWrite?.Invoke();
        }
        public override void Sync(SafeFileHandle file)
        {
            if (Inspect(file).Inode == publishedDirectory)
            {
                PublicationSyncAttempts++;
                if (FailPublicationSync) throw new IOException("injected-publication-sync-failure");
            }
            if (Fail == "sync" && (Inspect(file).Mode & 0xf000) == 0x8000) throw new IOException("injected-flush-failure");
            if (Fail == "directory-sync" && (Inspect(file).Mode & 0xf000) == 0x4000 && ++directorySyncs == 2)
                throw new IOException("injected-directory-flush-failure");
            base.Sync(file);
        }
        public override bool RenameNoReplace(SafeFileHandle directory, string source, string destination)
        {
            if (Fail == "rename") throw new IOException("injected-rename-failure");
            var result = base.RenameNoReplace(directory, source, destination);
            if (result) publishedDirectory = Inspect(directory).Inode;
            return result;
        }
        public override int Read(SafeFileHandle file, Span<byte> bytes, long offset)
        {
            if (Inspect(file).Length == 4 && offset == 0) BeforeDataRead?.Invoke();
            if (Inspect(file).Length == 4 && Partial)
            {
                if (Premature && offset >= 2) return 0;
                return base.Read(file, bytes[..Math.Min(1, bytes.Length)], offset);
            }
            var result = base.Read(file, bytes, offset);
            if (Inspect(file).Length == 4 && offset == 0) AfterDataRead?.Invoke();
            return result;
        }
    }

    [Fact]
    public async Task Directory_flush_failure_closes_every_opened_handle()
    {
        var files = new FaultFiles { Fail = "directory-sync" };
        await Assert.ThrowsAsync<IOException>(() => FaultAdapter(files).WriteImmutableAsync(Key, Bytes, CancellationToken.None));
        Assert.All(files.OpenedDirectories, handle => Assert.True(handle.IsClosed));
    }

    [Fact]
    public async Task Retry_after_published_directory_sync_failure_must_finish_durability_barrier()
    {
        var files = new FaultFiles { FailPublicationSync = true };
        var adapter = FaultAdapter(files);
        await Assert.ThrowsAsync<IOException>(() => adapter.WriteImmutableAsync(Key, Bytes, CancellationToken.None));
        Assert.Equal(Bytes, File.ReadAllBytes(Path.Combine(root, Key)));
        await Assert.ThrowsAsync<IOException>(() => adapter.WriteImmutableAsync(Key, Bytes, CancellationToken.None));
        files.FailPublicationSync = false;
        var copy = await adapter.WriteImmutableAsync(Key, Bytes, CancellationToken.None);
        Assert.Equal(3, files.PublicationSyncAttempts);
        Assert.Equal(Bytes, await adapter.ReadVerifiedAsync(Key, copy.Sha256, 4, CancellationToken.None));
    }

    [Fact]
    public async Task Parent_replaced_after_write_is_not_reported_as_a_published_copy()
    {
        var outside = root + "-outside";
        Directory.CreateDirectory(outside);
        try
        {
            var parent = Path.GetDirectoryName(Path.Combine(root, Key))!;
            var files = new FaultFiles
            {
                AfterWrite = () =>
            {
                Directory.Move(parent, parent + "-old");
                Directory.CreateSymbolicLink(parent, outside);
            }
            };
            await Assert.ThrowsAsync<IOException>(() => FaultAdapter(files).WriteImmutableAsync(Key, Bytes, CancellationToken.None));
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        }
        finally { Directory.Delete(outside); }
    }

    [Fact]
    public async Task Final_inode_replaced_during_read_is_rejected()
    {
        var copy = await Adapter().WriteImmutableAsync(Key, Bytes, CancellationToken.None);
        var path = Path.Combine(root, Key);
        var files = new FaultFiles
        {
            AfterDataRead = () =>
        {
            File.Move(path, path + ".old");
            File.WriteAllBytes(path, Bytes);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        };
        await Assert.ThrowsAsync<IOException>(() => FaultAdapter(files).ReadVerifiedAsync(Key, copy.Sha256, 4, CancellationToken.None));
    }

    [MacTheory]
    [InlineData("explicit")]
    [InlineData("inherited")]
    [InlineData("file")]
    public async Task Mac_acl_grants_are_rejected_even_when_unix_modes_are_private(string target)
    {
        if (!OperatingSystem.IsMacOS()) throw new InvalidOperationException("macOS-only test");
        var adapter = Adapter();
        if (target == "file")
        {
            var copy = await adapter.WriteImmutableAsync(Key, Bytes, CancellationToken.None);
            ChmodAcl("+a", "everyone allow read", Path.Combine(root, Key));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(root, Key)));
            await Assert.ThrowsAsync<IOException>(() => adapter.ReadVerifiedAsync(Key, copy.Sha256, 4, CancellationToken.None));
            return;
        }
        ChmodAcl("+a", "everyone allow read,search,file_inherit,directory_inherit", root);
        if (target == "inherited")
        {
            var objects = Path.Combine(root, "objects");
            Directory.CreateDirectory(objects);
            File.SetUnixFileMode(objects, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            ChmodAcl("-N", root); // Preserve the child's inherited ACL, remove only the root ACL.
        }
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(root));
        await Assert.ThrowsAsync<IOException>(() => adapter.WriteImmutableAsync(Key, Bytes, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(root, Key)));
    }

    private sealed class MacTheoryAttribute : TheoryAttribute
    {
        public MacTheoryAttribute() { if (!OperatingSystem.IsMacOS()) Skip = "Darwin ACL integration requires macOS"; }
    }

    private static void ChmodAcl(params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("/bin/chmod") { UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        Assert.True(process.WaitForExit(5000));
        Assert.Equal(0, process.ExitCode);
    }

    public void Dispose()
    {
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.Delete(root, recursive: true);
    }
}
