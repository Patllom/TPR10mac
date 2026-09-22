namespace TPR10.Api.Identity.Csrf;

public sealed class CsrfOptions
{
    public string[] AllowedOrigins { get; set; } = [];
    public int LifetimeMinutes { get; set; } = 10;
    public int MaxActiveFlows { get; set; } = 10_000;
    public int PerIpPermitLimit { get; set; } = 20;
    public int GlobalPermitLimit { get; set; } = 600;
    public string? KeyRingPath { get; set; }
    public string? CertificatePath { get; set; }
    public string? CertificatePassword { get; set; }
}
