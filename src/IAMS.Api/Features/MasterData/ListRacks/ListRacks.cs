using FluentValidation;
using IAMS.Api.Common.Access;
using IAMS.Api.Common.Errors;
using IAMS.Api.Common.MasterData;
using IAMS.Api.Common.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Features.MasterData.ListRacks;

// ── Contract ────────────────────────────────────────────────────────────────
public record RackDto(
    Guid Id,
    string Name,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc,
    Guid WarehouseId,
    Guid LocationId,
    Guid CompanyId);

/// <summary><paramref name="ParentId"/>, when supplied, is the parent <b>Warehouse</b> id to scope to.</summary>
public record ListRacksQuery(Guid? ParentId, string? Cursor, int? PageSize);

public class ListRacksQueryValidator : AbstractValidator<ListRacksQuery>
{
    public ListRacksQueryValidator()
    {
        RuleFor(x => x.PageSize)
            .GreaterThan(0).LessThanOrEqualTo(MasterDataPaging.MaxPageSize)
            .When(x => x.PageSize.HasValue);
        RuleFor(x => x.ParentId).NotEmpty().When(x => x.ParentId.HasValue);
    }
}

// ── Handler ─────────────────────────────────────────────────────────────────
public class ListRacksHandler(IamsDbContext db, AccessScopeResolver scopeResolver)
{
    public async Task<Results<Ok<MasterDataPage<RackDto>>, ProblemHttpResult>> HandleAsync(
        ListRacksQuery query, CancellationToken ct)
    {
        var scope = await scopeResolver.ResolveAsync(ct);
        var locationIds = scope.LocationIds;
        var take = query.PageSize ?? MasterDataPaging.DefaultPageSize;
        if (!SyncCursor.TryDecode(query.Cursor, out var ts, out var id))
        {
            return ApiError.Problem(StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed, "Malformed cursor.");
        }

        // Parent (Warehouse) ownership: 404 if it doesn't exist, 403 if it isn't in the caller's scope.
        if (query.ParentId is Guid pid)
        {
            var parent = await db.Warehouses.AsNoTracking()
                .Where(w => w.Id == pid).Select(w => new { w.CompanyId, w.LocationId }).FirstOrDefaultAsync(ct);
            if (parent is null)
            {
                return ApiError.Problem(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Parent warehouse not found.");
            }
            if (!scope.LocationInScope(parent.CompanyId, parent.LocationId))
            {
                return ApiError.Problem(StatusCodes.Status403Forbidden, ErrorCodes.AccessDenied, "Parent warehouse is not in your scope.");
            }
        }

        var q = db.Racks.AsNoTracking();
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
            q = q.Where(x => x.WarehouseId == parentId);
        }

        var rows = await q
            .Where(x => EF.Property<DateTime>(x, SyncCursor.ColumnName) > ts
                     || (EF.Property<DateTime>(x, SyncCursor.ColumnName) == ts && x.Id.CompareTo(id) > 0))
            .OrderBy(x => EF.Property<DateTime>(x, SyncCursor.ColumnName)).ThenBy(x => x.Id)
            .Take(take + 1)
            .Select(x => new
            {
                Cursor = EF.Property<DateTime>(x, SyncCursor.ColumnName),
                Dto = new RackDto(x.Id, x.Name, x.IsActive, x.CreatedAtUtc, x.UpdatedAtUtc, x.WarehouseId, x.LocationId, x.CompanyId)
            })
            .ToListAsync(ct);

        return TypedResults.Ok(MasterDataPaging.BuildPage(rows, take, r => r.Cursor, r => r.Dto, r => r.Dto.Id));
    }
}
