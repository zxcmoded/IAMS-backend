using FluentValidation;
using IAMS.Api.Common;
using IAMS.Api.Common.Errors;

namespace IAMS.Api.Features.Auth.ResendTwoFactor;

public static class ResendTwoFactorEndpoint
{
    public static void MapResendTwoFactorEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/2fa/resend", async Task<IResult> (
            ResendTwoFactorCommand command,
            IValidator<ResendTwoFactorCommand> validator,
            ResendTwoFactorHandler handler,
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
        .WithName("ResendTwoFactor")
        .WithTags("Auth");
    }
}
