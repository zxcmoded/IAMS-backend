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
public class ListWarehousesHandler(IamsDbContext db, AccessScopeResolver scopeResolver)
{
    public async Task<Results<Ok<MasterDataPage<WarehouseDto>>, ProblemHttpResult>> HandleAsync(
        ListWarehousesQuery query, CancellationToken ct)
    {
        var scope = await scopeResolver.ResolveAsync(ct);
        var locationIds = scope.LocationIds;
        var take = query.PageSize ?? MasterDataPaging.DefaultPageSize;
        if (!SyncCursor.TryDecode(query.Cursor, out var ts, out var id))
        {
            return ApiError.Problem(StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed, "Malformed cursor.");
        }

        // Parent (Location) ownership: 404 if it doesn't exist, 403 if it isn't in the caller's scope.
        if (query.ParentId is Guid pid)
        {
            var parent = await db.Locations.AsNoTracking()
                .Where(l => l.Id == pid).Select(l => new { l.CompanyId }).FirstOrDefaultAsync(ct);
            if (parent is null)
            {
                return ApiError.Problem(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Parent location not found.");
            }
            if (!scope.LocationInScope(parent.CompanyId, pid))
            {
                return ApiError.Problem(StatusCodes.Status403Forbidden, ErrorCodes.AccessDenied, "Parent location is not in your scope.");
            }
        }

        var q = db.Warehouses.AsNoTracking();
        if (!scope.SystemWide)
        {
            q = q.Where(x => x.CompanyId == scope.CompanyId);
        }
        if (scope.LocationRestricted)
        {
            q = q.Where(x => locationIds.Contains(x.LocationId));
        }
        if (query.ParentId is Guid parentId)
        {
            q = q.Where(x => x.LocationId == parentId);
        }

        var rows = await q
            .Where(x => EF.Property<DateTime>(x, SyncCursor.ColumnName) > ts
                     || (EF.Property<DateTime>(x, SyncCursor.ColumnName) == ts && x.Id.CompareTo(id) > 0))
            .OrderBy(x => EF.Property<DateTime>(x, SyncCursor.ColumnName)).ThenBy(x => x.Id)
            .Take(take + 1)
            .Select(x => new
            {
                Cursor = EF.Property<DateTime>(x, SyncCursor.ColumnName),
                Dto = new WarehouseDto(x.Id, x.Name, x.IsActive, x.CreatedAtUtc, x.UpdatedAtUtc, x.LocationId, x.CompanyId)
            })
            .ToListAsync(ct);

        return TypedResults.Ok(MasterDataPaging.BuildPage(rows, take, r => r.Cursor, r => r.Dto, r => r.Dto.Id));
    }
}
