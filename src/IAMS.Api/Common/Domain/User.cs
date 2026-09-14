namespace IAMS.Api.Common.Domain;

/// <summary>
/// Application user, authenticated by a single Activation Key credential (no username/password, no 2FA).
/// <see cref="Username"/>/<see cref="Email"/> are now purely display/contact fields — neither is used to
/// look the user up at authentication time; the Activation Key is the only credential.
///
/// Activation/device-binding fields live directly on this table (per spec) rather than a side table, and
/// this entity's PostgreSQL <c>xmin</c> system column is configured as an optimistic-concurrency token
/// (see <c>UserConfiguration</c> and <c>IamsDbContext.OnModelCreating</c> — same pattern previously proven
/// on the old per-user device-binding table): activating a never-activated key and re-registering a
/// key after an admin reset are BOTH plain UPDATEs to this same row (there is nothing to INSERT — the key
/// already exists as a column here), so the only race shape that matters is two concurrent UPDATEs to the
/// same row, which <c>xmin</c> turns into a catchable <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>
/// for the loser instead of a silently double-bound key. See <c>ActivateHandler</c>.
///
/// A reset (admin action, see <c>ResetUserActivationHandler</c>) flips <see cref="ActivationStatus"/> back
/// to <see cref="Domain.ActivationStatus.NotActivated"/> without clearing <see cref="ActivatedDeviceId"/>/
/// <see cref="ActivatedAtUtc"/> immediately — those are left as an audit trail (alongside
/// <see cref="ActivationResetAtUtc"/>/<see cref="ActivationResetByUserId"/>) until the next successful
/// activation overwrites them.
///
/// Deliberately does NOT include lockout mechanics — that policy is "Not Specified" upstream. The table is
/// trivially extensible (nullable columns added later) so nothing here forecloses adding e.g.
/// AccessFailedCount / LockoutEndUtc when the policy is defined.
/// </summary>
public class User
{
    public Guid Id { get; set; }

    /// <summary>Display-only identity; not used for authentication.</summary>
    public string Username { get; set; } = string.Empty;

    public string? Email { get; set; }

    /// <summary>
    /// SHA-256 hash of the Activation Key — the ONLY credential. Looked up by exact hash match (same
    /// deterministic-hash-for-lookup pattern as <see cref="UserSession.RefreshTokenHash"/> /
    /// <see cref="Security.TokenGenerator.Sha256"/>), not PBKDF2: the key itself is the row-selector (there
    /// is no separate "username" to find the user by first), and a high-entropy, system/admin-provisioned
    /// token does not need PBKDF2's slow, salted, "resist offline guessing of a low-entropy human secret"
    /// property the way a user-chosen password would. The raw key is never persisted or logged.
    /// </summary>
    public string ActivationKeyHash { get; set; } = string.Empty;

    public ActivationStatus ActivationStatus { get; set; } = ActivationStatus.NotActivated;

    /// <summary>The device currently bound to this user's Activation Key; null until first activation.</summary>
    public string? ActivatedDeviceId { get; set; }

    public DateTime? ActivatedAtUtc { get; set; }

    /// <summary>When an admin last reset this user's activation; null while currently Activated.</summary>
    public DateTime? ActivationResetAtUtc { get; set; }

    /// <summary>The admin (<see cref="IsSystemAdmin"/>) who performed the reset.</summary>
    public Guid? ActivationResetByUserId { get; set; }
    public User? ActivationResetByUser { get; set; }

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
