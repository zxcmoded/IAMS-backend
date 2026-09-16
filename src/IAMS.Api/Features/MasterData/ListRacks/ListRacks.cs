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
    Guid TenantId,
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
public class ListRacksHandler(IamsDbContext db, AccessCheckService accessCheck)
{
    public async Task<Results<Ok<MasterDataPage<RackDto>>, ProblemHttpResult>> HandleAsync(
        ListRacksQuery query, CancellationToken ct)
    {
        var reachable = (await accessCheck.GetReachableCompanyIdsAsync(ct)).ToArray();
        var take = query.PageSize ?? MasterDataPaging.DefaultPageSize;
        if (!SyncCursor.TryDecode(query.Cursor, out var ts, out var id))
        {
            return ApiError.Problem(StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed, "Malformed cursor.");
        }

        // Parent (Warehouse) ownership: 404 if it doesn't exist, 403 if its company isn't reachable.
        if (query.ParentId is Guid pid)
        {
            var parentCompanyId = await db.Warehouses.AsNoTracking()
                .Where(w => w.Id == pid).Select(w => (Guid?)w.CompanyId).FirstOrDefaultAsync(ct);
            if (parentCompanyId is null)
            {
                return ApiError.Problem(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Parent warehouse not found.");
            }
            if (!reachable.Contains(parentCompanyId.Value))
            {
                return ApiError.Problem(StatusCodes.Status403Forbidden, ErrorCodes.AccessDenied, "Parent warehouse is not reachable.");
            }
        }

        var q = db.Racks.AsNoTracking().Where(x => reachable.Contains(x.CompanyId));
        if (query.ParentId is Guid parentId)
        {
            q = q.Where(x => x.WarehouseId == parentId); // defense in depth on top of the reachable filter
        }

        var rows = await q
            .Where(x => EF.Property<DateTime>(x, SyncCursor.ColumnName) > ts
                     || (EF.Property<DateTime>(x, SyncCursor.ColumnName) == ts && x.Id.CompareTo(id) > 0))
            .OrderBy(x => EF.Property<DateTime>(x, SyncCursor.ColumnName)).ThenBy(x => x.Id)
            .Take(take + 1)
            .Select(x => new
            {
                Cursor = EF.Property<DateTime>(x, SyncCursor.ColumnName),
                Dto = new RackDto(x.Id, x.Name, x.IsActive, x.CreatedAtUtc, x.UpdatedAtUtc, x.TenantId, x.WarehouseId, x.LocationId, x.CompanyId)
            })
            .ToListAsync(ct);

        return TypedResults.Ok(MasterDataPaging.BuildPage(rows, take, r => r.Cursor, r => r.Dto, r => r.Dto.Id));
    }
}
