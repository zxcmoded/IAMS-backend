using System.Security.Cryptography;

namespace IAMS.Api.Common.Security;

/// <summary>Generates high-entropy opaque tokens and deterministically hashes them for storage.</summary>
public static class TokenGenerator
{
    /// <summary>A URL-safe, high-entropy random token (used for refresh tokens and 2FA challenge tokens).</summary>
    public static string NewOpaqueToken(int bytes = 32) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>
    /// Deterministic SHA-256 hash (hex) for storing high-entropy tokens so a DB leak does not expose
    /// the raw token. Deterministic on purpose: refresh tokens are looked up by hash.
    /// </summary>
    public static string Sha256(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    /// <summary>A numeric OTP of the given length (default 6 digits), zero-padded.</summary>
    public static string NewNumericOtp(int digits = 6)
    {
        var max = (int)Math.Pow(10, digits);
        var value = RandomNumberGenerator.GetInt32(0, max);
        return value.ToString(new string('0', digits));
    }
}
