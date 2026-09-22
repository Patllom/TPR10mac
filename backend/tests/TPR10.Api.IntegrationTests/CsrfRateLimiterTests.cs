using System.Net;
using Microsoft.AspNetCore.Http;
using TPR10.Api.Identity.Csrf;

namespace TPR10.Api.IntegrationTests;

public sealed class CsrfRateLimiterTests
{
    [Fact]
    public async Task Concurrent_admission_respects_global_and_per_ip_budgets_atomically()
    {
        using var limiter = new CsrfRateLimiter(new CsrfOptions { GlobalPermitLimit = 7, PerIpPermitLimit = 2 }, new ManualTimeProvider());
        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(index => Task.Run(() =>
        {
            var ip = index % 10 + 1;
            using var lease = limiter.AttemptAcquire(Request($"192.0.2.{ip}"));
            return (Ip: ip, Allowed: lease.IsAcquired);
        })));
        Assert.Equal(7, results.Count(x => x.Allowed));
        Assert.All(results.GroupBy(x => x.Ip), group => Assert.InRange(group.Count(x => x.Allowed), 0, 2));
    }

    [Fact]
    public async Task Window_replenishes_both_budgets_and_rejected_traffic_does_not_extend_it()
    {
        var clock = new ManualTimeProvider();
        using var limiter = new CsrfRateLimiter(new CsrfOptions { GlobalPermitLimit = 1, PerIpPermitLimit = 1 }, clock);
        Assert.True(limiter.AttemptAcquire(Request("192.0.2.1")).IsAcquired);
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False((await limiter.AcquireAsync(Request("192.0.2.1"))).IsAcquired);
        Assert.False(limiter.AttemptAcquire(Request("192.0.2.2")).IsAcquired);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await limiter.AcquireAsync(Request("192.0.2.1"))).IsAcquired);
        Assert.False(limiter.AttemptAcquire(Request("192.0.2.2")).IsAcquired);
    }

    [Fact]
    public void Ipv4_mapped_addresses_share_the_same_budget_and_safe_get_is_exempt()
    {
        using var limiter = new CsrfRateLimiter(new CsrfOptions { GlobalPermitLimit = 2, PerIpPermitLimit = 1 }, new ManualTimeProvider());
        Assert.True(limiter.AttemptAcquire(Request("192.0.2.1")).IsAcquired);
        Assert.False(limiter.AttemptAcquire(Request("::ffff:192.0.2.1")).IsAcquired);
        var health = Request("192.0.2.1");
        health.Request.Method = HttpMethods.Get;
        Assert.True(limiter.AttemptAcquire(health).IsAcquired);
        Assert.True(limiter.AttemptAcquire(Request("192.0.2.2")).IsAcquired);
    }

    private static DefaultHttpContext Request(string address)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        context.Request.Method = HttpMethods.Post;
        return context;
    }
}
