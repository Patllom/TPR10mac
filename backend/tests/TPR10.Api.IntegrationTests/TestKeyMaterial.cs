using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TPR10.Api.IntegrationTests;

internal sealed class TestKeyMaterial : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("tpr10-test-keys-").FullName;
    public Dictionary<string, string?> Settings { get; }

    public TestKeyMaterial()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=TPR10 test key encryption", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var certificatePath = Path.Combine(root, "test.pfx");
        File.WriteAllBytes(certificatePath, certificate.Export(X509ContentType.Pfx));
        Settings = new()
        {
            ["Identity:Csrf:AllowedOrigins:0"] = "https://localhost:4443",
            ["Identity:Csrf:KeyRingPath"] = Path.Combine(root, "keys"),
            ["Identity:Csrf:CertificatePath"] = certificatePath
        };
    }

    public string ReadKeyXml() => string.Join("\n", Directory.GetFiles(Path.Combine(root, "keys"), "*.xml").Select(File.ReadAllText));
    public void Dispose() => Directory.Delete(root, recursive: true);
}
