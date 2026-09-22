using TPR10.Api.Identity;
using TPR10.Api.Identity.Passwords;

namespace TPR10.Api.IntegrationTests;

public sealed class PasswordTests
{
    private readonly IPasswordHasher hasher = new ArgonPasswordHasher();
    private const string Password = "ทดสอบรหัสผ่าน-123456";

    [Fact]
    public async Task Verify_accepts_independently_derived_argon2id_vector()
    {
        // สร้างอิสระด้วย Python cryptography/OpenSSL: salt=bytes(range(16)), m=65536,t=3,p=1,length=32
        const string encoded = "$argon2id$v=19$m=65536,t=3,p=1$AAECAwQFBgcICQoLDA0ODw==$RK4b4ObhCe9rljgee6qGO59YVFpIP0ptbtO/4h0LNPU=";
        Assert.True(await hasher.VerifyAsync("independent-vector-password", encoded, default));
        Assert.False(await hasher.VerifyAsync("independent-vector-password-changed", encoded, default));
    }

    [Fact]
    public async Task Hash_is_salted_and_rejects_wrong_password()
    {
        var a = await hasher.HashAsync(Password, default);
        var b = await hasher.HashAsync(Password, default);
        Assert.NotEqual(a, b);
        Assert.StartsWith("$argon2id$v=19$m=65536,t=3,p=1$", a);
        Assert.DoesNotContain(Password, a);
        Assert.True(await hasher.VerifyAsync(Password, a, default));
        Assert.False(await hasher.VerifyAsync("wrong-password-123456", a, default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("$argon2i$v=19$m=65536,t=3,p=1$AA==$AA==")]
    [InlineData("$argon2id$v=16$m=65536,t=3,p=1$AA==$AA==")]
    [InlineData("$argon2id$v=19$m=2147483647,t=3,p=1$AA==$AA==")]
    [InlineData("$argon2id$v=19$m=65536,t=999999,p=1$AA==$AA==")]
    [InlineData("$argon2id$v=19$m=65536,t=3,p=1$not-base64$invalid")]
    [InlineData("$argon2id$v=19$m=65536,t=3,p=1$AA==$AA==")]
    public async Task Malformed_or_unapproved_hash_is_rejected(string encoded)
        => Assert.False(await hasher.VerifyAsync(Password, encoded, default));

    [Fact]
    public async Task Oversized_hash_is_rejected()
        => Assert.False(await hasher.VerifyAsync(Password, new string('A', 10000), default));

    [Fact]
    public async Task Password_is_not_trimmed_or_unicode_normalized()
    {
        const string password = " e\u0301-password-123456 ";
        var encoded = await hasher.HashAsync(password, default);
        Assert.True(await hasher.VerifyAsync(password, encoded, default));
        Assert.False(await hasher.VerifyAsync(password.Trim(), encoded, default));
        Assert.False(await hasher.VerifyAsync(password.Normalize(), encoded, default));
    }

    [Theory]
    [InlineData(15)]
    [InlineData(128)]
    public async Task Password_length_counts_unicode_scalars_not_utf16_units(int count)
    {
        var password = string.Concat(Enumerable.Repeat("\U0001f512", count));
        var encoded = await hasher.HashAsync(password, default);
        Assert.True(await hasher.VerifyAsync(password, encoded, default));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(14)]
    [InlineData(129)]
    public async Task New_password_outside_length_policy_is_rejected(int count)
        => await Assert.ThrowsAsync<ArgumentException>(() => hasher.HashAsync(new string('x', count), default));

    [Fact]
    public async Task Invalid_utf16_is_not_silently_replaced()
    {
        var invalid = new string('x', 15) + '\ud800';
        await Assert.ThrowsAsync<ArgumentException>(() => hasher.HashAsync(invalid, default));
        Assert.False(await hasher.VerifyAsync(invalid, "$argon2id$invalid", default));
    }

    [Fact]
    public async Task Cancelled_operation_does_not_hash()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hasher.HashAsync(Password, cts.Token));
    }
}
