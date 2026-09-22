using System.Text;

namespace TPR10.Api.Identity.Passwords;

public static class UsernameNormalizer
{
    public static string? Normalize(string? username)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > IdentityOptions.MaximumUsernameLength) return null;
        try
        {
            var normalized = username.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();
            return normalized.Length is > 0 and <= IdentityOptions.MaximumNormalizedUsernameLength ? normalized : null;
        }
        catch (ArgumentException) { return null; }
    }
}
