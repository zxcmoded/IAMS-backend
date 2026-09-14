using FluentValidation;
using IAMS.Api.Common.Errors;

namespace IAMS.Api.Features.Access.EvaluateAccess;

public static class EvaluateAccessEndpoint
{
    public static void MapEvaluateAccessEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/access/evaluate", async Task<IResult> (
            EvaluateAccessCommand command,
            IValidator<EvaluateAccessCommand> validator,
            EvaluateAccessHandler handler,
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
        .WithName("EvaluateAccess")
        .WithTags("Access");
    }
}
