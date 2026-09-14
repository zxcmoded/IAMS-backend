namespace IAMS.Api.Common.Domain;

/// <summary>Whether a tenant (and its companies) sits on the parent or child side of the hierarchy (F15).</summary>
public enum TenantKind
{
    Parent = 1,
    Child = 2
}

/// <summary>
/// Direction of a company-to-company connection. Stored explicitly (not derived) because direction is
/// evaluated independently per BR-TC-002 (P→C does not imply C→P).
/// </summary>
public enum ConnectionType
{
    ParentToParent = 1,
    ParentToChild = 2,
    ChildToParent = 3,
    ChildToChild = 4
}

/// <summary>The hierarchy level at which a connection scope grants access (BR-TC-003).</summary>
public enum HierarchyLevel
{
    Company = 1,
    Location = 2,
    Warehouse = 3,
    Rack = 4,
    Bin = 5
}

/// <summary>
/// Permission granted by a connection / scope (BR-TC-004). Ordered so a higher value satisfies every
/// requirement a lower one does (Read &lt; Write &lt; Full). "No permission" is represented out-of-band
/// (a nullable in access decisions), never stored, so the DB CHECK domain stays exactly these three.
/// </summary>
public enum PermissionLevel
{
    Read = 1,
    Write = 2,
    Full = 3
}

/// <summary>Extensible connection filter dimensions (region / warehouse / category / location).</summary>
public enum ConnectionFilterType
{
    Region = 1,
    Location = 2,
    Warehouse = 3,
    Category = 4
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
