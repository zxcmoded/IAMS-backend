using FluentValidation;
using IAMS.Api.Common.Errors;

namespace IAMS.Api.Features.MasterData.ListWarehouses;

public static class ListWarehousesEndpoint
{
    public static void MapListWarehousesEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/master-data/warehouses", async Task<IResult> (
            Guid? parentId,
            string? cursor,
            int? pageSize,
            IValidator<ListWarehousesQuery> validator,
            ListWarehousesHandler handler,
            CancellationToken ct) =>
        {
            var query = new ListWarehousesQuery(parentId, cursor, pageSize);
            var errors = await validator.ValidateToDictionaryAsync(query, ct);
            if (errors is not null)
            {
                return ApiError.Validation(errors);
            }

            return await handler.HandleAsync(query, ct);
        })
        .RequireAuthorization()
        .WithName("ListWarehouses")
        .WithTags("MasterData");
    }
}
