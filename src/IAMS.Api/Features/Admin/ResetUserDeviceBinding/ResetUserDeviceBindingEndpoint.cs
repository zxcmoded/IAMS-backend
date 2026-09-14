using FluentValidation;
using IAMS.Api.Common.Errors;

namespace IAMS.Api.Features.Admin.ResetUserDeviceBinding;

public static class ResetUserDeviceBindingEndpoint
{
    public static void MapResetUserDeviceBindingEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/admin/users/{userId:guid}/device-binding/reset", async Task<IResult> (
            Guid userId,
            IValidator<ResetUserDeviceBindingCommand> validator,
            ResetUserDeviceBindingHandler handler,
            CancellationToken ct) =>
        {
            var command = new ResetUserDeviceBindingCommand(userId);
            var errors = await validator.ValidateToDictionaryAsync(command, ct);
            if (errors is not null)
            {
                return ApiError.Validation(errors);
            }

            return await handler.HandleAsync(command, ct);
        })
        .RequireAuthorization("SystemAdmin")
        .WithName("ResetUserDeviceBinding")
        .WithTags("Admin");
    }
}
