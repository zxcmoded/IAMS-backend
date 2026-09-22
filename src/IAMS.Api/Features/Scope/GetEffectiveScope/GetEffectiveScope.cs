using IAMS.Api.Common.Access;
using IAMS.Api.Common.Errors;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Features.Scope.GetEffectiveScope;

// ── Contract ────────────────────────────────────────────────────────────────
/// <summary>
/// The caller's effective access scope. <see cref="AssignedLocations"/> is the set of Locations the user
/// can act within: for the location-restricted roles (Manager / User / Viewer) it is exactly their assigned
/// set; for Admin and SuperAdmin it is every Location in their Company (<see cref="UnrestrictedCompanyAccess"/>
/// = true). <see cref="SystemWideAccess"/> is true only for SuperAdmin, who is additionally not restricted to
/// a single Company.
/// </summary>
public record EffectiveScopeResponse(
    UserRef User,
    RoleDto Role,
    CompanyRef Company,
    IReadOnlyList<LocationRef> AssignedLocations,
    bool UnrestrictedCompanyAccess,
    bool SystemWideAccess);

public record UserRef(Guid Id, string Username, string DisplayName);
public record CompanyRef(Guid Id, string Name);
public record LocationRef(Guid Id, string Name);

// ── Handler ─────────────────────────────────────────────────────────────────
public class GetEffectiveScopeHandler(IamsDbContext db, ICurrentUser currentUser, AccessScopeResolver scopeResolver)
{
    public async Task<Results<Ok<EffectiveScopeResponse>, ProblemHttpResult>> HandleAsync(CancellationToken ct)
    {
        var userId = currentUser.UserId;

        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Id, u.Username, u.CompanyId, u.Role })
            .FirstOrDefaultAsync(ct);
        if (user is null)
        {
            return ApiError.Problem(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "User not found.");
        }

        var company = await db.Companies.AsNoTracking()
            .Where(c => c.Id == user.CompanyId)
            .Select(c => new CompanyRef(c.Id, c.Name))
            .FirstOrDefaultAsync(ct);
        if (company is null)
        {
            return ApiError.Problem(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Company not found.");
        }

        var scope = await scopeResolver.ResolveAsync(ct);

        // Admin/SuperAdmin see every Location in their Company; restricted roles see exactly their assigned set.
        var locationsQuery = db.Locations.AsNoTracking().Where(l => l.CompanyId == user.CompanyId);
        if (scope.LocationRestricted)
        {
            locationsQuery = locationsQuery.Where(l => scope.LocationIds.Contains(l.Id));
        }

        var locations = await locationsQuery
            .OrderBy(l => l.Name).ThenBy(l => l.Id)
            .Select(l => new LocationRef(l.Id, l.Name))
            .ToListAsync(ct);

        var response = new EffectiveScopeResponse(
            new UserRef(user.Id, user.Username, user.Username),
            RoleDto.From(user.Role),
            company,
            locations,
            UnrestrictedCompanyAccess: scope.AllCompanyLocations,
            SystemWideAccess: scope.SystemWide);

        return TypedResults.Ok(response);
    }
}
