namespace IAMS.Api.Common.Security;

/// <summary>Bound from the "Jwt" configuration section. Signing key must be supplied via secret in real envs.</summary>
public class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "iams";
    public string Audience { get; set; } = "iams-mobile";

    /// <summary>HMAC signing key. Must be at least 32 bytes. Never commit a real key.</summary>
    public string SigningKey { get; set; } = string.Empty;

    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;
}
