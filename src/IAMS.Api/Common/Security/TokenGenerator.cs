using System.Security.Cryptography;

namespace IAMS.Api.Common.Security;

/// <summary>Deterministically hashes high-entropy tokens for storage.</summary>
public static class TokenGenerator
{
    /// <summary>
    /// Deterministic SHA-256 hash (hex) for storing high-entropy tokens so a DB leak does not expose
    /// the raw token. Deterministic on purpose: Activation Keys are looked up by hash
    /// (see <see cref="IAMS.Api.Common.Domain.User.ActivationKeyHash"/>) — never stored or logged in the clear.
    /// </summary>
    public static string Sha256(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
