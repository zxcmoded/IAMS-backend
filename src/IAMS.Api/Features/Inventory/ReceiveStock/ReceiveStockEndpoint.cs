using IAMS.Api.Common.Access;
using FluentValidation;
using IAMS.Api.Common.Errors;

namespace IAMS.Api.Features.Inventory.ReceiveStock;

public static class ReceiveStockEndpoint
{
    public static void MapReceiveStockEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/inventory/receive", async Task<IResult> (
            ReceiveStockCommand command,
            IValidator<ReceiveStockCommand> validator,
            ReceiveStockHandler handler,
            CancellationToken ct) =>
        {
            var errors = await validator.ValidateToDictionaryAsync(command, ct);
            if (errors is not null)
            {
                return ApiError.Validation(errors);
            }

            return await handler.HandleAsync(command, ct);
        })
        .RequireAuthorization(Policies.Write)
        .WithName("ReceiveStock")
        .WithTags("Inventory");
    }
}
