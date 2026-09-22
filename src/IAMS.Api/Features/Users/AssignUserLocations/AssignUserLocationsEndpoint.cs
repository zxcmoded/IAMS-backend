using FluentValidation;
using IAMS.Api.Common.Access;
using IAMS.Api.Common.Errors;

namespace IAMS.Api.Features.Users.AssignUserLocations;

public static class AssignUserLocationsEndpoint
{
    public static void MapAssignUserLocationsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPut("/api/users/{userId:guid}/locations", async Task<IResult> (
            Guid userId,
            AssignUserLocationsCommand command,
            IValidator<AssignUserLocationsCommand> validator,
            AssignUserLocationsHandler handler,
            CancellationToken ct) =>
        {
            var errors = await validator.ValidateToDictionaryAsync(command, ct);
            if (errors is not null)
            {
                return ApiError.Validation(errors);
            }

            return await handler.HandleAsync(userId, command, ct);
        })
        .RequireAuthorization(Policies.ManageCompany)
        .WithName("AssignUserLocations")
        .WithTags("Users");
    }
}
