using FluentValidation;
using IAMS.Api.Common.Errors;

namespace IAMS.Api.Features.MasterData.ListCompanies;

public static class ListCompaniesEndpoint
{
    public static void MapListCompaniesEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/master-data/companies", async Task<IResult> (
            string? cursor,
            int? pageSize,
            IValidator<ListCompaniesQuery> validator,
            ListCompaniesHandler handler,
            CancellationToken ct) =>
        {
            var query = new ListCompaniesQuery(cursor, pageSize);
            var errors = await validator.ValidateToDictionaryAsync(query, ct);
            if (errors is not null)
            {
                return ApiError.Validation(errors);
            }

            return await handler.HandleAsync(query, ct);
        })
        .RequireAuthorization()
        .WithName("ListCompanies")
        .WithTags("MasterData");
    }
}
