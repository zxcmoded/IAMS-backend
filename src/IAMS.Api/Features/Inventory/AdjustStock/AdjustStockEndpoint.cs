using IAMS.Api.Common.Access;
using FluentValidation;
using IAMS.Api.Common.Errors;

namespace IAMS.Api.Features.Inventory.AdjustStock;

public static class AdjustStockEndpoint
{
    public static void MapAdjustStockEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/inventory/adjust", async Task<IResult> (
            AdjustStockCommand command,
            IValidator<AdjustStockCommand> validator,
            AdjustStockHandler handler,
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
        .WithName("AdjustStock")
        .WithTags("Inventory");
    }
}
