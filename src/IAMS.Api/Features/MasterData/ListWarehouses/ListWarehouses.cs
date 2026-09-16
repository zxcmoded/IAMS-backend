using FluentValidation;
using IAMS.Api.Common.Access;
using IAMS.Api.Common.Errors;
using IAMS.Api.Common.MasterData;
using IAMS.Api.Common.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Features.MasterData.ListWarehouses;

// ── Contract ────────────────────────────────────────────────────────────────
public record WarehouseDto(
    Guid Id,
    string Name,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc,
    Guid TenantId,
    Guid LocationId,
    Guid CompanyId);

/// <summary><paramref name="ParentId"/>, when supplied, is the parent <b>Location</b> id to scope to.</summary>
public record ListWarehousesQuery(Guid? ParentId, string? Cursor, int? PageSize);

public class ListWarehousesQueryValidator : AbstractValidator<ListWarehousesQuery>
{
    public ListWarehousesQueryValidator()
    {
        RuleFor(x => x.PageSize)
            .GreaterThan(0).LessThanOrEqualTo(MasterDataPaging.MaxPageSize)
            .When(x => x.PageSize.HasValue);
        RuleFor(x => x.ParentId).NotEmpty().When(x => x.ParentId.HasValue);
    }
}

// ── Handler ─────────────────────────────────────────────────────────────────
public class ListWarehousesHandler(IamsDbContext db, AccessCheckService accessCheck)
{
    public async Task<Results<Ok<MasterDataPage<WarehouseDto>>, ProblemHttpResult>> HandleAsync(
        ListWarehousesQuery query, CancellationToken ct)
    {
        var reachable = (await accessCheck.GetReachableCompanyIdsAsync(ct)).ToArray();
        var take = query.PageSize ?? MasterDataPaging.DefaultPageSize;
        if (!SyncCursor.TryDecode(query.Cursor, out var ts, out var id))
        {
            return ApiError.Problem(StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed, "Malformed cursor.");
        }

        // Parent (Location) ownership: 404 if it doesn't exist, 403 if its company isn't reachable.
        if (query.ParentId is Guid pid)
        {
            var parentCompanyId = await db.Locations.AsNoTracking()
                .Where(l => l.Id == pid).Select(l => (Guid?)l.CompanyId).FirstOrDefaultAsync(ct);
            if (parentCompanyId is null)
            {
                return ApiError.Problem(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Parent location not found.");
            }
            if (!reachable.Contains(parentCompanyId.Value))
            {
                return ApiError.Problem(StatusCodes.Status403Forbidden, ErrorCodes.AccessDenied, "Parent location is not reachable.");
            }
        }

        var q = db.Warehouses.AsNoTracking().Where(x => reachable.Contains(x.CompanyId));
        if (query.ParentId is Guid parentId)
        {
            q = q.Where(x => x.LocationId == parentId); // defense in depth on top of the reachable filter
        }

        var rows = await q
            .Where(x => EF.Property<DateTime>(x, SyncCursor.ColumnName) > ts
                     || (EF.Property<DateTime>(x, SyncCursor.ColumnName) == ts && x.Id.CompareTo(id) > 0))
            .OrderBy(x => EF.Property<DateTime>(x, SyncCursor.ColumnName)).ThenBy(x => x.Id)
            .Take(take + 1)
            .Select(x => new
            {
                Cursor = EF.Property<DateTime>(x, SyncCursor.ColumnName),
                Dto = new WarehouseDto(x.Id, x.Name, x.IsActive, x.CreatedAtUtc, x.UpdatedAtUtc, x.TenantId, x.LocationId, x.CompanyId)
            })
            .ToListAsync(ct);

        return TypedResults.Ok(MasterDataPaging.BuildPage(rows, take, r => r.Cursor, r => r.Dto, r => r.Dto.Id));
    }
}
