using FluentValidation;
using IAMS.Api.Common.Errors;

namespace IAMS.Api.Features.Scanning.ResolveScan;

public static class ResolveScanEndpoint
{
    public static void MapResolveScanEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/scan/resolve", async Task<IResult> (
            ResolveScanCommand command,
            IValidator<ResolveScanCommand> validator,
            ResolveScanHandler handler,
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
        .WithName("ResolveScan")
        .WithTags("Scanning");
    }
}
