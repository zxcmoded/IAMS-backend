namespace IAMS.Api.Common.Domain;

/// <summary>
/// An authenticated session, retained for audit/session bookkeeping (activation reset and logout mark it
/// revoked). There is no refresh token — access tokens are permanent-per-device and authenticate on their
/// own. The session no longer carries an "active company/location" scope: a user has exactly one Company
/// (<see cref="User.CompanyId"/>) and a fixed set of assigned Locations, so there is nothing to switch
/// between and nothing to freeze here — access is evaluated live per request against the user's Company +
/// assigned Locations.
/// </summary>
public class UserSession
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>
    /// Snapshot of the user's <see cref="Domain.User.SecurityStamp"/> at issue time, kept as session/audit
    /// metadata. Not enforced on the stateless permanent access token.
    /// </summary>
    public string SecurityStamp { get; set; } = string.Empty;

    public string? DeviceId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// When this session's access token expires. With permanent-per-device tokens this is a far-future
    /// timestamp mirroring the issued token's <c>exp</c>; kept as session/audit metadata.
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
}
