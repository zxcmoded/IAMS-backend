using FluentValidation;
using IAMS.Api.Common.Errors;

namespace IAMS.Api.Features.Access.EvaluateAccessBatch;

public static class EvaluateAccessBatchEndpoint
{
    public static void MapEvaluateAccessBatchEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/access/evaluate-batch", async Task<IResult> (
            EvaluateAccessBatchCommand command,
            IValidator<EvaluateAccessBatchCommand> validator,
            EvaluateAccessBatchHandler handler,
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
        .WithName("EvaluateAccessBatch")
        .WithTags("Access");
    }
}
