namespace IAMS.Api.Common.Domain;

/// <summary>
/// A single-use OTP challenge created after a successful password check (F1). The raw code is never stored
/// — only a hash. Purpose/channel are captured so non-login OTP flows can be added without a schema change.
///
/// <see cref="ChallengeToken"/> is an opaque handle returned to the client and echoed back on verify/resend,
/// so those calls need not resend the username. (This column is an addition on top of the DB engineer's
/// reference OtpChallenge — flagged for reconciliation.)
/// </summary>
public class OtpChallenge
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>Opaque handle tying verify/resend back to this challenge.</summary>
    public string ChallengeToken { get; set; } = string.Empty;

    public string CodeHash { get; set; } = string.Empty;
    public OtpPurpose Purpose { get; set; }
    public TwoFactorChannel Channel { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? ConsumedAtUtc { get; set; }

    /// <summary>Earliest time a resend is permitted (throttling); set on create and each resend.</summary>
    public DateTime ResendAvailableAtUtc { get; set; }

    public int AttemptCount { get; set; }
    public int ResendCount { get; set; }

    public bool IsConsumed => ConsumedAtUtc is not null;
}

/// <summary>
/// An authenticated session, carrying the resolved active scope (F1): active company and active
/// location/store. The refresh token is stored only as a hash. Cross-tenant connection scope is resolved
/// dynamically per request against the connection tables, never frozen here, so policy changes are honored
/// without re-issuing the session (BR-TC-007).
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

    /// <summary>
    /// Snapshot of the user's <see cref="Domain.User.SecurityStamp"/> at issue time. Refresh rejects the
    /// session if it no longer matches the user's current stamp, so rotating the stamp (on password change,
    /// forced logout, etc.) invalidates every outstanding session for that user.
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

/// <summary>
/// Single-device binding for a user: exactly one row per user (enforced by a unique index on
/// <see cref="UserId"/>), recording the one device currently allowed to complete 2FA verify for that
/// account. Enforced in <c>VerifyTwoFactorHandler</c>, after the OTP is verified and before a session is
/// issued — identity is established but access is not yet granted at that point.
///
/// A reset (admin action, see <c>ResetUserDeviceBindingHandler</c>) flips <see cref="Status"/> to
/// <see cref="DeviceBindingStatus.Reset"/> rather than deleting the row, preserving an audit trail
/// (<see cref="ResetAtUtc"/> / <see cref="ResetByUserId"/>). The next successful verify re-registers
/// whichever device authenticates and flips the same row back to <see cref="DeviceBindingStatus.Active"/> —
/// it does not insert a second row, since the unique index on <see cref="UserId"/> allows only one.
///
/// This entity's PostgreSQL <c>xmin</c> system column is configured as an optimistic-concurrency
/// token (see <c>UserDeviceBindingConfiguration</c>, same pattern as <see cref="CompanyConnection"/>):
/// re-registering a Reset row is an UPDATE, so the unique index on <see cref="UserId"/> never fires to
/// catch two concurrent re-registrations racing the same row — <c>xmin</c> is what turns the loser's
/// write into a catchable <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>
/// instead of a silent double-issued session.
/// </summary>
public class UserDeviceBinding
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public string DeviceId { get; set; } = string.Empty;

    /// <summary>Optional client-supplied device metadata; not populated by the mobile client yet.</summary>
    public string? DeviceType { get; set; }
    public string? DeviceName { get; set; }

    public DateTime RegisteredAtUtc { get; set; }
    public DateTime LastAuthenticatedAtUtc { get; set; }

    public DeviceBindingStatus Status { get; set; }

    /// <summary>When an admin reset this binding; null while <see cref="Status"/> is Active.</summary>
    public DateTime? ResetAtUtc { get; set; }

    /// <summary>The admin (<see cref="Domain.User.IsSystemAdmin"/>) who performed the reset.</summary>
    public Guid? ResetByUserId { get; set; }
    public User? ResetByUser { get; set; }
}
