using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace TPR10.Api.Identity.Passwords;

public sealed class ArgonPasswordHasher : IPasswordHasher
{
    private const string Prefix = "$argon2id$v=19$m=65536,t=3,p=1$";
    private static readonly SemaphoreSlim WorkSlots = new(2, 2);

    public async Task<string> HashAsync(string password, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsValidPassword(password))
            throw new ArgumentException("รหัสผ่านต้องมีอักขระ Unicode ที่ถูกต้อง 15–128 ตัว", nameof(password));
        var salt = RandomNumberGenerator.GetBytes(16);
        var digest = await DeriveAsync(password, salt, ct);
        try { return Prefix + Convert.ToBase64String(salt) + "$" + Convert.ToBase64String(digest); }
        finally { CryptographicOperations.ZeroMemory(digest); }
    }

    public async Task<bool> VerifyAsync(string password, string encoded, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsValidPassword(password) || encoded is null || encoded.Length != Prefix.Length + 24 + 1 + 44
            || !encoded.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var parts = encoded[Prefix.Length..].Split('$');
        if (parts.Length != 2 || parts[0].Length != 24 || parts[1].Length != 44) return false;
        var salt = new byte[16];
        var expected = new byte[32];
        if (!Convert.TryFromBase64String(parts[0], salt, out var saltBytes) || saltBytes != salt.Length
            || !Convert.TryFromBase64String(parts[1], expected, out var hashBytes) || hashBytes != expected.Length
            || Convert.ToBase64String(salt) != parts[0] || Convert.ToBase64String(expected) != parts[1]) return false;
        var actual = await DeriveAsync(password, salt, ct);
        try { return CryptographicOperations.FixedTimeEquals(expected, actual); }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(actual);
        }
    }

    private static bool IsValidPassword(string? password)
    {
        if (password is null || password.Length > IdentityOptions.MaximumPasswordScalars * 2) return false;
        var remaining = password.AsSpan();
        var count = 0;
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out var consumed) != OperationStatus.Done) return false;
            count++;
            remaining = remaining[consumed..];
        }
        return count is >= IdentityOptions.MinimumPasswordScalars and <= IdentityOptions.MaximumPasswordScalars;
    }

    private static async Task<byte[]> DeriveAsync(string password, byte[] salt, CancellationToken ct)
    {
        await WorkSlots.WaitAsync(ct);
        var bytes = Encoding.UTF8.GetBytes(password);
        try
        {
            using var argon = new Argon2id(bytes)
            {
                Salt = salt,
                MemorySize = 65536,
                Iterations = 3,
                DegreeOfParallelism = 1
            };
            // ไลบรารีไม่รองรับการยกเลิกขณะคำนวณ จึงคืน slot หลังงานจริงจบเท่านั้น
            var digest = await argon.GetBytesAsync(32);
            if (ct.IsCancellationRequested)
            {
                CryptographicOperations.ZeroMemory(digest);
                ct.ThrowIfCancellationRequested();
            }
            return digest;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            WorkSlots.Release();
        }
    }
}
