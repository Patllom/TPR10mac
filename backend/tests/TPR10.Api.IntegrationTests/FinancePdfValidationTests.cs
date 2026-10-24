using System.Diagnostics;
using System.Text;
using TPR10.Api.FieldAdvances.Evidence;

namespace TPR10.Api.IntegrationTests;

// These tests require prebuilt local images. Missing Docker/images fail, never silently skip or build during validation.
[Collection("database")]
public sealed class FinancePdfValidationTests
{
    [Fact]
    public async Task Valid_pdf_succeeds()
    {
        var runner = new PdfValidationProcess(await Options());
        Assert.True(await runner.ValidateAsync(Pdf(), default)); await NoContainers();
    }

    [Theory]
    [InlineData("q Q", true)]
    [InlineData("q definitelyNotAPdfOperator Q", false)]
    [InlineData("definitelyNotAPdfOperator q Q", false)]
    [InlineData("BX definitelyNotAPdfOperator EX", false)]
    public async Task Content_operators_must_belong_to_the_inspectable_subset(string content, bool expected)
    {
        var runner = new PdfValidationProcess(await Options());
        Assert.Equal(expected, await runner.ValidateAsync(Pdf(stream: Encoding.ASCII.GetBytes(content)), default));
        await NoContainers();
    }

    [Fact]
    public async Task Interrupted_create_retains_ownership_and_does_not_reopen_on_absence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tpr10-delayed-create-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var marker = Path.Combine(directory, "name");
        var executable = Path.Combine(directory, "docker-proxy");
        await File.WriteAllTextAsync(executable, "#!/bin/sh\nif [ \"$1\" = create ]; then\n  shift\n  while [ \"$1\" != --name ]; do shift; done\n  shift\n  printf '%s' \"$1\" > '" + marker + "'\n  exec sleep 30\nfi\nexec docker \"$@\"\n");
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This host-runner fixture requires Unix.");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string? name = null;
        try
        {
            var options = await Options(fixture: true); options.DockerExecutable = executable;
            var runner = new PdfValidationProcess(options); using var cancel = new CancellationTokenSource();
            var job = runner.ValidateAsync("normal"u8.ToArray(), cancel.Token);
            var watch = Stopwatch.StartNew();
            while (!File.Exists(marker) && watch.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
            Assert.True(File.Exists(marker)); name = await File.ReadAllTextAsync(marker);
            cancel.Cancel(); Assert.False(await job);
            Assert.Equal(name, Assert.Single(runner.PendingCleanupNames));
            Assert.False(await runner.ReconcileCleanupAsync(default)); // absence is not completion
            Assert.Equal(name, Assert.Single(runner.PendingCleanupNames));
            // The daemon completion is independent of the interrupted CLI: materialize the
            // real owned container only after ValidateAsync's initial cleanup has returned.
            await Docker("create", "--name", name, "--label", "tpr10.finance-pdf=true", "--network=none", options.ImageId);
            options.DockerExecutable = "docker";
            Assert.False(await runner.ValidateAsync("normal"u8.ToArray(), default));
            Assert.Equal(name, Assert.Single(runner.PendingCleanupNames));
            Assert.True(await runner.ReconcileCleanupAsync(default));
            Assert.Empty(runner.PendingCleanupNames);
            Assert.True(await runner.ValidateAsync("normal"u8.ToArray(), default));
            await NoContainers();
        }
        finally
        {
            if (name is not null) await Docker("rm", "-f", name);
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("embedded")]
    [InlineData("launch")]
    [InlineData("reference")]
    [InlineData("encrypted")]
    [InlineData("broken")]
    [InlineData("truncated")]
    [InlineData("escaped-name")]
    public async Task Forbidden_or_uninspectable_pdf_is_rejected(string kind)
    {
        var bytes = kind switch
        {
            "javascript" => Pdf("/OpenAction << /S /JavaScript /JS (app.alert) >>"),
            "escaped-name" => Pdf("/Names << /Java#53cript << /Names [] >> >>"),
            "embedded" => Pdf("/Names << /EmbeddedFiles << /Names [(x) 4 0 R] >> >>"),
            "launch" => Pdf("/AA << /O << /S /Launch /F (x) >> >>"),
            "reference" => Pdf("/Mystery 999 0 R"),
            "encrypted" => Pdf(encrypt: true),
            "broken" => "%PDF-1.7\nbroken\n%%EOF"u8.ToArray(),
            "truncated" => Pdf()[..^20],
            _ => throw new InvalidOperationException()
        };
        var runner = new PdfValidationProcess(await Options());
        Assert.False(await runner.ValidateAsync(bytes, default));
        Assert.True(await runner.ValidateAsync(Pdf(), default)); await NoContainers();
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("truncated-zlib", false)]
    [InlineData("expansion", false)]
    [InlineData("unsupported", false)]
    public async Task Compressed_content_must_be_valid_and_bounded(string kind, bool expected)
    {
        using var output = new MemoryStream();
        using (var zip = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.SmallestSize, true))
            zip.Write(kind == "expansion" ? Enumerable.Repeat((byte)' ', 17 * 1024 * 1024).ToArray() : "q Q"u8.ToArray());
        var stream = output.ToArray(); if (kind == "truncated-zlib") stream = stream[..^4];
        var bytes = Pdf(stream: stream, filter: kind == "unsupported" ? "UnknownDecode" : "FlateDecode");
        var runner = new PdfValidationProcess(await Options());
        Assert.Equal(expected, await runner.ValidateAsync(bytes, default)); await NoContainers();
    }

    [Theory]
    [InlineData("oom")]
    [InlineData("stdout")]
    [InlineData("stderr")]
    [InlineData("crash")]
    [InlineData("sleep")]
    public async Task Real_worker_resource_or_process_failure_rejects_and_is_cleaned(string mode)
    {
        var options = await Options(fixture: true); options.DeadlineSeconds = mode == "sleep" ? 2 : 15;
        var runner = new PdfValidationProcess(options); var watch = Stopwatch.StartNew();
        Assert.False(await runner.ValidateAsync(Encoding.UTF8.GetBytes(mode), default));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(25)); await NoContainers();
        Assert.True(await runner.ValidateAsync("normal"u8.ToArray(), default)); await NoContainers();
    }

    [Theory]
    [InlineData("memory-settings")]
    [InlineData("cpu")]
    [InlineData("pids")]
    [InlineData("isolation")]
    public async Task Kernel_enforces_cpu_pids_and_isolation_against_real_work(string mode)
    {
        var runner = new PdfValidationProcess(await Options(fixture: true));
        Assert.True(await runner.ValidateAsync(Encoding.UTF8.GetBytes(mode), default)); await NoContainers();
    }

    [Fact]
    public async Task Cancellation_stops_container_and_next_validation_works()
    {
        var runner = new PdfValidationProcess(await Options(fixture: true)); using var cancel = new CancellationTokenSource();
        var job = runner.ValidateAsync("sleep"u8.ToArray(), cancel.Token);
        await WaitForContainers(1); cancel.Cancel(); Assert.False(await job); await NoContainers();
        Assert.True(await runner.ValidateAsync("normal"u8.ToArray(), default));
    }

    [Fact]
    public async Task Two_active_jobs_reject_a_third_without_queueing()
    {
        var runner = new PdfValidationProcess(await Options(fixture: true)); using var cancel = new CancellationTokenSource();
        var first = runner.ValidateAsync("sleep"u8.ToArray(), cancel.Token);
        var second = runner.ValidateAsync("sleep"u8.ToArray(), cancel.Token);
        try
        {
            await WaitForContainers(2); var watch = Stopwatch.StartNew();
            Assert.False(await runner.ValidateAsync("normal"u8.ToArray(), default)); Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
        }
        finally { cancel.Cancel(); await Task.WhenAll(first, second); }
        await NoContainers();
    }

    [Fact]
    public async Task Missing_runtime_or_image_fails_closed()
    {
        var options = await Options(); options.DockerExecutable = "/nonexistent/tpr10-docker";
        Assert.False(await new PdfValidationProcess(options).ValidateAsync(Pdf(), default));
        options.DockerExecutable = "docker"; options.ImageId = "sha256:" + new string('0', 64);
        Assert.False(await new PdfValidationProcess(options).ValidateAsync(Pdf(), default));
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("pids")]
    [InlineData("cpu")]
    [InlineData("deadline")]
    [InlineData("concurrency")]
    [InlineData("output")]
    [InlineData("image")]
    public void Unbounded_or_mutable_configuration_is_rejected(string setting)
    {
        var options = new PdfValidationOptions { ImageId = "sha256:" + new string('a', 64) };
        switch (setting)
        {
            case "memory": options.MemoryMiB = 0; break;
            case "pids": options.Pids = -1; break;
            case "cpu": options.Cpus = double.NaN; break;
            case "deadline": options.DeadlineSeconds = 0; break;
            case "concurrency": options.MaxConcurrent = 3; break;
            case "output": options.MaxOutputBytes = int.MaxValue; break;
            case "image": options.ImageId = "latest"; break;
        }
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    internal static async Task<PdfValidationOptions> Options(bool fixture = false)
    {
        var image = await Docker("image", "inspect", "--format", "{{.Id}}", fixture ? "tpr10-pdf-fixture:task5" : "tpr10-pdf-validator:task5");
        Assert.StartsWith("sha256:", image.Trim()); return new() { ImageId = image.Trim() };
    }
    private static async Task NoContainers() => Assert.Empty((await Docker("ps", "-aq", "--filter", "label=tpr10.finance-pdf=true")).Trim());
    private static async Task WaitForContainers(int count)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            if ((await Docker("ps", "-q", "--filter", "label=tpr10.finance-pdf=true")).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == count) return;
            await Task.Delay(100);
        }
        Assert.Fail("Expected real worker containers did not start.");
    }
    private static async Task<string> Docker(params string[] args)
    {
        using var process = new Process { StartInfo = new("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        Assert.True(process.Start()); var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
        Assert.True(process.ExitCode == 0, await stderr); return await stdout;
    }
    internal static byte[] Pdf(string catalogExtra = "", bool encrypt = false, byte[]? stream = null, string? filter = null)
    {
        var objects = new[] { $"<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 0 R >>", $"<< /Length {stream?.Length ?? 0} {(filter is null ? "" : "/Filter /" + filter)} >>\nstream\n{(stream is null ? "" : Encoding.Latin1.GetString(stream))}\nendstream" };
        var text = new StringBuilder("%PDF-1.7\n"); var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++) { offsets.Add(Encoding.Latin1.GetByteCount(text.ToString())); text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = Encoding.Latin1.GetByteCount(text.ToString()); text.Append("xref\n0 5\n0000000000 65535 f \n");
        foreach (var offset in offsets) text.Append($"{offset:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size 5 /Root 1 0 R {(encrypt ? "/Encrypt << /Filter /Standard /V 1 /R 2 >>" : "")} >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(text.ToString());
    }
}
