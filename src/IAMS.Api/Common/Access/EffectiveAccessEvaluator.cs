using IAMS.Api.Common.Domain;

namespace IAMS.Api.Common.Access;

/// <summary>
/// Pure implementation of the F15 "Effective access evaluation" flowchart. Takes an actor, a target
/// resource, the required permission, and the single connection configured from the actor's company to the
/// resource's company (with its scopes), and returns a decision. No I/O — callers load the connection live
/// (so BR-TC-007/008 policy changes take effect immediately) and pass it in.
///
/// There is at most one connection per directed company pair (unique index), so the source doc's
/// "permission precedence across multiple connections" gap is moot. Where a connection has multiple
/// applicable scopes, the effective permission is the MOST permissive across them (each scope may carry a
/// per-scope permission override, else it inherits the connection's base permission). Hierarchy scope is
/// inherited downward via the resource's denormalized ancestry; a resource that does not resolve to a
/// scope's level fails closed.
/// </summary>
public static class EffectiveAccessEvaluator
{
    public static AccessDecision Evaluate(
        AccessActor actor,
        ResourceDescriptor resource,
        PermissionLevel requiredPermission,
        ConnectionEvaluation? connection)
    {
        // Same tenant → normal tenant authorization (out of scope for connection policy).
        if (resource.TenantId == actor.TenantId)
        {
            return new AccessDecision(true, PermissionLevel.Full, AccessReason.SameTenant, null, null);
        }

        // Cross-tenant: an explicit connection is required (BR-TC-001/005).
        if (connection is null)
        {
            return new AccessDecision(false, null, AccessReason.NoConnection, null, null);
        }

        if (!connection.IsEnabled)
        {
            return new AccessDecision(false, null, AccessReason.ConnectionDisabled, connection.ConnectionId, connection.PolicyRevision);
        }

        // A scope must cover the requested resource (BR-TC-003).
        var covering = connection.Scopes.Where(s => ScopeCovers(s, resource)).ToList();
        if (covering.Count == 0)
        {
            return new AccessDecision(false, null, AccessReason.OutOfScope, connection.ConnectionId, connection.PolicyRevision);
        }

        // Most-permissive scope wins; a scope inherits the connection's base permission unless it overrides.
        var effective = covering.Max(s => s.PermissionOverride ?? connection.BasePermission);

        var reason = effective >= requiredPermission ? AccessReason.ConnectionGranted : AccessReason.InsufficientPermission;
        return new AccessDecision(
            effective >= requiredPermission,
            effective,
            reason,
            connection.ConnectionId,
            connection.PolicyRevision);
    }

    /// <summary>
    /// True when the scope covers the requested resource: the resource's ancestor id at the scope's level
    /// equals the scoped node. Fail closed if the resource does not resolve to that level.
    /// </summary>
    private static bool ScopeCovers(ScopeGrant scope, ResourceDescriptor resource)
    {
        return scope.Level switch
        {
            HierarchyLevel.Company => resource.CompanyId == scope.NodeId,
            HierarchyLevel.Location => resource.LocationId is { } id && id == scope.NodeId,
            HierarchyLevel.Warehouse => resource.WarehouseId is { } id && id == scope.NodeId,
            HierarchyLevel.Rack => resource.RackId is { } id && id == scope.NodeId,
            HierarchyLevel.Bin => resource.BinId is { } id && id == scope.NodeId,
            _ => false
        };
    }
}
