using FluentValidation;
using IAMS.Api.Common.Errors;

namespace IAMS.Api.Features.MasterData.ListBins;

public static class ListBinsEndpoint
{
    public static void MapListBinsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/master-data/bins", async Task<IResult> (
            Guid? parentId,
            string? cursor,
            int? pageSize,
            IValidator<ListBinsQuery> validator,
            ListBinsHandler handler,
            CancellationToken ct) =>
        {
            var query = new ListBinsQuery(parentId, cursor, pageSize);
            var errors = await validator.ValidateToDictionaryAsync(query, ct);
            if (errors is not null)
            {
                return ApiError.Validation(errors);
            }

            return await handler.HandleAsync(query, ct);
        })
        .RequireAuthorization()
        .WithName("ListBins")
        .WithTags("MasterData");
    }
}
