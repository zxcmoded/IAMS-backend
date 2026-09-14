using IAMS.Api.Common.Domain;

namespace IAMS.Api.Features.Auth;

/// <summary>
/// Minimal user identity returned alongside a freshly issued session. <see cref="DisplayName"/> currently
/// mirrors the username — the reference User has no display-name column yet — so the contract field stays
/// stable for mobile; a real display name can populate it later without a shape change.
/// </summary>
public record UserSummary(Guid Id, string Username, string DisplayName)
{
    public static UserSummary From(User user) => new(user.Id, user.Username, user.Username);
}

/// <summary>
/// The token pair + user returned by activation and by token refresh. The full effective scope
/// (active tenant/company/location + connections + permissions) is fetched separately from
/// GET /api/me/scope so this response stays small and both auth slices share one shape.
/// </summary>
public record AuthTokenResponse(
    string AccessToken,
    string TokenType,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    UserSummary User);
