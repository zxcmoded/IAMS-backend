using System.Security.Cryptography;

namespace IAMS.Api.Common.Security;

/// <summary>Generates high-entropy opaque tokens and deterministically hashes them for storage.</summary>
public static class TokenGenerator
{
    /// <summary>A URL-safe, high-entropy random token (used for refresh tokens).</summary>
    public static string NewOpaqueToken(int bytes = 32) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>
    /// Deterministic SHA-256 hash (hex) for storing high-entropy tokens so a DB leak does not expose
    /// the raw token. Deterministic on purpose: refresh tokens AND Activation Keys are looked up by hash
    /// (see <see cref="IAMS.Api.Common.Domain.User.ActivationKeyHash"/>) — never stored or logged in the clear.
    /// </summary>
    public static string Sha256(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
