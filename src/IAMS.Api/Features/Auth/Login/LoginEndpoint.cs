using FluentValidation;
using IAMS.Api.Common;
using IAMS.Api.Common.Errors;

namespace IAMS.Api.Features.Auth.Login;

public static class LoginEndpoint
{
    public static void MapLoginEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login", async Task<IResult> (
            LoginCommand command,
            IValidator<LoginCommand> validator,
            LoginHandler handler,
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
        .WithName("Login")
        .WithTags("Auth");
    }
}
