using FluentValidation;
using IAMS.Api.Common.Access;
using IAMS.Api.Common.Errors;
using IAMS.Api.Common.MasterData;
using IAMS.Api.Common.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Features.MasterData.ListCompanies;

// ── Contract ────────────────────────────────────────────────────────────────
public record CompanyDto(
    Guid Id,
    string Name,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc,
    Guid TenantId);

public record ListCompaniesQuery(string? Cursor, int? PageSize);

public class ListCompaniesQueryValidator : AbstractValidator<ListCompaniesQuery>
{
    public ListCompaniesQueryValidator()
    {
        RuleFor(x => x.PageSize)
            .GreaterThan(0).LessThanOrEqualTo(MasterDataPaging.MaxPageSize)
            .When(x => x.PageSize.HasValue);
    }
}

// ── Handler ─────────────────────────────────────────────────────────────────
public class ListCompaniesHandler(IamsDbContext db, AccessCheckService accessCheck)
{
    public async Task<Results<Ok<MasterDataPage<CompanyDto>>, ProblemHttpResult>> HandleAsync(
        ListCompaniesQuery query, CancellationToken ct)
    {
        if (!SyncCursor.TryDecode(query.Cursor, out var ts, out var id))
        {
            return ApiError.Problem(StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed, "Malformed cursor.");
        }

        var reachable = (await accessCheck.GetReachableCompanyIdsAsync(ct)).ToArray();
        var take = query.PageSize ?? MasterDataPaging.DefaultPageSize;

        var rows = await db.Companies.AsNoTracking()
            .Where(c => reachable.Contains(c.Id))
            .Where(c => EF.Property<DateTime>(c, SyncCursor.ColumnName) > ts
                     || (EF.Property<DateTime>(c, SyncCursor.ColumnName) == ts && c.Id.CompareTo(id) > 0))
            .OrderBy(c => EF.Property<DateTime>(c, SyncCursor.ColumnName)).ThenBy(c => c.Id)
            .Take(take + 1)
            .Select(c => new
            {
                Cursor = EF.Property<DateTime>(c, SyncCursor.ColumnName),
                Dto = new CompanyDto(c.Id, c.Name, c.IsActive, c.CreatedAtUtc, c.UpdatedAtUtc, c.TenantId)
            })
            .ToListAsync(ct);

        return TypedResults.Ok(MasterDataPaging.BuildPage(rows, take, r => r.Cursor, r => r.Dto, r => r.Dto.Id));
    }
}
