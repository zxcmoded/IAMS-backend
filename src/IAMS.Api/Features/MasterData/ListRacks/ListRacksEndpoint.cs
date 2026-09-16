using FluentValidation;
using IAMS.Api.Common.Errors;

namespace IAMS.Api.Features.MasterData.ListRacks;

public static class ListRacksEndpoint
{
    public static void MapListRacksEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/master-data/racks", async Task<IResult> (
            Guid? parentId,
            string? cursor,
            int? pageSize,
            IValidator<ListRacksQuery> validator,
            ListRacksHandler handler,
            CancellationToken ct) =>
        {
            var query = new ListRacksQuery(parentId, cursor, pageSize);
            var errors = await validator.ValidateToDictionaryAsync(query, ct);
            if (errors is not null)
            {
                return ApiError.Validation(errors);
            }

            return await handler.HandleAsync(query, ct);
        })
        .RequireAuthorization()
        .WithName("ListRacks")
        .WithTags("MasterData");
    }
}
