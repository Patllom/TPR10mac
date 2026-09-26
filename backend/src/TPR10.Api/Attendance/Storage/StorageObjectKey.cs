namespace TPR10.Api.Attendance.Storage;

public static class StorageObjectKey
{
    public static string Create(Guid id, string variant)
    {
        if (id == Guid.Empty || variant is not ("full" or "thumbnail")) throw new IOException("storage-key-invalid");
        var text = id.ToString("N");
        return $"objects/{text[..2]}/{text}/{variant}.jpg";
    }

    internal static string[] Parse(string key)
    {
        var parts = key.Split('/');
        if (parts.Length != 4 || parts[0] != "objects" || parts[1].Length != 2 || parts[2].Length != 32 ||
            parts[2].Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) || parts[1] != parts[2][..2] ||
            !Guid.TryParseExact(parts[2], "N", out var id) || id == Guid.Empty || parts[3] is not ("full.jpg" or "thumbnail.jpg"))
            throw new IOException("storage-key-invalid");
        return parts;
    }
}
