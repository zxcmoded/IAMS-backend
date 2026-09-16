using FluentValidation;
using IAMS.Api.Common.Access;
using IAMS.Api.Common.Errors;
using IAMS.Api.Common.MasterData;
using IAMS.Api.Common.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Features.MasterData.ListLocations;

// ── Contract ────────────────────────────────────────────────────────────────
public record LocationDto(
    Guid Id,
    string Name,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc,
    Guid TenantId,
    Guid CompanyId,
    string? Region);

/// <summary><paramref name="ParentId"/>, when supplied, is the parent <b>Company</b> id to scope to.</summary>
public record ListLocationsQuery(Guid? ParentId, string? Cursor, int? PageSize);

public class ListLocationsQueryValidator : AbstractValidator<ListLocationsQuery>
{
    public ListLocationsQueryValidator()
    {
        RuleFor(x => x.PageSize)
            .GreaterThan(0).LessThanOrEqualTo(MasterDataPaging.MaxPageSize)
            .When(x => x.PageSize.HasValue);
        RuleFor(x => x.ParentId).NotEmpty().When(x => x.ParentId.HasValue);
    }
}

// ── Handler ─────────────────────────────────────────────────────────────────
public class ListLocationsHandler(IamsDbContext db, AccessCheckService accessCheck)
{
    public async Task<Results<Ok<MasterDataPage<LocationDto>>, ProblemHttpResult>> HandleAsync(
        ListLocationsQuery query, CancellationToken ct)
    {
        var reachable = (await accessCheck.GetReachableCompanyIdsAsync(ct)).ToArray();
        var take = query.PageSize ?? MasterDataPaging.DefaultPageSize;
        if (!SyncCursor.TryDecode(query.Cursor, out var ts, out var id))
        {
            return ApiError.Problem(StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed, "Malformed cursor.");
        }

        // Parent (Company) ownership: 404 if it doesn't exist, 403 if it exists but isn't reachable.
        if (query.ParentId is Guid pid)
        {
            var parentCompanyId = await db.Companies.AsNoTracking()
                .Where(c => c.Id == pid).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
            if (parentCompanyId is null)
            {
                return ApiError.Problem(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Parent company not found.");
            }
            if (!reachable.Contains(parentCompanyId.Value))
            {
                return ApiError.Problem(StatusCodes.Status403Forbidden, ErrorCodes.AccessDenied, "Parent company is not reachable.");
            }
        }

        var q = db.Locations.AsNoTracking().Where(x => reachable.Contains(x.CompanyId));
        if (query.ParentId is Guid parentId)
        {
            q = q.Where(x => x.CompanyId == parentId); // defense in depth on top of the reachable filter
        }

        var rows = await q
            .Where(x => EF.Property<DateTime>(x, SyncCursor.ColumnName) > ts
                     || (EF.Property<DateTime>(x, SyncCursor.ColumnName) == ts && x.Id.CompareTo(id) > 0))
            .OrderBy(x => EF.Property<DateTime>(x, SyncCursor.ColumnName)).ThenBy(x => x.Id)
            .Take(take + 1)
            .Select(x => new
            {
                Cursor = EF.Property<DateTime>(x, SyncCursor.ColumnName),
                Dto = new LocationDto(x.Id, x.Name, x.IsActive, x.CreatedAtUtc, x.UpdatedAtUtc, x.TenantId, x.CompanyId, x.Region)
            })
            .ToListAsync(ct);

        return TypedResults.Ok(MasterDataPaging.BuildPage(rows, take, r => r.Cursor, r => r.Dto, r => r.Dto.Id));
    }
}
