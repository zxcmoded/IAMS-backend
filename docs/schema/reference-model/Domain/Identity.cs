namespace Iams.Domain;

/// <summary>
/// Application user. Deliberately does NOT include lockout/offline-login mechanics — those policies are
/// "Not Specified" in F1. The table is trivially extensible (nullable columns can be added later) so nothing
/// here forecloses adding e.g. AccessFailedCount / LockoutEndUtc when the policy is defined.
/// </summary>
public class User
{
    public Guid Id { get; set; }
    public required string Username { get; set; }
    /// <summary>Upper-cased username for case-insensitive unique lookup at login.</summary>
    public required string NormalizedUsername { get; set; }
    public string? Email { get; set; }
    public required string PasswordHash { get; set; }
    /// <summary>Rotated on credential/security changes to invalidate outstanding sessions.</summary>
    public required string SecurityStamp { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }

    public UserTwoFactorSetting? TwoFactorSetting { get; set; }
    public ICollection<UserCompanyMembership> Memberships { get; set; } = new List<UserCompanyMembership>();
    public ICollection<UserSession> Sessions { get; set; } = new List<UserSession>();
}

/// <summary>A role a user may hold within a company. Minimal scaffold so the unresolved
/// "role may further restrict connection permissions" gap is not foreclosed.</summary>
public class Role
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
}

/// <summary>User's membership in one of their home-tenant companies, plus optional role.</summary>
public class UserCompanyMembership
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public Guid CompanyId { get; set; }
    public Company Company { get; set; } = null!;

    public Guid? RoleId { get; set; }
    public Role? Role { get; set; }

    /// <summary>The user's default company when a session does not specify one.</summary>
    public bool IsPrimary { get; set; }
}

/// <summary>Per-user 2FA configuration.</summary>
public class UserTwoFactorSetting
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public bool IsEnabled { get; set; }
    public TwoFactorChannel Channel { get; set; }
    /// <summary>Shared secret for authenticator (TOTP); null for OTP-over-email/SMS.</summary>
    public string? SharedSecret { get; set; }
}

/// <summary>
/// A single-use OTP challenge. The raw code is never stored — only a hash. Purpose/channel are captured so
/// non-login OTP flows can be added later without a schema change.
/// </summary>
public class OtpChallenge
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>
    /// Opaque, unguessable token returned to the client on login and echoed back on 2FA verify/resend.
    /// The primary lookup key for the verify/resend flow — the client never re-sends username, and the
    /// raw OTP code is only sent on verify (compared against <see cref="CodeHash"/>). Globally unique.
    /// </summary>
    public required string ChallengeToken { get; set; }

    public required string CodeHash { get; set; }
    public OtpPurpose Purpose { get; set; }
    public TwoFactorChannel Channel { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? ConsumedAtUtc { get; set; }
    public int AttemptCount { get; set; }
}

/// <summary>
/// An authenticated session, carrying the resolved active scope (F1): active company and active
/// location/store. Cross-tenant connection scope is resolved dynamically per request against the
/// connection tables, not frozen here, so policy changes are honored without re-issuing the session.
/// </summary>
public class UserSession
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>Active company scope resolved at login (BR-002).</summary>
    public Guid ActiveCompanyId { get; set; }
    public Company ActiveCompany { get; set; } = null!;

    /// <summary>Active location/store scope; null = company-wide.</summary>
    public Guid? ActiveLocationId { get; set; }
    public Location? ActiveLocation { get; set; }

    /// <summary>Whether the 2FA step has been satisfied for this session.</summary>
    public bool IsTwoFactorComplete { get; set; }

    public string? DeviceId { get; set; }
    /// <summary>Hash of the refresh token; raw token never persisted.</summary>
    public string? RefreshTokenHash { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
}
