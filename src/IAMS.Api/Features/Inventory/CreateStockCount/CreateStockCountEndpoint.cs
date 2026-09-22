using IAMS.Api.Common.Access;
using FluentValidation;
using IAMS.Api.Common.Errors;

namespace IAMS.Api.Features.Inventory.CreateStockCount;

public static class CreateStockCountEndpoint
{
    public static void MapCreateStockCountEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/inventory/counts", async Task<IResult> (
            CreateStockCountCommand command,
            IValidator<CreateStockCountCommand> validator,
            CreateStockCountHandler handler,
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
        .WithName("CreateStockCount")
        .WithTags("Inventory");
    }
}
