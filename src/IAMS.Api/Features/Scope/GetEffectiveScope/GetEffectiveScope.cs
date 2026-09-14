using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Errors;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Features.Scope.GetEffectiveScope;

// ── Contract ────────────────────────────────────────────────────────────────
public record EffectiveScopeResponse(
    UserRef User,
    TenantRef ActiveTenant,
    CompanyRef ActiveCompany,
    LocationRef? ActiveLocation,
    IReadOnlyList<ConnectionScope> Connections,
    long PolicyVersion);

public record UserRef(Guid Id, string Username, string DisplayName);
public record TenantRef(Guid Id, string Name, TenantKind Kind);
public record CompanyRef(Guid Id, string Name, Guid TenantId);
public record LocationRef(Guid Id, string Name);

/// <summary>One connection available from the user's active company, with its granted scopes.</summary>
public record ConnectionScope(
    Guid ConnectionId,
    CompanyRef TargetCompany,
    TenantRef TargetTenant,
    ConnectionType ConnectionType,
    bool IsEnabled,
    PermissionLevel PermissionLevel,
    IReadOnlyList<ConnectionScopeNode> Scopes,
    long PolicyVersion);

/// <summary>A single granted scope node. <see cref="PermissionLevel"/> is the effective permission (per-scope override, else the connection default).</summary>
public record ConnectionScopeNode(HierarchyLevel Level, Guid NodeId, PermissionLevel PermissionLevel);

// ── Handler ─────────────────────────────────────────────────────────────────
public class GetEffectiveScopeHandler(IamsDbContext db, ICurrentUser currentUser)
{
    public async Task<Results<Ok<EffectiveScopeResponse>, ProblemHttpResult>> HandleAsync(CancellationToken ct)
    {
        var userId = currentUser.UserId;
        var companyId = currentUser.CompanyId;
        var tenantId = currentUser.TenantId;
        var locationId = currentUser.LocationId;

        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Id, u.Username })
            .FirstOrDefaultAsync(ct);
        if (user is null)
        {
            return ApiError.Problem(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "User not found.");
        }

        var tenant = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new TenantRef(t.Id, t.Name, t.Kind))
            .FirstOrDefaultAsync(ct);

        var company = await db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId)
            .Select(c => new CompanyRef(c.Id, c.Name, c.TenantId))
            .FirstOrDefaultAsync(ct);

        if (tenant is null || company is null)
        {
            return ApiError.Problem(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Active scope not found.");
        }

        LocationRef? activeLocation = null;
        if (locationId is { } locId)
        {
            activeLocation = await db.Locations.AsNoTracking()
                .Where(l => l.Id == locId)
                .Select(l => new LocationRef(l.Id, l.Name))
                .FirstOrDefaultAsync(ct);
        }

        // Live query — never a login-time snapshot — so policy changes are reflected immediately (BR-TC-007).
        var rows = await db.CompanyConnections.AsNoTracking()
            .Where(c => c.SourceCompanyId == companyId)
            .Select(c => new
            {
                c.Id,
                c.ConnectionType,
                c.IsEnabled,
                c.PermissionLevel,
                c.PolicyRevision,
                Target = new CompanyRef(c.TargetCompany.Id, c.TargetCompany.Name, c.TargetCompany.TenantId),
                TargetTenant = new TenantRef(c.TargetCompany.Tenant.Id, c.TargetCompany.Tenant.Name, c.TargetCompany.Tenant.Kind),
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

        var connections = rows.Select(r => new ConnectionScope(
            r.Id,
            r.Target,
            r.TargetTenant,
            r.ConnectionType,
            r.IsEnabled,
            r.PermissionLevel,
            r.Scopes.Select(s => new ConnectionScopeNode(
                s.Level,
                BoundNode(s.Level, s.ScopeCompanyId, s.ScopeLocationId, s.ScopeWarehouseId, s.ScopeRackId, s.ScopeBinId),
                s.PermissionLevelOverride ?? r.PermissionLevel)).ToList(),
            r.PolicyRevision)).ToList();

        var policyVersion = connections.Count == 0 ? 0 : connections.Max(c => c.PolicyVersion);

        var response = new EffectiveScopeResponse(
            new UserRef(user.Id, user.Username, user.Username),
            tenant,
            company,
            activeLocation,
            connections,
            policyVersion);

        return TypedResults.Ok(response);
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
