using IAMS.Api.Common.Access;

namespace IAMS.Api.Features.Users.GetUserLocations;

public static class GetUserLocationsEndpoint
{
    public static void MapGetUserLocationsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/users/{userId:guid}/locations", async Task<IResult> (
            Guid userId,
            GetUserLocationsHandler handler,
            CancellationToken ct) =>
        {
            return await handler.HandleAsync(userId, ct);
        })
        .RequireAuthorization(Policies.ManageCompany)
        .WithName("GetUserLocations")
        .WithTags("Users");
    }
}
