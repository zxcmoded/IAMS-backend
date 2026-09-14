using FluentValidation;
using IAMS.Api.Common.Errors;

namespace IAMS.Api.Features.Admin.ResetUserActivation;

public static class ResetUserActivationEndpoint
{
    public static void MapResetUserActivationEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/admin/users/{userId:guid}/activation/reset", async Task<IResult> (
            Guid userId,
            IValidator<ResetUserActivationCommand> validator,
            ResetUserActivationHandler handler,
            CancellationToken ct) =>
        {
            var command = new ResetUserActivationCommand(userId);
            var errors = await validator.ValidateToDictionaryAsync(command, ct);
            if (errors is not null)
            {
                return ApiError.Validation(errors);
            }

            return await handler.HandleAsync(command, ct);
        })
        .RequireAuthorization("SystemAdmin")
        .WithName("ResetUserActivation")
        .WithTags("Admin");
    }
}
