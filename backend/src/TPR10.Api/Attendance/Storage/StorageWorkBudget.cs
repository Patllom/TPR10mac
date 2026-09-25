namespace TPR10.Api.Attendance.Storage;

internal static class StorageWorkBudget
{
    private static readonly SemaphoreSlim Admission = new(10, 10);
    private static readonly SemaphoreSlim Active = new(2, 2);

    public static async Task<T> Run<T>(TimeProvider clock, CancellationToken ct, Func<CancellationToken, T> action)
    {
        ct.ThrowIfCancellationRequested();
        if (!Admission.Wait(0)) throw new IOException("storage-busy");
        var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10), clock);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var token = linked.Token;
        var worker = Task.Run(async () =>
        {
            var acquired = false;
            try
            {
                await Active.WaitAsync(token).ConfigureAwait(false);
                acquired = true;
                token.ThrowIfCancellationRequested();
                return action(token);
            }
            finally
            {
                if (acquired) Active.Release();
                Admission.Release();
            }
        });
        try { return await worker.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("storage-timeout"); }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException or PlatformNotSupportedException)
        { throw new IOException("storage-runtime-unsupported"); }
        finally
        {
            _ = worker.ContinueWith(done =>
            {
                _ = done.Exception;
                linked.Dispose();
                deadline.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
