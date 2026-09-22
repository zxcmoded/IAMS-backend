using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Security;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Common.Access;

/// <summary>
/// The authenticated actor's effective data-access scope, resolved from their role + single Company +
/// assigned Locations. This replaces the removed cross-tenant connection evaluation: access is now simply
/// "your own Company, narrowed to your assigned Locations for the location-restricted roles".
///
/// Three tiers, by role:
/// <list type="bullet">
/// <item><see cref="UserRole.SuperAdmin"/> ⇒ <see cref="SystemWide"/> — no Company or Location filter at all.</item>
/// <item><see cref="UserRole.Admin"/> ⇒ Company-scoped, all Locations (<see cref="LocationRestricted"/> = false).</item>
/// <item>Manager / User / Viewer ⇒ Company-scoped AND restricted to <see cref="LocationIds"/>.</item>
/// </list>
///
/// Handlers apply this with the small <c>Where…</c> helpers below rather than re-deriving the rules, so the
/// scoping is uniform across every slice. Company-only master data (the SKU catalog, the ledger, scan audit)
/// is scoped by <see cref="CompanyId"/>; location-bound data (locations, warehouses, racks, bins, stock,
/// counts) is additionally narrowed to <see cref="LocationIds"/> when <see cref="LocationRestricted"/>.
/// </summary>
public sealed record UserAccessScope(
    Guid UserId,
    Guid CompanyId,
    UserRole Role,
    bool SystemWide,
    bool LocationRestricted,
    IReadOnlyCollection<Guid> LocationIds)
{
    /// <summary>True when the actor may see every Location in their Company (Admin) or the whole system (SuperAdmin).</summary>
    public bool AllCompanyLocations => !LocationRestricted;

    /// <summary>Whether a given Company is inside the actor's scope (their own Company, or anything for SuperAdmin).</summary>
    public bool CompanyInScope(Guid companyId) => SystemWide || companyId == CompanyId;

    /// <summary>
    /// Whether a location-bound resource (identified by its Company + Location ancestry) is inside the actor's
    /// scope: the Company must match, and — for the location-restricted roles — the Location must be assigned.
    /// </summary>
    public bool LocationInScope(Guid companyId, Guid locationId) =>
        CompanyInScope(companyId) && (!LocationRestricted || LocationIds.Contains(locationId));
}

/// <summary>
/// Resolves the current request's <see cref="UserAccessScope"/> from the JWT (role + company, no DB hit) plus
/// — only for the location-restricted roles — a single lookup of their assigned Location ids.
/// </summary>
public class AccessScopeResolver(IamsDbContext db, ICurrentUser currentUser)
{
    public async Task<UserAccessScope> ResolveAsync(CancellationToken ct)
    {
        var userId = currentUser.UserId;
        var companyId = currentUser.CompanyId;
        var role = currentUser.Role;

        if (role == UserRole.SuperAdmin)
        {
            return new UserAccessScope(userId, companyId, role, SystemWide: true, LocationRestricted: false, Array.Empty<Guid>());
        }

        if (role == UserRole.Admin)
        {
            return new UserAccessScope(userId, companyId, role, SystemWide: false, LocationRestricted: false, Array.Empty<Guid>());
        }

        var locationIds = await db.UserLocationAssignments.AsNoTracking()
            .Where(a => a.UserId == userId)
            .Select(a => a.LocationId)
            .ToArrayAsync(ct);

        return new UserAccessScope(userId, companyId, role, SystemWide: false, LocationRestricted: true, locationIds);
    }
}
