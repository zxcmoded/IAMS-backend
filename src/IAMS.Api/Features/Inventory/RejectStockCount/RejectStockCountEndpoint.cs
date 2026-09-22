using IAMS.Api.Common.Access;
using FluentValidation;
using IAMS.Api.Common.Errors;

namespace IAMS.Api.Features.Inventory.RejectStockCount;

public static class RejectStockCountEndpoint
{
    public static void MapRejectStockCountEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/inventory/counts/{id:guid}/reject", async Task<IResult> (
            Guid id,
            RejectStockCountCommand? command,
            IValidator<RejectStockCountCommand> validator,
            RejectStockCountHandler handler,
            CancellationToken ct) =>
        {
            command ??= new RejectStockCountCommand(null);
            var errors = await validator.ValidateToDictionaryAsync(command, ct);
            if (errors is not null)
            {
                return ApiError.Validation(errors);
            }

            return await handler.HandleAsync(id, command, ct);
        })
        .RequireAuthorization(Policies.ManageInventory)
        .WithName("RejectStockCount")
        .WithTags("Inventory");
    }
}
