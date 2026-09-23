using TPR10.Api.Data;
using TPR10.Api.Identity.Data;

namespace TPR10.Api.Identity.Reset;

// Enqueue only: the caller owns the credential/request/audit transaction.
public sealed class ResetDelivery(Tpr10DbContext db, TimeProvider clock) : IResetDelivery
{
    public Task EnqueueAsync(Guid requestId, string recipient, string protectedPayload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Delivery requires a transaction.");
        db.Add(new IdentityDeliveryOutbox
        {
            Id = Guid.NewGuid(),
            RequestId = requestId,
            Recipient = recipient,
            ProtectedPayload = protectedPayload,
            CreatedAtUtc = clock.GetUtcNow()
        });
        return Task.CompletedTask;
    }
}
