namespace IAMS.Api.Common.Domain;

/// <summary>
/// The fixed, closed set of application roles. Modeled as a plain C# enum with explicit <b>int codes</b>
/// (not a lookup table) — the codes are deliberately ordered and gapped so a higher value always denotes a
/// superset of privileges, letting access checks compare with <c>&gt;=</c> (e.g. "Manager or above" is
/// <c>role &gt;= UserRole.Manager</c>). Each user holds exactly one role (see <see cref="User.Role"/>).
///
/// The int codes are the authoritative wire value (persisted as an int column, carried as the JWT
/// <c>role</c> claim, and echoed in API responses as <c>{ code, name }</c>) so a client can reason about
/// privilege ordering numerically without hardcoding name strings.
/// </summary>
public enum UserRole
{
    /// <summary>Read-only within assigned Locations.</summary>
    Viewer = 100,

    /// <summary>"Scanner" — performs inventory/scanning operations within assigned Locations.</summary>
    User = 200,

    /// <summary>Manages assigned Locations and their inventory.</summary>
    Manager = 300,

    /// <summary>Manages company-level data and users; scoped to their own Company (all its Locations).</summary>
    Admin = 700,

    /// <summary>Full system access — not restricted by Company/Location scoping.</summary>
    SuperAdmin = 800
}

/// <summary>
/// The hierarchy level a scope/resource resolves to. Retained from the original model (the physical
/// hierarchy — Company → Location → Warehouse → Rack → Bin — is unchanged apart from dropping the former
/// Tenant level on top) so scope-level checks have a single named vocabulary.
/// </summary>
public enum HierarchyLevel
{
    Company = 1,
    Location = 2,
    Warehouse = 3,
    Rack = 4,
    Bin = 5
}

/// <summary>
/// State of a user's Activation Key device binding. <see cref="NotActivated"/> covers BOTH "never
/// activated" and "admin-reset" — both mean the key is free to bind to the next device that presents it;
/// the reset audit fields on <see cref="User"/> are what distinguish a fresh key from a reset one, not this
/// enum.
/// </summary>
public enum ActivationStatus
{
    NotActivated = 1,
    Activated = 2
}
