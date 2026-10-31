namespace TPR10.Api.FieldAdvances.Evidence;

public sealed class FinanceEvidenceOptions
{
    public Guid ActiveStorageId { get; set; }
    public Dictionary<Guid, string> Roots { get; set; } = [];
    public long MaxFileBytes { get; set; } = 10 * 1024 * 1024;
    public int MaxFiles { get; set; } = 10;
    public long MaxTotalBytes { get; set; } = 50 * 1024 * 1024;
    public long MaxImagePixels { get; set; } = 25_000_000;
    public void Validate()
    {
        if (MaxFileBytes is < 1 or > 10 * 1024 * 1024 || MaxFiles is < 1 or > 10
            || MaxTotalBytes is < 1 or > 50 * 1024 * 1024 || MaxImagePixels is < 1 or > 25_000_000
            || ActiveStorageId == Guid.Empty || !Roots.ContainsKey(ActiveStorageId)
            || Roots.Any(x => x.Key == Guid.Empty || string.IsNullOrWhiteSpace(x.Value) || !Path.IsPathFullyQualified(x.Value)))
            throw new InvalidOperationException("Finance evidence storage is not configured with bounded limits and absolute roots.");
    }
}

public readonly record struct EvidenceLocator(Guid StorageId, string Key)
{
    public override string ToString() => $"v1:{StorageId:N}:{Key}";
    public static bool TryParse(string value, out EvidenceLocator locator)
    {
        locator = default;
        if (value is null || value.Length != 68 || !value.StartsWith("v1:", StringComparison.Ordinal) || value[35] != ':') return false;
        var storage = value.Substring(3, 32); var key = value[36..];
        if (!IsKey(storage) || !IsKey(key)) return false;
        locator = new(Guid.ParseExact(storage, "N"), key); return true;
    }
    internal static bool IsKey(string key) => key is { Length: 32 } && Guid.TryParseExact(key, "N", out var id)
        && id != Guid.Empty && id.ToString("N") == key;
}
