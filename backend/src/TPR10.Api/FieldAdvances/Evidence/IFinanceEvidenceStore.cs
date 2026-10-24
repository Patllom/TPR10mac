using TPR10.Api.FieldAdvances.Payments;
namespace TPR10.Api.FieldAdvances.Evidence;

public interface IFinanceEvidenceStore
{
    Task<EvidenceTicket> WriteImmutableAsync(Guid storageId, string key, Stream content, CancellationToken ct);
    Task<byte[]> ReadVerifiedAsync(Guid storageId, string key, string sha256, long length, CancellationToken ct);
    IEnumerable<EvidenceLocator> Enumerate();
}
public interface IFinancePdfValidator
{
    Task<bool> ValidateAsync(byte[] content, CancellationToken ct);
}
