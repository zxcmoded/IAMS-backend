using FluentValidation;
using IAMS.Api.Common;
using IAMS.Api.Common.Errors;

namespace IAMS.Api.Features.Auth.Activate;

public static class ActivateEndpoint
{
    public static void MapActivateEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/activate", async Task<IResult> (
            ActivateCommand command,
            IValidator<ActivateCommand> validator,
            ActivateHandler handler,
            CancellationToken ct) =>
        {
            var errors = await validator.ValidateToDictionaryAsync(command, ct);
            if (errors is not null)
            {
                return ApiError.Validation(errors);
            }

            return await handler.HandleAsync(command, ct);
        })
        .AllowAnonymous()
        .RequireRateLimiting(DependencyInjection.AuthRateLimitPolicy)
        .WithName("Activate")
        .WithTags("Auth");
    }
}
