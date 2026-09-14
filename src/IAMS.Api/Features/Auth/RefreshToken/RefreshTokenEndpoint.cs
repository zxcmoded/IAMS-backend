using FluentValidation;
using IAMS.Api.Common.Errors;

namespace IAMS.Api.Features.Auth.RefreshToken;

public static class RefreshTokenEndpoint
{
    public static void MapRefreshTokenEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/refresh", async Task<IResult> (
            RefreshTokenCommand command,
            IValidator<RefreshTokenCommand> validator,
            RefreshTokenHandler handler,
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
        .WithName("RefreshToken")
        .WithTags("Auth");
    }
}
