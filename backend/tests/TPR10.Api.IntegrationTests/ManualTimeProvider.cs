namespace TPR10.Api.IntegrationTests;

public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => now;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => now.UtcTicks;
    public void Advance(TimeSpan duration) => now += duration;
}
