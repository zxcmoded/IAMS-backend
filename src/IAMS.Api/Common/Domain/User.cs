namespace IAMS.Api.Common.Domain;

/// <summary>
/// Application user, authenticated by a single Activation Key credential (no username/password, no 2FA).
/// <see cref="Username"/>/<see cref="Email"/> are now purely display/contact fields — neither is used to
/// look the user up at authentication time; the Activation Key is the only credential.
///
/// A user belongs to exactly <b>one</b> <see cref="Company"/> (non-nullable <see cref="CompanyId"/> FK) and
/// holds exactly one <see cref="Domain.UserRole"/>. Data access is scoped to that Company and — for the
/// location-restricted roles (Manager / User / Viewer) — to the specific Locations assigned via
/// <see cref="AssignedLocations"/>. <see cref="UserRole.Admin"/> sees all Locations in its Company;
/// <see cref="UserRole.SuperAdmin"/> is not restricted by Company/Location at all.
///
/// Activation/device-binding fields live directly on this table (per spec) rather than a side table, and
/// this entity's PostgreSQL <c>xmin</c> system column is configured as an optimistic-concurrency token
/// (see <c>UserConfiguration</c> and <c>IamsDbContext.OnModelCreating</c>): activating a never-activated key
/// and re-registering a key after an admin reset are BOTH plain UPDATEs to this same row, so the only race
/// shape that matters is two concurrent UPDATEs to the same row, which <c>xmin</c> turns into a catchable
/// <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/> for the loser instead of a
/// silently double-bound key. See <c>ActivateHandler</c>.
///
/// A reset (admin action, see <c>ResetUserActivationHandler</c>) flips <see cref="ActivationStatus"/> back
/// to <see cref="Domain.ActivationStatus.NotActivated"/> without clearing <see cref="ActivatedDeviceId"/>/
/// <see cref="ActivatedAtUtc"/> immediately — those are left as an audit trail (alongside
/// <see cref="ActivationResetAtUtc"/>/<see cref="ActivationResetByUserId"/>) until the next successful
/// activation overwrites them.
/// </summary>
public class User
{
    public Guid Id { get; set; }

    /// <summary>Display-only identity; not used for authentication.</summary>
    public string Username { get; set; } = string.Empty;

    public string? Email { get; set; }

    /// <summary>The single Company this user belongs to. Non-nullable — every user has exactly one Company.</summary>
    public Guid CompanyId { get; set; }
    public Company Company { get; set; } = null!;

    /// <summary>The user's single role. Determines both privilege level and how data access is scoped.</summary>
    public UserRole Role { get; set; } = UserRole.Viewer;

    /// <summary>
    /// SHA-256 hash of the Activation Key — the ONLY credential. Looked up by exact hash match, not PBKDF2:
    /// the key itself is the row-selector (there is no separate "username" to find the user by first), and a
    /// high-entropy, system/admin-provisioned token does not need PBKDF2's slow, salted resistance the way a
    /// user-chosen password would. The raw key is never persisted or logged.
    /// </summary>
    public string ActivationKeyHash { get; set; } = string.Empty;

    public ActivationStatus ActivationStatus { get; set; } = ActivationStatus.NotActivated;

    /// <summary>The device currently bound to this user's Activation Key; null until first activation.</summary>
    public string? ActivatedDeviceId { get; set; }

    public DateTime? ActivatedAtUtc { get; set; }

    /// <summary>When an admin last reset this user's activation; null while currently Activated.</summary>
    public DateTime? ActivationResetAtUtc { get; set; }

    /// <summary>The admin who performed the reset.</summary>
    public Guid? ActivationResetByUserId { get; set; }
    public User? ActivationResetByUser { get; set; }

    /// <summary>Rotated on credential/security changes to invalidate outstanding sessions.</summary>
    public string SecurityStamp { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>The Locations this user is assigned to (many-to-many). Relevant for the location-restricted roles.</summary>
    public ICollection<UserLocationAssignment> AssignedLocations { get; set; } = new List<UserLocationAssignment>();

    public ICollection<UserSession> Sessions { get; set; } = new List<UserSession>();
}

/// <summary>
/// Join row assigning a <see cref="User"/> to a <see cref="Location"/> within that user's Company. A user
/// may be assigned to multiple Locations; access to location-bound data (warehouses, racks, bins, stock,
/// counts, physical scans) is evaluated against exactly this assigned set for the location-restricted roles
/// — no bleed into unassigned Locations of the same Company. Unique per (UserId, LocationId).
/// </summary>
public class UserLocationAssignment
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public Guid LocationId { get; set; }
    public Location Location { get; set; } = null!;

    public DateTime CreatedAtUtc { get; set; }
}
