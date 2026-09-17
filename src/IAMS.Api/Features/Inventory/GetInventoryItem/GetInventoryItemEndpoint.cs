namespace IAMS.Api.Features.Inventory.GetInventoryItem;

public static class GetInventoryItemEndpoint
{
    public static void MapGetInventoryItemEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/inventory/items/{id:guid}", async Task<IResult> (
            Guid id,
            GetInventoryItemHandler handler,
            CancellationToken ct) => await handler.HandleAsync(id, ct))
        .RequireAuthorization()
        .WithName("GetInventoryItem")
        .WithTags("Inventory");
    }
}
