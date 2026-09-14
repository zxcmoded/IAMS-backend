namespace Iams.Domain;

/// <summary>Whether a tenant (and its companies) sits on the parent or child side of the hierarchy.</summary>
public enum TenantKind
{
    Parent = 1,
    Child = 2,
}

/// <summary>
/// Direction of a company-to-company connection. Stored explicitly (not derived) because
/// direction is evaluated independently per BR-TC-002 (P→C does not imply C→P).
/// </summary>
public enum ConnectionType
{
    ParentToParent = 1, // P → P
    ParentToChild = 2,  // P → C
    ChildToParent = 3,  // C → P
    ChildToChild = 4,   // C → C
}

/// <summary>The hierarchy level at which a connection grants access (BR-TC-003).</summary>
public enum HierarchyLevel
{
    Company = 1,
    Location = 2,
    Warehouse = 3,
    Rack = 4,
    Bin = 5,
}

/// <summary>Permission granted by a connection / scope (BR-TC-004).</summary>
public enum PermissionLevel
{
    Read = 1,
    Write = 2,
    Full = 3,
}

/// <summary>Extensible connection filter dimensions (region / warehouse / category / location).</summary>
public enum ConnectionFilterType
{
    Region = 1,
    Location = 2,
    Warehouse = 3,
    Category = 4,
}

/// <summary>Purpose of an OTP challenge. Kept as an enum so non-login OTP flows can be added later.</summary>
public enum OtpPurpose
{
    Login = 1,
}

/// <summary>Delivery channel for a 2FA code.</summary>
public enum TwoFactorChannel
{
    Email = 1,
    Sms = 2,
    Authenticator = 3,
}
