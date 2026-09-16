using FluentValidation;
using IAMS.Api.Common.Errors;

namespace IAMS.Api.Features.MasterData.ListLocations;

public static class ListLocationsEndpoint
{
    public static void MapListLocationsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/master-data/locations", async Task<IResult> (
            Guid? parentId,
            string? cursor,
            int? pageSize,
            IValidator<ListLocationsQuery> validator,
            ListLocationsHandler handler,
            CancellationToken ct) =>
        {
            var query = new ListLocationsQuery(parentId, cursor, pageSize);
            var errors = await validator.ValidateToDictionaryAsync(query, ct);
            if (errors is not null)
            {
                return ApiError.Validation(errors);
            }

            return await handler.HandleAsync(query, ct);
        })
        .RequireAuthorization()
        .WithName("ListLocations")
        .WithTags("MasterData");
    }
}
