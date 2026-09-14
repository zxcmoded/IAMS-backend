using System.Security.Cryptography;

namespace IAMS.Api.Common.Security;

/// <summary>
/// PBKDF2 (SHA-256) password/secret hasher. No external dependency. Used for user passwords and for
/// hashing short-lived OTP codes (salted, so codes cannot be looked up — they are verified against a
/// specific challenge).
/// </summary>
public static class PasswordHasher
{
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 100_000;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    /// <summary>
    /// A fixed, valid hash of a throwaway value. Verifying a supplied password against this when no user
    /// exists makes the unknown-username path perform the same 100k-iteration PBKDF2 work as the
    /// known-username path, removing the timing oracle that would otherwise leak valid usernames.
    /// </summary>
    private static readonly string DummyHash = Hash("iams-timing-equalizer-not-a-real-password");

    /// <summary>Runs a full verify against a dummy hash purely to equalize timing; the result is discarded.</summary>
    public static void VerifyDummy(string value) => _ = Verify(value, DummyHash);

    public static string Hash(string value)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(value, salt, Iterations, Algorithm, KeySize);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    public static bool Verify(string value, string encoded)
    {
        var parts = encoded.Split('.', 3);
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations))
        {
            return false;
        }

        var salt = Convert.FromBase64String(parts[1]);
        var expectedKey = Convert.FromBase64String(parts[2]);
        var actualKey = Rfc2898DeriveBytes.Pbkdf2(value, salt, iterations, Algorithm, expectedKey.Length);
        return CryptographicOperations.FixedTimeEquals(actualKey, expectedKey);
    }
}
