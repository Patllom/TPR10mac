using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TPR10.Api.Data;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Sessions;

namespace TPR10.Api.Identity.Csrf;

public sealed class CsrfService(Tpr10DbContext db, IDataProtectionProvider protection, TimeProvider clock, IOptions<CsrfOptions> options, RequestSession session)
{
    public const string CookieName = "__Host-tpr10_preauth";
    public const string SessionCookieName = "__Host-tpr10_session";
    public const string HeaderName = "X-CSRF-Token";
    private const string Purpose = "csrf-preauth";
    private readonly IDataProtector protector = protection.CreateProtector("TPR10.Identity.Csrf.v1");

    public bool HasValidTransport(HttpContext context, bool requireOrigin)
        => HasTrustedTransport(context, requireOrigin)
            && (!context.Request.Cookies.ContainsKey(SessionCookieName) || session.Entity is not null);

    public bool HasTrustedTransport(HttpContext context, bool requireOrigin)
    {
        var request = context.Request;
        if (!request.IsHttps) return false;
        var authority = $"https://{request.Host.Value}";
        if (!options.Value.AllowedOrigins.Contains(authority, StringComparer.OrdinalIgnoreCase)) return false;
        var origin = request.Headers.Origin;
        if (origin.Count == 0) return !requireOrigin && request.Headers["Sec-Fetch-Site"] != "cross-site";
        return origin.Count == 1 && string.Equals(origin[0], authority, StringComparison.OrdinalIgnoreCase)
            && request.Headers["Sec-Fetch-Site"] != "cross-site";
    }

    public async Task<string> IssueAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (session.Entity is { } active)
            return protector.Protect(JsonSerializer.Serialize(new Payload(active.Id, "csrf-session",
                (active.ExpiresAtUtc < now.AddMinutes(options.Value.LifetimeMinutes) ? active.ExpiresAtUtc : now.AddMinutes(options.Value.LifetimeMinutes)).ToUnixTimeSeconds())));
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Serialize capacity checks across API instances; no business or audit rows are removed.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241003)", cancellationToken);
        await db.Set<PreAuthFlow>().Where(x => x.ExpiresAtUtc <= now || x.ConsumedAtUtc != null || x.RevokedAtUtc != null)
            .ExecuteDeleteAsync(cancellationToken);
        var hash = CookieHash(context);
        var flow = hash is null ? null : await db.Set<PreAuthFlow>().SingleOrDefaultAsync(x => x.TokenHash == hash && x.Purpose == Purpose, cancellationToken);
        string? cookie = null;
        if (flow is null)
        {
            if (await db.Set<PreAuthFlow>().CountAsync(cancellationToken) >= options.Value.MaxActiveFlows)
                throw new PreAuthCapacityException();
            var random = RandomNumberGenerator.GetBytes(32);
            cookie = WebEncoders.Base64UrlEncode(random);
            flow = new PreAuthFlow
            {
                Id = Guid.NewGuid(),
                TokenHash = SHA256.HashData(random),
                Purpose = Purpose,
                CreatedAtUtc = now,
                ExpiresAtUtc = now.AddMinutes(options.Value.LifetimeMinutes)
            };
            db.Add(flow);
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        if (cookie is not null)
            context.Response.Cookies.Append(CookieName, cookie, new CookieOptions
            { Secure = true, HttpOnly = true, Path = "/", SameSite = SameSiteMode.Lax, Expires = flow.ExpiresAtUtc, IsEssential = true });
        return protector.Protect(JsonSerializer.Serialize(new Payload(flow.Id, Purpose, flow.ExpiresAtUtc.ToUnixTimeSeconds())));
    }

    public async Task<bool> ValidateAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (!HasValidTransport(context, requireOrigin: true)) return false;
        var header = context.Request.Headers[HeaderName];
        if (header.Count != 1 || header[0] is not { Length: > 0 and <= 2048 } token) return false;
        Payload? payload;
        try { payload = JsonSerializer.Deserialize<Payload>(protector.Unprotect(token)); }
        catch (Exception error) when (error is CryptographicException or JsonException) { return false; }
        var now = clock.GetUtcNow();
        if (payload is null || payload.Expires <= now.ToUnixTimeSeconds()) return false;
        if (session.Entity is { } active) return payload.Purpose == "csrf-session" && payload.Id == active.Id;
        var hash = CookieHash(context);
        if (hash is null || payload.Purpose != Purpose) return false;
        return await db.Set<PreAuthFlow>().AnyAsync(x => x.Id == payload.Id && x.TokenHash == hash && x.Purpose == Purpose
            && x.ExpiresAtUtc > now && x.ConsumedAtUtc == null && x.RevokedAtUtc == null, cancellationToken);
    }

    public async Task<bool> ConsumePreAuthAsync(HttpContext context, CancellationToken ct)
    {
        var hash = CookieHash(context);
        var now = clock.GetUtcNow();
        return hash is not null && await db.Set<PreAuthFlow>().Where(x => x.TokenHash == hash && x.Purpose == Purpose
            && x.ExpiresAtUtc > now && x.ConsumedAtUtc == null && x.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ConsumedAtUtc, now), ct) == 1;
    }

    private static byte[]? CookieHash(HttpContext context)
    {
        if (context.Request.Cookies[CookieName] is not { Length: 43 } cookie) return null;
        try
        {
            var bytes = WebEncoders.Base64UrlDecode(cookie);
            return bytes.Length == 32 && WebEncoders.Base64UrlEncode(bytes) == cookie ? SHA256.HashData(bytes) : null;
        }
        catch (FormatException) { return null; }
    }

    private sealed record Payload(Guid Id, string Purpose, long Expires);
}

public sealed class PreAuthCapacityException : Exception;
