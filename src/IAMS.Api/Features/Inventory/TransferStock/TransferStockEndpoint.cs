using FluentValidation;
using IAMS.Api.Common.Errors;

namespace IAMS.Api.Features.Inventory.TransferStock;

public static class TransferStockEndpoint
{
    public static void MapTransferStockEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/inventory/transfer", async Task<IResult> (
            TransferStockCommand command,
            IValidator<TransferStockCommand> validator,
            TransferStockHandler handler,
            CancellationToken ct) =>
        {
            var errors = await validator.ValidateToDictionaryAsync(command, ct);
            if (errors is not null)
            {
                return ApiError.Validation(errors);
            }

            return await handler.HandleAsync(command, ct);
        })
        .RequireAuthorization()
        .WithName("TransferStock")
        .WithTags("Inventory");
    }
}
