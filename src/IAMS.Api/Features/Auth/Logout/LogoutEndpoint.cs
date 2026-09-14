using FluentValidation;
using IAMS.Api.Common.Errors;

namespace IAMS.Api.Features.Auth.Logout;

public static class LogoutEndpoint
{
    public static void MapLogoutEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/logout", async Task<IResult> (
            LogoutCommand command,
            IValidator<LogoutCommand> validator,
            LogoutHandler handler,
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
        .WithName("Logout")
        .WithTags("Auth");
    }
}
