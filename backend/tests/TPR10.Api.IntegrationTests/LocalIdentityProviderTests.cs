using Microsoft.EntityFrameworkCore;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Passwords;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class LocalIdentityProviderTests(PostgresFixture postgres)
{
    private const string Password = "รหัสทดสอบยาวพอ-123456";

    [Theory]
    [InlineData(" staff ", "STAFF")]
    [InlineData("Ｓｔａｆｆ", "STAFF")]
    [InlineData("e\u0301", "É")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Username_normalization_is_stable(string? input, string? expected)
        => Assert.Equal(expected, UsernameNormalizer.Normalize(input));

    [Fact]
    public void Oversized_and_invalid_username_is_rejected()
    {
        Assert.Null(UsernameNormalizer.Normalize(new string('x', 129)));
        Assert.Null(UsernameNormalizer.Normalize("broken" + '\ud800'));
    }

    [Theory]
    [InlineData(true, false, false, " staff ", Password, true)]
    [InlineData(true, false, true, "ＳＴＡＦＦ", Password, true)]
    [InlineData(false, false, false, "staff", Password, false)]
    [InlineData(true, true, false, "staff", Password, false)]
    [InlineData(true, false, false, "unknown", Password, false)]
    [InlineData(true, false, false, "staff", "wrong-password-123456", false)]
    public async Task Provider_returns_only_eligible_local_identity(bool active, bool locked,
        bool mustChange, string username, string password, bool success)
    {
        await using var database = new IdentityDatabase(postgres.ConnectionString);
        await database.InitializeAsync();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var id = Guid.NewGuid();
        var hasher = new ArgonPasswordHasher();
        db.Add(new IdentityUser { Id = id, Username = "staff", NormalizedUsername = "STAFF", IsActive = active });
        db.Add(new LocalCredential
        {
            UserId = id,
            PasswordHash = await hasher.HashAsync(Password, default),
            MustChangePassword = mustChange,
            LockedUntilUtc = locked ? DateTimeOffset.UtcNow.AddHours(1) : null
        });
        await db.SaveChangesAsync();
        var provider = new LocalIdentityProvider(db, hasher, TimeProvider.System);
        var result = await provider.VerifyAsync(username, password, default);
        if (success)
        {
            Assert.NotNull(result);
            Assert.Equal(id, result.UserId);
            Assert.Equal(mustChange, result.MustChangePassword);
        }
        else Assert.Null(result);
    }

    [Fact]
    public async Task Normalized_alias_cannot_create_a_second_account()
    {
        await using var database = new IdentityDatabase(postgres.ConnectionString);
        await database.InitializeAsync();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        db.Add(new IdentityUser { Id = Guid.NewGuid(), Username = "staff", NormalizedUsername = UsernameNormalizer.Normalize("staff")! });
        await db.SaveChangesAsync();
        db.Add(new IdentityUser { Id = Guid.NewGuid(), Username = "ＳＴＡＦＦ", NormalizedUsername = UsernameNormalizer.Normalize(" ＳＴＡＦＦ ")! });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23505", Assert.IsType<Npgsql.PostgresException>(error.InnerException).SqlState);
    }
}
