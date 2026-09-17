namespace IAMS.Api.Common.Security;

/// <summary>Bound from the "Jwt" configuration section. Signing key must be supplied via secret in real envs.</summary>
public class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "iams";
    public string Audience { get; set; } = "iams-mobile";

    /// <summary>HMAC signing key. Must be at least 32 bytes. Never commit a real key.</summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>
    /// Access-token lifetime in days. Tokens are permanent-per-device now (no refresh flow), so this is a
    /// deliberately very-long fixed lifetime (~100 years) rather than a short expiry. An <c>exp</c> claim is
    /// still emitted because <c>TokenValidationParameters.ValidateLifetime</c> is enabled — omitting it would
    /// make every token fail validation.
    /// </summary>
    public int AccessTokenLifetimeDays { get; set; } = 36500;
}
