using FluentValidation;
using IAMS.Api.Common.Errors;

namespace IAMS.Api.Features.Inventory.SyncItems;

public static class SyncItemsEndpoint
{
    public static void MapSyncItemsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/inventory/sync/items", async Task<IResult> (
            string? cursor,
            int? pageSize,
            IValidator<SyncItemsQuery> validator,
            SyncItemsHandler handler,
            CancellationToken ct) =>
        {
            var query = new SyncItemsQuery(cursor, pageSize);
            var errors = await validator.ValidateToDictionaryAsync(query, ct);
            if (errors is not null)
            {
                return ApiError.Validation(errors);
            }

            return await handler.HandleAsync(query, ct);
        })
        .RequireAuthorization()
        .WithName("SyncInventoryItems")
        .WithTags("Inventory");
    }
}
