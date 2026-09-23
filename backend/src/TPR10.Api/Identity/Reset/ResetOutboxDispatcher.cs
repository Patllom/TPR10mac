using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using TPR10.Api.Data;
using TPR10.Api.Identity.Data;

namespace TPR10.Api.Identity.Reset;

public interface IResetTransport
{
    Task SendAsync(Guid requestId, string recipient, string token, CancellationToken ct);
}

public sealed class ResetOutboxDispatcher(Tpr10DbContext db, IDataProtectionProvider protection, IResetTransport transport, TimeProvider clock)
{
    public async Task<bool> DispatchAsync(Guid requestId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct);
        var row = await db.Set<IdentityDeliveryOutbox>().FromSqlInterpolated($"SELECT * FROM identity_delivery_outbox WHERE request_id={requestId} FOR UPDATE").SingleOrDefaultAsync(ct);
        var now = clock.GetUtcNow();
        if (row is null || row.DeliveredAtUtc is not null || row.NextAttemptAtUtc > now || row.Attempts >= 5) return false;
        var request = await db.Set<PasswordResetRequest>().AsNoTracking().SingleAsync(x => x.Id == requestId, ct);
        if (request.ConsumedAtUtc is not null || request.RevokedAtUtc is not null || request.ExpiresAtUtc <= now
            || !await db.Set<IdentityUser>().AnyAsync(x => x.Id == request.UserId && x.IsActive && x.Email == row.Recipient, ct)) return false;
        row.Attempts++;
        var delivered = false;
        try
        {
            var token = protection.CreateProtector("TPR10.Reset.Delivery.v1", requestId.ToString("D")).Unprotect(row.ProtectedPayload);
            await transport.SendAsync(requestId, row.Recipient, token, ct);
            row.DeliveredAtUtc = now;
            row.NextAttemptAtUtc = null;
            delivered = true;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or CryptographicException)
        {
            // Never log a transport exception: third-party messages may contain credentials.
            row.NextAttemptAtUtc = now.AddMinutes(1);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return delivered;
    }
}
