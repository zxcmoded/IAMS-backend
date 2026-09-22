using IAMS.Api.Common.Access;
namespace IAMS.Api.Features.Inventory.ApproveStockCount;

public static class ApproveStockCountEndpoint
{
    public static void MapApproveStockCountEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/inventory/counts/{id:guid}/approve", async Task<IResult> (
            Guid id,
            ApproveStockCountHandler handler,
            CancellationToken ct) => await handler.HandleAsync(id, ct))
        .RequireAuthorization(Policies.ManageInventory)
        .WithName("ApproveStockCount")
        .WithTags("Inventory");
    }
}
