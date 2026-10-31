using TPR10.Api.Identity.Passwords;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Evidence;

// Diagnostic runs only inside the disposable API container, never against host evidence.
if (args.SequenceEqual(new[] { "--payment-storage-probe" }))
{
    if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") != "Testing" || !File.Exists("/app/appsettings.Testing.json"))
        throw new InvalidOperationException("Requires isolated Testing API configuration");
    var configuration = new ConfigurationBuilder().AddJsonFile("/app/appsettings.json").AddJsonFile("/app/appsettings.Testing.json").Build();
    using var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddFieldAdvances().BuildServiceProvider();
    var options = services.GetRequiredService<FinanceEvidenceOptions>();
    if (options.ActiveStorageId != Guid.Parse("88400000-0000-4000-8000-000000000001") || options.Roots.Count != 1 || options.Roots[options.ActiveStorageId] != "/tmp")
        throw new InvalidOperationException("Unexpected disposable storage configuration");
    options.Validate(); Console.WriteLine("registered root validated");
    var store = services.GetRequiredService<IFinanceEvidenceStore>(); var key = Guid.NewGuid().ToString("N");
    var fileLink = Guid.NewGuid().ToString("N"); var directoryLink = Path.Combine("/tmp", "task13-link-" + Guid.NewGuid().ToString("N"));
    static async Task Reject(Func<Task> operation)
    {
        try { await operation(); } catch (IOException) { return; }
        throw new InvalidOperationException("Secure store accepted a forbidden operation");
    }
    try
    {
        var ticket = await store.WriteImmutableAsync(options.ActiveStorageId, key, new MemoryStream(new byte[] { 1, 2, 3 }), default);
        var bytes = await store.ReadVerifiedAsync(options.ActiveStorageId, key, ticket.Sha256, ticket.Length, default);
        Console.WriteLine($"storage roundtrip bytes={bytes.Length}");
        await Reject(async () => { await store.WriteImmutableAsync(options.ActiveStorageId, key, new MemoryStream(new byte[] { 9 }), default); });
        File.CreateSymbolicLink(Path.Combine("/tmp", fileLink), Path.Combine("/tmp", key));
        await Reject(async () => { await store.ReadVerifiedAsync(options.ActiveStorageId, fileLink, ticket.Sha256, ticket.Length, default); });
        Directory.CreateSymbolicLink(directoryLink, "/tmp"); options.Roots[options.ActiveStorageId] = directoryLink;
        await Reject(async () => { await store.ReadVerifiedAsync(options.ActiveStorageId, key, ticket.Sha256, ticket.Length, default); });
        options.Roots[options.ActiveStorageId] = "/tmp";
        var intact = await store.ReadVerifiedAsync(options.ActiveStorageId, key, ticket.Sha256, ticket.Length, default);
        if (!intact.SequenceEqual(new byte[] { 1, 2, 3 })) throw new InvalidOperationException("Existing evidence overwritten");
        Console.WriteLine("exclusive create, file/root symlink rejection and original digest preserved");
    }
    catch (IOException error) { Console.WriteLine($"storage failure architecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} errno={error.HResult} rootExists={Directory.Exists("/tmp")}"); throw; }
    finally { File.Delete(Path.Combine("/tmp", fileLink)); if (Directory.Exists(directoryLink)) Directory.Delete(directoryLink); File.Delete(Path.Combine("/tmp", key)); }
    return;
}

// Opt-in evidence root for the disposable HTTPS fixture only. Never modify source/deployed config.
if (args.SequenceEqual(new[] { "--field-advance-sql" }) && Environment.GetEnvironmentVariable("TPR10_PAYMENT_E2E") == "1")
{
    var root = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
    var api = Path.Combine(root, "api");
    if (!Path.GetFileName(root).StartsWith("tpr10-tls-smoke-", StringComparison.Ordinal)
        || !File.Exists(Path.Combine(api, "TPR10.Api.dll"))) throw new InvalidOperationException("Requires disposable HTTPS publication");
    // /tmp is private to this disposable API container; no host evidence or NAS is involved.
    var config = new
    {
        FinanceEvidence = new
        {
            ActiveStorageId = "88400000-0000-4000-8000-000000000001",
            Roots = new Dictionary<string, string> { ["88400000-0000-4000-8000-000000000001"] = "/tmp" }
        }
    };
    File.WriteAllText(Path.Combine(api, "appsettings.Testing.json"), System.Text.Json.JsonSerializer.Serialize(config));
}

// Test-only executable, never shipped with API; no HTTP seeding endpoint.
var hash = await new ArgonPasswordHasher().HashAsync("e2e-isolated-password-123", CancellationToken.None);
Console.Write(args.SequenceEqual(new[] { "--scope-sql" }) ? ScopeSeed.Sql(hash)
    : args.SequenceEqual(new[] { "--attendance-sql" }) ? AttendanceSeed.Sql(hash)
    : args.SequenceEqual(new[] { "--field-advance-sql" }) ? FieldAdvanceSeed.Sql() : hash);
