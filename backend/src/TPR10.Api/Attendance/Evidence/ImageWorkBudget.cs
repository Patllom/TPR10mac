namespace TPR10.Api.Attendance.Evidence;

internal static class ImageWorkBudget
{
    private static readonly SemaphoreSlim Admission = new(10, 10);
    private static readonly SemaphoreSlim Active = new(2, 2);

    public static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, TimeProvider timeProvider, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Admission.Wait(0)) throw new ImageProcessingException("image-busy");
        var deadline = new CancellationTokenSource(ImageLimits.Deadline, timeProvider);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var token = linked.Token;
        // The worker, not its caller, owns the permit until native work/read really ends.
        var worker = Task.Run(async () =>
        {
            var acquired = false;
            try
            {
                await Active.WaitAsync(token).ConfigureAwait(false);
                acquired = true;
                token.ThrowIfCancellationRequested();
                return await action(token).ConfigureAwait(false);
            }
            finally
            {
                if (acquired) Active.Release();
                Admission.Release();
            }
        });
        try
        {
            return await worker.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ImageProcessingException("image-timeout");
        }
        finally
        {
            _ = worker.ContinueWith(completed =>
            {
                _ = completed.Exception;
                linked.Dispose();
                deadline.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
