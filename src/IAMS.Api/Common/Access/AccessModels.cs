using IAMS.Api.Common.Domain;

namespace IAMS.Api.Common.Access;

/// <summary>The reason an access decision was reached — mirrors the F15 flowchart branches.</summary>
public enum AccessReason
{
    /// <summary>Resource is in the actor's own tenant; normal tenant authorization applies.</summary>
    SameTenant,
    /// <summary>An enabled, in-scope connection granted sufficient permission.</summary>
    ConnectionGranted,
    /// <summary>No connection is configured from the actor's company to the target company.</summary>
    NoConnection,
    /// <summary>A connection exists but is disabled.</summary>
    ConnectionDisabled,
    /// <summary>The connection is enabled but none of its scopes cover the requested resource's hierarchy node.</summary>
    OutOfScope,
    /// <summary>An enabled, in-scope connection exists but its permission level is below what was requested.</summary>
    InsufficientPermission
}

/// <summary>The actor side of an evaluation: who is asking, resolved from the authenticated session.</summary>
public readonly record struct AccessActor(Guid TenantId, Guid CompanyId);

/// <summary>
/// The resource being accessed, described by its tenant/company plus its position in the
/// Company → Location → Warehouse → Rack → Bin hierarchy. Deeper ids are null when the resource does not
/// resolve that deep. (These are the resource's denormalized ancestry ids.)
/// </summary>
public readonly record struct ResourceDescriptor(
    Guid TenantId,
    Guid CompanyId,
    Guid? LocationId,
    Guid? WarehouseId,
    Guid? RackId,
    Guid? BinId);

/// <summary>One granted scope of a connection, projected to what the evaluator needs.</summary>
public readonly record struct ScopeGrant(HierarchyLevel Level, Guid NodeId, PermissionLevel? PermissionOverride);

/// <summary>
/// The single connection from the actor's company to the target company (there is at most one, enforced by
/// a unique index), projected with its scopes. Null when no such connection exists.
/// </summary>
public sealed record ConnectionEvaluation(
    Guid ConnectionId,
    bool IsEnabled,
    PermissionLevel BasePermission,
    long PolicyRevision,
    IReadOnlyList<ScopeGrant> Scopes);

/// <summary>The outcome of evaluating one (actor, resource, requiredPermission) tuple.</summary>
public readonly record struct AccessDecision(
    bool Allowed,
    PermissionLevel? EffectivePermission,
    AccessReason Reason,
    Guid? ConnectionId,
    long? PolicyVersion);
