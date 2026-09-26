using TPR10.Api.Attendance.Evidence;

namespace TPR10.Api.IntegrationTests;

[CollectionDefinition("image-processing", DisableParallelization = true)]
public sealed class ImageProcessingCollection;

[Collection("image-processing")]
public sealed class EvidenceImageBudgetTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_or_deadline_returns_promptly_but_holds_native_slot_until_work_finishes(bool timeout)
    {
        using var clock = new ManualClock();
        var service = new ImageStampService(clock);
        using var cancel = new CancellationTokenSource();
        using var first = new HeldRead();
        using var second = new HeldRead();
        var stamp = new StampRequest(DateTimeOffset.Parse("2026-09-24T17:00:00Z"), EvidenceAction.CheckIn);
        var firstTask = service.StampAsync(first, stamp, cancel.Token);
        var secondTask = new ImageStampService(TimeProvider.System).StampAsync(second, stamp, CancellationToken.None);
        var queued = new List<Task<StampedImage>>();
        var inputs = new List<MemoryStream>();
        try
        {
            await Task.WhenAll(first.Entered.Task, second.Entered.Task).WaitAsync(TimeSpan.FromSeconds(3));
            for (var i = 0; i < 8; i++)
            {
                var input = new MemoryStream(StampFixtures.Image(100, 80, false));
                inputs.Add(input);
                queued.Add(new ImageStampService(TimeProvider.System).StampAsync(input, stamp, CancellationToken.None));
            }
            await Busy();
            if (timeout)
            {
                clock.Fire();
                var error = await Assert.ThrowsAsync<ImageProcessingException>(() => firstTask.WaitAsync(TimeSpan.FromSeconds(2)));
                Assert.Equal("image-timeout", error.Code);
                Assert.Equal(503, error.StatusCode);
            }
            else
            {
                cancel.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstTask.WaitAsync(TimeSpan.FromSeconds(2)));
            }
            // Neither a returned caller nor a new service instance creates a new native permit.
            await Busy();
            Assert.All(inputs, input => Assert.Equal(0, input.Position));
        }
        finally
        {
            first.Release.TrySetResult();
            second.Release.TrySetResult();
            await Record.ExceptionAsync(() => firstTask);
            await Record.ExceptionAsync(() => secondTask);
            await Task.WhenAll(queued).WaitAsync(TimeSpan.FromSeconds(8));
            foreach (var input in inputs) input.Dispose();
        }

        async Task Busy()
        {
            using var input = new MemoryStream([1]);
            var error = await Assert.ThrowsAsync<ImageProcessingException>(() => service.StampAsync(input, stamp, CancellationToken.None));
            Assert.Equal("image-busy", error.Code);
            Assert.Equal(429, error.StatusCode);
            Assert.Equal(5, error.RetryAfterSeconds);
        }
    }

    [Fact]
    public async Task Already_cancelled_request_does_not_read_source()
    {
        using var input = new HeldRead();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ImageStampService(TimeProvider.System).StampAsync(input,
            new StampRequest(DateTimeOffset.UtcNow, EvidenceAction.CheckIn), cancel.Token));
        Assert.False(input.Entered.Task.IsCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Queued_cancel_or_timeout_never_reads_input_and_returns_admission(bool timeout)
    {
        var service = new ImageStampService(TimeProvider.System);
        var stamp = new StampRequest(DateTimeOffset.Parse("2026-09-24T17:00:00Z"), EvidenceAction.CheckIn);
        using var first = new HeldRead();
        using var second = new HeldRead();
        var active = new[] { service.StampAsync(first, stamp, CancellationToken.None), service.StampAsync(second, stamp, CancellationToken.None) };
        var pending = new List<Task<StampedImage>>();
        var inputs = new List<MemoryStream>();
        using var clock = new ManualClock();
        using var cancel = new CancellationTokenSource();
        using var cancelledInput = new MemoryStream(StampFixtures.Image(100, 80, true));
        Task<StampedImage>? cancelled = null;
        try
        {
            await Task.WhenAll(first.Entered.Task, second.Entered.Task).WaitAsync(TimeSpan.FromSeconds(3));
            for (var i = 0; i < 7; i++)
            {
                var input = new MemoryStream(StampFixtures.Image(100, 80, true));
                inputs.Add(input);
                pending.Add(service.StampAsync(input, stamp, CancellationToken.None));
            }
            cancelled = new ImageStampService(clock).StampAsync(cancelledInput, stamp, cancel.Token);
            using var probe = new MemoryStream([1]);
            Assert.Equal("image-busy", (await Assert.ThrowsAsync<ImageProcessingException>(() => service.StampAsync(probe, stamp, CancellationToken.None))).Code);
            if (timeout)
            {
                clock.Fire();
                Assert.Equal("image-timeout", (await Assert.ThrowsAsync<ImageProcessingException>(() => cancelled.WaitAsync(TimeSpan.FromSeconds(2)))).Code);
            }
            else
            {
                cancel.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(TimeSpan.FromSeconds(2)));
            }
            var replacementInput = new MemoryStream(StampFixtures.Image(100, 80, true));
            inputs.Add(replacementInput);
            Task<StampedImage>? replacement = null;
            var until = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < until)
            {
                var attempt = service.StampAsync(replacementInput, stamp, CancellationToken.None);
                if (!attempt.IsCompleted) { replacement = attempt; break; }
                Assert.Equal("image-busy", (await Assert.ThrowsAsync<ImageProcessingException>(() => attempt)).Code);
                await Task.Delay(10); // Wait for worker cleanup, not for a guessed native processing duration.
            }
            Assert.NotNull(replacement);
            pending.Add(replacement);
            Assert.Equal(0, cancelledInput.Position);
            Assert.All(inputs, input => Assert.Equal(0, input.Position));
        }
        finally
        {
            first.Release.TrySetResult();
            second.Release.TrySetResult();
            foreach (var task in active) await Record.ExceptionAsync(() => task);
            if (cancelled is not null) await Record.ExceptionAsync(() => cancelled);
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(8));
            foreach (var input in inputs) input.Dispose();
        }
        Assert.Equal(0, cancelledInput.Position);
    }

    private sealed class HeldRead : Stream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task; // Simulates native/read work which cannot be cancelled.
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ManualClock : TimeProvider, IDisposable
    {
        private ManualTimer? timer;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(TimeSpan.FromSeconds(10), dueTime);
            return timer = new ManualTimer(callback, state);
        }
        public void Fire() => timer!.Fire();
        public void Dispose() => timer?.Dispose();
        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            public void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
