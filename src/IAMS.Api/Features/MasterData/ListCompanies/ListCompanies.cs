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
    DateTime? UpdatedAtUtc);

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
/// <summary>
/// Lists Companies the caller can see: their own single Company for every scoped role; for SuperAdmin (system
/// wide) every Company. Company is the top-level unit now, so this is not location-scoped.
/// </summary>
public class ListCompaniesHandler(IamsDbContext db, AccessScopeResolver scopeResolver)
{
    public async Task<Results<Ok<MasterDataPage<CompanyDto>>, ProblemHttpResult>> HandleAsync(
        ListCompaniesQuery query, CancellationToken ct)
    {
        if (!SyncCursor.TryDecode(query.Cursor, out var ts, out var id))
        {
            return ApiError.Problem(StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed, "Malformed cursor.");
        }

        var scope = await scopeResolver.ResolveAsync(ct);
        var take = query.PageSize ?? MasterDataPaging.DefaultPageSize;

        var q = db.Companies.AsNoTracking();
        if (!scope.SystemWide)
        {
            q = q.Where(c => c.Id == scope.CompanyId);
        }

        var rows = await q
            .Where(c => EF.Property<DateTime>(c, SyncCursor.ColumnName) > ts
                     || (EF.Property<DateTime>(c, SyncCursor.ColumnName) == ts && c.Id.CompareTo(id) > 0))
            .OrderBy(c => EF.Property<DateTime>(c, SyncCursor.ColumnName)).ThenBy(c => c.Id)
            .Take(take + 1)
            .Select(c => new
            {
                Cursor = EF.Property<DateTime>(c, SyncCursor.ColumnName),
                Dto = new CompanyDto(c.Id, c.Name, c.IsActive, c.CreatedAtUtc, c.UpdatedAtUtc)
            })
            .ToListAsync(ct);

        return TypedResults.Ok(MasterDataPaging.BuildPage(rows, take, r => r.Cursor, r => r.Dto, r => r.Dto.Id));
    }
}
