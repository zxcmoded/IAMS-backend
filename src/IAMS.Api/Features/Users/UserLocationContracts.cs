namespace IAMS.Api.Features.Users;

/// <summary>One Location a user is assigned to.</summary>
public record AssignedLocationDto(Guid Id, string Name);

/// <summary>The current set of Locations a user is assigned to (shared by the assign + list slices).</summary>
public record UserLocationsResponse(Guid UserId, IReadOnlyList<AssignedLocationDto> Locations);
