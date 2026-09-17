namespace IAMS.Api.Common.Domain;

/// <summary>
/// An authenticated session, carrying the resolved active scope: active company and active
/// location/store. Retained for audit/session bookkeeping (activation reset and logout mark it revoked);
/// there is no refresh token — access tokens are permanent-per-device and authenticate on their own.
/// Cross-tenant connection scope is resolved dynamically per request against the connection tables, never
/// frozen here, so policy changes are honored without re-issuing the session (BR-TC-007).
/// </summary>
public class UserSession
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>Active company scope resolved at activation (BR-002).</summary>
    public Guid ActiveCompanyId { get; set; }
    public Company ActiveCompany { get; set; } = null!;

    /// <summary>Active location/store scope; null = company-wide.</summary>
    public Guid? ActiveLocationId { get; set; }
    public Location? ActiveLocation { get; set; }

    /// <summary>
    /// Snapshot of the user's <see cref="Domain.User.SecurityStamp"/> at issue time, kept as session/audit
    /// metadata. No longer enforced: the check that rejected a session when this stamp no longer matched
    /// the user's current one lived in the refresh flow, which has been removed — rotating the stamp
    /// (forced logout, activation reset, etc.) no longer invalidates outstanding sessions.
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
