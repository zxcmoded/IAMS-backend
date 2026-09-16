using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Security;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Common.Access;

/// <summary>
/// Bridges the pure <see cref="EffectiveAccessEvaluator"/> to live data: given the authenticated actor and
/// one or more (targetCompany, hierarchy, requiredPermission) requests, it loads the current connection
/// policy (the single connection per directed pair, with its scopes) and returns decisions. Always loads
/// live — never from a login/refresh snapshot — so a connection disabled or narrowed on the server takes
/// effect on the very next evaluation, including at sync time (BR-TC-007/008).
///
/// Shared by the single and batch evaluate slices, and available to any future slice that needs to gate a
/// cross-tenant operation server-side.
/// </summary>
public class AccessCheckService(IamsDbContext db, ICurrentUser currentUser)
{
    public record Request(
        Guid TargetCompanyId,
        Guid? LocationId,
        Guid? WarehouseId,
        Guid? RackId,
        Guid? BinId,
        PermissionLevel RequiredPermission);

    /// <summary>
    /// The set of companies the current actor can reach for master-data sync: their own home company plus
    /// every company reachable via an <b>enabled</b> <see cref="CompanyConnection"/> from it. This is the
    /// same "reachable universe" the master-data listing endpoints filter every level by (via the
    /// denormalized <c>CompanyId</c> on each node).
    ///
    /// Unlike <c>GetEffectiveScopeHandler</c> — which deliberately surfaces disabled connections too, for
    /// UI display — this intentionally excludes disabled connections: a disabled connection grants nothing
    /// (BR-TC-001/005), so its target's data must not be syncable.
    /// </summary>
    public async Task<IReadOnlySet<Guid>> GetReachableCompanyIdsAsync(CancellationToken ct)
    {
        var companyId = currentUser.CompanyId;
        var targets = await db.CompanyConnections.AsNoTracking()
            .Where(c => c.SourceCompanyId == companyId && c.IsEnabled)
            .Select(c => c.TargetCompanyId)
            .ToListAsync(ct);

        var reachable = new HashSet<Guid>(targets) { companyId };
        return reachable;
    }

    /// <summary>The actor's aggregate policy revision — the max across all connections from their active company.</summary>
    public async Task<long> GetActorPolicyVersionAsync(CancellationToken ct)
    {
        var companyId = currentUser.CompanyId;
        var revisions = db.CompanyConnections
            .Where(c => c.SourceCompanyId == companyId)
            .Select(c => c.PolicyRevision);
        return await revisions.AnyAsync(ct) ? await revisions.MaxAsync(ct) : 0;
    }

    public async Task<IReadOnlyList<AccessDecision>> EvaluateAsync(
        IReadOnlyList<Request> requests, CancellationToken ct)
    {
        var actor = new AccessActor(currentUser.TenantId, currentUser.CompanyId);
        var targetCompanyIds = requests.Select(r => r.TargetCompanyId).Distinct().ToList();

        // Resolve each target company's owning tenant server-side (do not trust a client-supplied tenant).
        var companyTenants = await db.Companies.AsNoTracking()
            .Where(c => targetCompanyIds.Contains(c.Id))
            .Select(c => new { c.Id, c.TenantId })
            .ToDictionaryAsync(c => c.Id, c => c.TenantId, ct);

        // Load the single connection per involved target company, with its scopes, live.
        var rows = await db.CompanyConnections.AsNoTracking()
            .Where(c => c.SourceCompanyId == actor.CompanyId && targetCompanyIds.Contains(c.TargetCompanyId))
            .Select(c => new
            {
                c.Id,
                c.TargetCompanyId,
                c.IsEnabled,
                c.PermissionLevel,
                c.PolicyRevision,
                Scopes = c.Scopes.Select(s => new
                {
                    s.Level,
                    s.ScopeCompanyId,
                    s.ScopeLocationId,
                    s.ScopeWarehouseId,
                    s.ScopeRackId,
                    s.ScopeBinId,
                    s.PermissionLevelOverride
                }).ToList()
            })
            .ToListAsync(ct);

        var connectionsByTarget = rows.ToDictionary(
            r => r.TargetCompanyId,
            r => new ConnectionEvaluation(
                r.Id,
                r.IsEnabled,
                r.PermissionLevel,
                r.PolicyRevision,
                r.Scopes
                    .Select(s => new ScopeGrant(
                        s.Level,
                        BoundNode(s.Level, s.ScopeCompanyId, s.ScopeLocationId, s.ScopeWarehouseId, s.ScopeRackId, s.ScopeBinId),
                        s.PermissionLevelOverride))
                    .Where(s => s.NodeId != Guid.Empty)
                    .ToList()));

        var decisions = new List<AccessDecision>(requests.Count);
        foreach (var request in requests)
        {
            // Unknown target company → fail closed (treated as no reachable connection).
            if (!companyTenants.TryGetValue(request.TargetCompanyId, out var targetTenantId))
            {
                decisions.Add(new AccessDecision(false, null, AccessReason.NoConnection, null, null));
                continue;
            }

            var resource = new ResourceDescriptor(
                targetTenantId,
                request.TargetCompanyId,
                request.LocationId,
                request.WarehouseId,
                request.RackId,
                request.BinId);

            connectionsByTarget.TryGetValue(request.TargetCompanyId, out var connection);
            decisions.Add(EffectiveAccessEvaluator.Evaluate(actor, resource, request.RequiredPermission, connection));
        }

        return decisions;
    }

    private static Guid BoundNode(
        HierarchyLevel level, Guid? company, Guid? location, Guid? warehouse, Guid? rack, Guid? bin) =>
        level switch
        {
            HierarchyLevel.Company => company ?? Guid.Empty,
            HierarchyLevel.Location => location ?? Guid.Empty,
            HierarchyLevel.Warehouse => warehouse ?? Guid.Empty,
            HierarchyLevel.Rack => rack ?? Guid.Empty,
            HierarchyLevel.Bin => bin ?? Guid.Empty,
            _ => Guid.Empty
        };
}
