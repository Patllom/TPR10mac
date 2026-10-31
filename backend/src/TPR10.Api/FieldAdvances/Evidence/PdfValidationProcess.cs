using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TPR10.Api.FieldAdvances.Evidence;

public sealed class PdfValidationOptions
{
    public string DockerExecutable { get; set; } = "docker";
    public string ImageId { get; set; } = "";
    public int MemoryMiB { get; set; } = 512;
    public int Pids { get; set; } = 64;
    public double Cpus { get; set; } = 1;
    public int DeadlineSeconds { get; set; } = 15;
    public int MaxConcurrent { get; set; } = 2;
    public int MaxOutputBytes { get; set; } = 4096;
    public void Validate()
    {
        if (MemoryMiB is < 64 or > 512 || Pids is < 16 or > 64 || !double.IsFinite(Cpus) || Cpus is <= 0 or > 1
            || DeadlineSeconds is < 1 or > 15 || MaxConcurrent is < 1 or > 2 || MaxOutputBytes is < 64 or > 4096
            || string.IsNullOrWhiteSpace(DockerExecutable) || !Regex.IsMatch(ImageId, "\\Asha256:[a-f0-9]{64}\\z", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("PDF validation requires a local immutable image ID and bounded resource limits.");
    }
}

// Trusted host boundary: the API account can invoke Docker. The worker receives bytes only, never
// filesystem mounts, host credentials, sockets or network access. Register one instance per API host.
public sealed class PdfValidationProcess(PdfValidationOptions options) : IFinancePdfValidator
{
    private readonly SemaphoreSlim slots = new(2, 2);
    private int active;
    private readonly ConcurrentDictionary<string, bool> pendingCleanup = new(StringComparer.Ordinal);
    public string[] PendingCleanupNames => pendingCleanup.Keys.Order(StringComparer.Ordinal).ToArray();

    // Operator recovery retains exact ownership. An uncertain create cannot be cleared by absence:
    // only a successful removal proves that the late create existed and has now been removed.
    public async Task<bool> ReconcileCleanupAsync(CancellationToken ct)
    {
        foreach (var pending in pendingCleanup.ToArray())
        {
            ct.ThrowIfCancellationRequested();
            if (await Remove(pending.Key, pending.Value)) pendingCleanup.TryRemove(pending.Key, out _);
        }
        return pendingCleanup.IsEmpty;
    }

    public async Task<bool> ValidateAsync(byte[] content, CancellationToken ct)
    {
        try { options.Validate(); } catch (InvalidOperationException) { return false; }
        if (content.Length is 0 or > 10 * 1024 * 1024 || ct.IsCancellationRequested || !pendingCleanup.IsEmpty || !slots.Wait(0)) return false;
        var entered = Interlocked.Increment(ref active);
        if (entered > options.MaxConcurrent) { Interlocked.Decrement(ref active); slots.Release(); return false; }
        var name = "tpr10-finance-pdf-" + Guid.NewGuid().ToString("N"); var attemptedCreate = false; var creationConfirmed = false; var valid = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.DeadlineSeconds));
        try
        {
            var runtime = await Run(["info", "--format", "{{.OSType}} {{.CgroupVersion}} {{.MemoryLimit}} {{.SwapLimit}} {{.PidsLimit}} {{.CPUCfsQuota}}"], null, deadline.Token);
            if (runtime.Code != 0 || runtime.Output.Trim() != "linux 2 true true true true") return false;
            var image = await Run(["image", "inspect", "--format", "{{.Id}}", options.ImageId], null, deadline.Token);
            if (image.Code != 0 || image.Output.Trim() != options.ImageId) return false;
            attemptedCreate = true;
            var memory = options.MemoryMiB.ToString(CultureInfo.InvariantCulture) + "m";
            var created = await Run(["create", "--pull=never", "--name", name, "--label", "tpr10.finance-pdf=true", "-i",
                "--network=none", "--read-only", "--user", "1654:1654", "--cap-drop=ALL", "--security-opt", "no-new-privileges=true",
                "--memory", memory, "--memory-swap", memory, "--cpus", options.Cpus.ToString(CultureInfo.InvariantCulture),
                "--pids-limit", options.Pids.ToString(CultureInfo.InvariantCulture), "--stop-timeout", "1",
                "--log-driver=none", options.ImageId], null, deadline.Token);
            if (created.Code != 0) return false;
            creationConfirmed = true;
            var result = await Run(["start", "-a", "-i", name], content, deadline.Token);
            var state = await Run(["inspect", "--format", "{{.State.ExitCode}} {{.State.OOMKilled}} {{.State.Running}}", name], null, deadline.Token);
            valid = result.Code == 0 && result.Output == "valid\n" && result.Error.Length == 0
                && state.Code == 0 && state.Output.Trim() == "0 false false" && !deadline.IsCancellationRequested;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { valid = false; }
        finally
        {
            if (attemptedCreate && !await Remove(name, creationConfirmed))
            { pendingCleanup[name] = creationConfirmed; valid = false; }
            Interlocked.Decrement(ref active); slots.Release();
        }
        return valid && !ct.IsCancellationRequested;
    }

    private async Task<bool> Remove(string name, bool creationConfirmed)
    {
        // Cleanup has its own bounded budget, independent of the canceled upload. Always target only
        // this unpredictable job name. CLI termination does NOT establish daemon create completion.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        try
        {
            var removalConfirmed = false;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var present = await Run(["inspect", "--format", "{{.Id}}", name], null, cleanup.Token);
                var id = present.Output.Trim();
                var observed = present.Code == 0 && Regex.IsMatch(id, "\\A[a-f0-9]{64}\\z", RegexOptions.CultureInvariant);
                var removed = await Run(["rm", "-f", observed ? id : name], null, cleanup.Token);
                removalConfirmed |= observed && removed.Code == 0;
                await Task.Delay(100, cleanup.Token);
                var remaining = await Run(["ps", "-aq", "--filter", "name=^/" + name + "$"], null, cleanup.Token);
                if (remaining.Code != 0 || remaining.Output.Trim().Length != 0) continue;
                if (attempt == 2) return creationConfirmed || removalConfirmed;
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
        return false; // latch failure; operator recovery is required before accepting more PDFs
    }

    private async Task<(int Code, string Output, string Error)> Run(string[] args, byte[]? input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = new(options.DockerExecutable)
            { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true }
        };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        if (!process.Start()) throw new IOException("Cannot start PDF runner.");
        using var io = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stdout = ReadBounded(process.StandardOutput.BaseStream, io);
        var stderr = ReadBounded(process.StandardError.BaseStream, io);
        var writer = WriteInput();
        try
        {
            await Task.WhenAll(stdout, stderr, writer, process.WaitForExitAsync(io.Token));
            return (process.ExitCode, await stdout, await stderr);
        }
        catch
        {
            io.Cancel();
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch (InvalidOperationException) { }
            try { await Task.WhenAll(stdout, stderr, writer); } catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
            throw;
        }
        async Task WriteInput()
        {
            try { if (input != null) await process.StandardInput.BaseStream.WriteAsync(input, io.Token); }
            finally { process.StandardInput.Close(); }
        }
    }
    private async Task<string> ReadBounded(Stream stream, CancellationTokenSource io)
    {
        using var bytes = new MemoryStream(); var buffer = new byte[1024];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, io.Token)) != 0)
            {
                if (bytes.Length + read > options.MaxOutputBytes) throw new IOException("PDF worker output exceeded its limit.");
                bytes.Write(buffer, 0, read);
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }
        catch { io.Cancel(); throw; }
    }
}
