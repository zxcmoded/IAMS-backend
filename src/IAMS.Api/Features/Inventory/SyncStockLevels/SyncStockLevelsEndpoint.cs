using FluentValidation;
using IAMS.Api.Common.Errors;

namespace IAMS.Api.Features.Inventory.SyncStockLevels;

public static class SyncStockLevelsEndpoint
{
    public static void MapSyncStockLevelsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/inventory/sync/stock-levels", async Task<IResult> (
            string? cursor,
            int? pageSize,
            IValidator<SyncStockLevelsQuery> validator,
            SyncStockLevelsHandler handler,
            CancellationToken ct) =>
        {
            var query = new SyncStockLevelsQuery(cursor, pageSize);
            var errors = await validator.ValidateToDictionaryAsync(query, ct);
            if (errors is not null)
            {
                return ApiError.Validation(errors);
            }

            return await handler.HandleAsync(query, ct);
        })
        .RequireAuthorization()
        .WithName("SyncStockLevels")
        .WithTags("Inventory");
    }
}
