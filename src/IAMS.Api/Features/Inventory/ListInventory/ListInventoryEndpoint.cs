using FluentValidation;
using IAMS.Api.Common.Errors;

namespace IAMS.Api.Features.Inventory.ListInventory;

public static class ListInventoryEndpoint
{
    public static void MapListInventoryEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/inventory/items", async Task<IResult> (
            string? search,
            string? filter,
            decimal? lowStockThreshold,
            int? page,
            int? pageSize,
            IValidator<ListInventoryQuery> validator,
            ListInventoryHandler handler,
            CancellationToken ct) =>
        {
            var query = new ListInventoryQuery(search, filter, lowStockThreshold, page, pageSize);
            var errors = await validator.ValidateToDictionaryAsync(query, ct);
            if (errors is not null)
            {
                return ApiError.Validation(errors);
            }

            return await handler.HandleAsync(query, ct);
        })
        .RequireAuthorization()
        .WithName("ListInventory")
        .WithTags("Inventory");
    }
}
