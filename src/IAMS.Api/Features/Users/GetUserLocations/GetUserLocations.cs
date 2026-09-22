using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Errors;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Security;
using IAMS.Api.Features.Users;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Features.Users.GetUserLocations;

// ── Handler ─────────────────────────────────────────────────────────────────
/// <summary>
/// Lists a user's assigned Locations. Admin-and-above only (policy-gated at the endpoint); an
/// <see cref="UserRole.Admin"/> caller may only read users in their own Company, while
/// <see cref="UserRole.SuperAdmin"/> may read any. A target user outside the caller's Company is 404.
/// </summary>
public class GetUserLocationsHandler(IamsDbContext db, ICurrentUser currentUser)
{
    public async Task<Results<Ok<UserLocationsResponse>, ProblemHttpResult>> HandleAsync(
        Guid userId, CancellationToken ct)
    {
        var target = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => new { u.Id, u.CompanyId })
            .FirstOrDefaultAsync(ct);

        if (target is null || (currentUser.Role != UserRole.SuperAdmin && target.CompanyId != currentUser.CompanyId))
        {
            return ApiError.Problem(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "User not found.");
        }

        var locations = await db.UserLocationAssignments.AsNoTracking()
            .Where(a => a.UserId == userId)
            .Select(a => new AssignedLocationDto(a.Location.Id, a.Location.Name))
            .OrderBy(l => l.Name).ThenBy(l => l.Id)
            .ToListAsync(ct);

        return TypedResults.Ok(new UserLocationsResponse(userId, locations));
    }
}
