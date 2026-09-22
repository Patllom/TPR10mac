using System.Threading.RateLimiting;

namespace TPR10.Api.Identity.Csrf;

// หนึ่ง admission หักทั้งสอง budget พร้อมกัน; request ที่ถูกปฏิเสธไม่จอง IP partition
public sealed class CsrfRateLimiter(CsrfOptions policy, TimeProvider clock) : PartitionedRateLimiter<HttpContext>
{
    private readonly object gate = new();
    private readonly Dictionary<string, int> admittedByIp = new(StringComparer.Ordinal);
    private long windowStarted = clock.GetTimestamp();
    private int admitted;
    private bool disposed;

    public override RateLimiterStatistics? GetStatistics(HttpContext resource) => null;

    protected override RateLimitLease AttemptAcquireCore(HttpContext resource, int permitCount)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!CsrfMiddleware.IsIssuer(resource) && !CsrfMiddleware.IsUnsafe(resource.Request.Method)) return Lease.Accepted;
            var timestamp = clock.GetTimestamp();
            if (clock.GetElapsedTime(windowStarted, timestamp) >= TimeSpan.FromMinutes(1))
            {
                admittedByIp.Clear();
                admitted = 0;
                windowStarted = timestamp;
            }
            var address = resource.Connection.RemoteIpAddress;
            if (address?.IsIPv4MappedToIPv6 == true) address = address.MapToIPv4();
            var ip = address?.ToString() ?? "unknown";
            admittedByIp.TryGetValue(ip, out var ipAdmitted);
            if (policy.PerIpPermitLimit - ipAdmitted < Math.Max(1, permitCount)
                || policy.GlobalPermitLimit - admitted < Math.Max(1, permitCount)) return Lease.Rejected;
            if (permitCount > 0)
            {
                admitted += permitCount;
                admittedByIp[ip] = ipAdmitted + permitCount;
            }
            // จำนวน entries <= จำนวน admissions <= GlobalPermitLimit และล้างเมื่อเปลี่ยน window
            return Lease.Accepted;
        }
    }

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(HttpContext resource, int permitCount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(AttemptAcquireCore(resource, permitCount));
    }

    protected override void Dispose(bool disposing)
    {
        lock (gate) { disposed = true; admittedByIp.Clear(); }
        base.Dispose(disposing);
    }

    private sealed class Lease(bool acquired) : RateLimitLease
    {
        public static readonly Lease Accepted = new(true);
        public static readonly Lease Rejected = new(false);
        public override bool IsAcquired => acquired;
        public override IEnumerable<string> MetadataNames => [];
        public override bool TryGetMetadata(string metadataName, out object? metadata) { metadata = null; return false; }
    }
}
