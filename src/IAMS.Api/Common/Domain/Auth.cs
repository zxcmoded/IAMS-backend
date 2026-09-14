namespace IAMS.Api.Common.Domain;

/// <summary>
/// An authenticated session, carrying the resolved active scope: active company and active
/// location/store. The refresh token is stored only as a hash. Cross-tenant connection scope is resolved
/// dynamically per request against the connection tables, never frozen here, so policy changes are honored
/// without re-issuing the session (BR-TC-007).
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
    /// Snapshot of the user's <see cref="Domain.User.SecurityStamp"/> at issue time. Refresh rejects the
    /// session if it no longer matches the user's current stamp, so rotating the stamp (forced logout,
    /// activation reset, etc.) invalidates every outstanding session for that user.
    /// </summary>
    public string SecurityStamp { get; set; } = string.Empty;

    public string? DeviceId { get; set; }

    /// <summary>SHA-256 hash of the refresh token; the raw value is only ever returned once, at issue time.</summary>
    public string? RefreshTokenHash { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }

    public bool IsActive(DateTime nowUtc) => RevokedAtUtc is null && ExpiresAtUtc > nowUtc;
}
