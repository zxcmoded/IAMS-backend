using IAMS.Api.Common.Access;
using IAMS.Api.Common.Domain;

namespace IAMS.Api.Features.Auth;

/// <summary>
/// Minimal user identity returned alongside a freshly issued session. <see cref="DisplayName"/> currently
/// mirrors the username — the User has no display-name column yet — so the contract field stays stable for
/// mobile; a real display name can populate it later without a shape change. <see cref="Role"/> carries the
/// user's single role as <c>{ code, name }</c>, and <see cref="CompanyId"/> the single Company they belong to.
/// </summary>
public record UserSummary(Guid Id, string Username, string DisplayName, Guid CompanyId, RoleDto Role)
{
    public static UserSummary From(User user) =>
        new(user.Id, user.Username, user.Username, user.CompanyId, RoleDto.From(user.Role));
}

/// <summary>
/// The access token + user returned by activation. Tokens are permanent-per-device (there is no refresh
/// flow), so <see cref="AccessTokenExpiresAt"/> is a far-future timestamp — clients may surface it but do
/// not need to act on it. The full effective scope (Company + assigned Locations + role) is fetched
/// separately from GET /api/me/scope so this response stays small.
/// </summary>
public record AuthTokenResponse(
    string AccessToken,
    string TokenType,
    DateTimeOffset AccessTokenExpiresAt,
    UserSummary User);
