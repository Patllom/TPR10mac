using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography.X509Certificates;

namespace TPR10.Api.Identity.Csrf;

public static class CsrfRegistration
{
    public static void AddPreAuthCsrf(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var development = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        services.AddOptions<CsrfOptions>().Bind(configuration.GetSection("Identity:Csrf"))
            .PostConfigure(x =>
            {
                if (development && !configuration.GetSection("Identity:Csrf:AllowedOrigins").Exists())
                    x.AllowedOrigins = ["https://localhost:4443"];
            })
            .Validate(x => x.AllowedOrigins.Length > 0 && x.AllowedOrigins.All(IsOrigin), "CSRF requires exact HTTPS origins without paths")
            .Validate(x => x.LifetimeMinutes is >= 1 and <= 30 && x.MaxActiveFlows is >= 1 and <= 100_000
                && x.PerIpPermitLimit is >= 1 and <= 10_000 && x.GlobalPermitLimit is >= 1 and <= 100_000, "CSRF bounds are invalid")
            .Validate(x => development || configuration.GetSection("Identity:Csrf:AllowedOrigins").Exists(), "Production requires explicit origins")
            .Validate(x => (development && x.KeyRingPath is null && x.CertificatePath is null)
                || (x.KeyRingPath is not null && Path.IsPathFullyQualified(x.KeyRingPath)
                    && x.CertificatePath is not null && Path.IsPathFullyQualified(x.CertificatePath) && File.Exists(x.CertificatePath)),
                "Persistent CSRF keys require an absolute key-ring directory and an encryption certificate")
            .ValidateOnStart();
        services.AddSingleton<X509Certificate2>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<CsrfOptions>>().Value;
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(options.CertificatePath!, options.CertificatePassword,
                OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
            if (!certificate.HasPrivateKey)
            {
                certificate.Dispose();
                throw new InvalidOperationException("Data-protection certificate requires a private key");
            }
            return certificate;
        });
        services.AddSingleton<IDataProtectionProvider>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<CsrfOptions>>().Value;
            if (options.KeyRingPath is null) return new EphemeralDataProtectionProvider();
            var certificate = provider.GetRequiredService<X509Certificate2>();
            return DataProtectionProvider.Create(new DirectoryInfo(options.KeyRingPath), builder =>
                builder.SetApplicationName("TPR10.Identity").ProtectKeysWithCertificate(certificate));
        });
        services.AddScoped<CsrfService>();
        services.AddCsrfRateLimiting();
    }

    private static bool IsOrigin(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
        && value.Equals(uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    public static void AddCsrfRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(_ => { });
        services.AddOptions<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>()
            .Configure<IOptions<CsrfOptions>, TimeProvider>((limiter, configured, clock) =>
            {
                limiter.GlobalLimiter = new CsrfRateLimiter(configured.Value, clock);
                limiter.OnRejected = async (context, cancellationToken) =>
                {
                    context.HttpContext.Response.Headers.CacheControl = "no-store";
                    context.HttpContext.Response.Headers.RetryAfter = "60";
                    await Results.Problem(statusCode: 429, title: "ส่งคำขอถี่เกินไป กรุณาลองใหม่ภายหลัง").ExecuteAsync(context.HttpContext);
                };
            });
    }
}
