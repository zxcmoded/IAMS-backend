using FluentValidation;
using IAMS.Api.Common;
using IAMS.Api.Common.Errors;

namespace IAMS.Api.Features.Auth.VerifyTwoFactor;

public static class VerifyTwoFactorEndpoint
{
    public static void MapVerifyTwoFactorEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/2fa/verify", async Task<IResult> (
            VerifyTwoFactorCommand command,
            IValidator<VerifyTwoFactorCommand> validator,
            VerifyTwoFactorHandler handler,
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
        .WithName("VerifyTwoFactor")
        .WithTags("Auth");
    }
}
