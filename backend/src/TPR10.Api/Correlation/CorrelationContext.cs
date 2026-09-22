namespace TPR10.Api.Correlation;

public sealed class CorrelationContext : ICorrelationContext
{
    public Guid CorrelationId { get; private set; }
    private bool initialized;
    public void Initialize(Guid value)
    {
        if (initialized) throw new InvalidOperationException("Correlation is already initialized.");
        CorrelationId = value;
        initialized = true;
    }
}
