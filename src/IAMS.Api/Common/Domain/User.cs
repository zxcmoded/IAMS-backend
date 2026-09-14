namespace IAMS.Api.Common.Domain;

/// <summary>
/// Application user. Deliberately does NOT include lockout/offline-login mechanics — those policies are
/// "Not Specified" in F1. The table is trivially extensible (nullable columns added later) so nothing here
/// forecloses adding e.g. AccessFailedCount / LockoutEndUtc when the policy is defined.
/// </summary>
public class User
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;

    /// <summary>Upper-cased username for case-insensitive unique lookup at login.</summary>
    public string NormalizedUsername { get; set; } = string.Empty;

    public string? Email { get; set; }
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Rotated on credential/security changes to invalidate outstanding sessions.</summary>
    public string SecurityStamp { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// Platform-wide administrator flag, NOT scoped to any tenant/company (distinct from
    /// <see cref="UserCompanyMembership.RoleId"/>, which is per-company). Gates the "SystemAdmin"
    /// authorization policy. There is currently no admin-user-management feature to grant this — it is set
    /// directly in the database.
    /// </summary>
    public bool IsSystemAdmin { get; set; }

    public UserTwoFactorSetting? TwoFactorSetting { get; set; }
    public ICollection<UserCompanyMembership> Memberships { get; set; } = new List<UserCompanyMembership>();
    public ICollection<UserSession> Sessions { get; set; } = new List<UserSession>();
}

/// <summary>
/// A role a user may hold within a company. Minimal scaffold so the unresolved "role may further restrict
/// connection permissions" gap is not foreclosed (not enforced in Phase 1).
/// </summary>
public class Role
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
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
