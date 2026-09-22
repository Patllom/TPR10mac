namespace TPR10.Api.Data.Entities;

public sealed class TechnicalProbe
{
    public Guid Id { get; set; }
    public required string Note { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public required string CorrelationId { get; set; }
}
