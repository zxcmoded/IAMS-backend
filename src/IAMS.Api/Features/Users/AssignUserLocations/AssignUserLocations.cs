using FluentValidation;
using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Errors;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Security;
using IAMS.Api.Common.Time;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Features.Users.AssignUserLocations;

// ── Contract ────────────────────────────────────────────────────────────────
/// <summary>Replaces the target user's entire Location-assignment set with <paramref name="LocationIds"/>.</summary>
public record AssignUserLocationsCommand(IReadOnlyList<Guid> LocationIds);

public class AssignUserLocationsValidator : AbstractValidator<AssignUserLocationsCommand>
{
    public AssignUserLocationsValidator()
    {
        RuleFor(x => x.LocationIds).NotNull();
        RuleForEach(x => x.LocationIds).NotEmpty();
        RuleFor(x => x.LocationIds)
            .Must(ids => ids.Distinct().Count() == ids.Count)
            .When(x => x.LocationIds is not null)
            .WithMessage("LocationIds must not contain duplicates.");
        RuleFor(x => x.LocationIds.Count).LessThanOrEqualTo(1000)
            .When(x => x.LocationIds is not null);
    }
}

// ── Handler ─────────────────────────────────────────────────────────────────
/// <summary>
/// Replaces a user's assigned-Location set (many-to-many <c>UserLocationAssignments</c>). Admin-and-above
/// only (policy-gated at the endpoint); an <see cref="UserRole.Admin"/> caller is additionally confined to
/// their own Company — both the target user and every assigned Location must belong to it — while
/// <see cref="UserRole.SuperAdmin"/> may manage any Company. A target user outside the caller's Company is
/// reported as 404 (not revealed). Every supplied Location must exist and belong to the target user's Company,
/// otherwise 400. An empty list is valid and clears all assignments.
/// </summary>
public class AssignUserLocationsHandler(IamsDbContext db, ICurrentUser currentUser, IClock clock)
{
    public async Task<Results<Ok<UserLocationsResponse>, ProblemHttpResult>> HandleAsync(
        Guid userId, AssignUserLocationsCommand command, CancellationToken ct)
    {
        var target = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => new { u.Id, u.CompanyId })
            .FirstOrDefaultAsync(ct);

        // An Admin may only manage users in their own Company; a target elsewhere is "not found" to them.
        if (target is null || (currentUser.Role != UserRole.SuperAdmin && target.CompanyId != currentUser.CompanyId))
        {
            return ApiError.Problem(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "User not found.");
        }

        var requested = command.LocationIds.Distinct().ToList();

        // Every requested Location must exist AND belong to the target user's Company.
        var validIds = await db.Locations.AsNoTracking()
            .Where(l => l.CompanyId == target.CompanyId && requested.Contains(l.Id))
            .Select(l => l.Id)
            .ToListAsync(ct);

        var invalid = requested.Except(validIds).ToList();
        if (invalid.Count > 0)
        {
            return ApiError.Problem(
                StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed,
                "One or more Locations do not exist in the user's Company.", null,
                new Dictionary<string, object?> { ["invalidLocationIds"] = invalid });
        }

        // Replace the full set: remove existing rows, add the requested ones.
        var existing = await db.UserLocationAssignments
            .Where(a => a.UserId == userId)
            .ToListAsync(ct);
        db.UserLocationAssignments.RemoveRange(existing);

        var now = clock.UtcNow.UtcDateTime;
        foreach (var locationId in requested)
        {
            db.UserLocationAssignments.Add(new UserLocationAssignment
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                LocationId = locationId,
                CreatedAtUtc = now
            });
        }

        await db.SaveChangesAsync(ct);

        var locations = await db.Locations.AsNoTracking()
            .Where(l => requested.Contains(l.Id))
            .OrderBy(l => l.Name).ThenBy(l => l.Id)
            .Select(l => new AssignedLocationDto(l.Id, l.Name))
            .ToListAsync(ct);

        return TypedResults.Ok(new UserLocationsResponse(userId, locations));
    }
}
