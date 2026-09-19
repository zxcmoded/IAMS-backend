using FluentValidation;
using IAMS.Api.Common.Access;
using IAMS.Api.Common.Errors;
using IAMS.Api.Common.MasterData;
using IAMS.Api.Common.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Features.Inventory.SyncStockLevels;

// ── Contract ────────────────────────────────────────────────────────────────
/// <summary>
/// One <c>StockLevel</c> (per item-per-bin on-hand) row for the mobile offline cache. Includes the full
/// denormalized ancestry (bin → rack → warehouse → location → company → tenant) so the client can compute
/// aggregate on-hand and location-scoped views locally, and <see cref="Version"/> — the app-managed monotonic
/// counter the client already stamps offline mutations with and caches in <c>stock_version_cache</c> for
/// conflict detection (it's the same value the mutation endpoints return). The opaque resume cursor lives on
/// the page envelope (<see cref="MasterDataPage{T}.NextCursor"/>), not per-row. Timestamps mirror the other
/// sync DTOs: <see cref="CreatedAtUtc"/> plus nullable <see cref="UpdatedAtUtc"/> (the <c>SyncCursorUtc</c>
/// keyset basis is <c>COALESCE(UpdatedAtUtc, CreatedAtUtc)</c>).
/// </summary>
public record StockLevelSyncDto(
    Guid Id,
    Guid InventoryItemId,
    Guid BinId,
    Guid RackId,
    Guid WarehouseId,
    Guid LocationId,
    Guid CompanyId,
    Guid TenantId,
    decimal QuantityOnHand,
    long Version,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc);

public record SyncStockLevelsQuery(string? Cursor, int? PageSize);

public class SyncStockLevelsQueryValidator : AbstractValidator<SyncStockLevelsQuery>
{
    public SyncStockLevelsQueryValidator()
    {
        RuleFor(x => x.PageSize)
            .GreaterThan(0).LessThanOrEqualTo(MasterDataPaging.MaxPageSize)
            .When(x => x.PageSize.HasValue);
    }
}

// ── Handler ─────────────────────────────────────────────────────────────────
/// <summary>
/// Cursor-paginated offline sync feed for <c>StockLevels</c>, reachable-company scoped via
/// <see cref="AccessCheckService"/>. <c>StockLevel</c> denormalizes <c>CompanyId</c>/<c>TenantId</c>/ancestry
/// directly on the row, so scoping is a direct indexed <c>reachable.Contains(CompanyId)</c> filter — no join
/// up the hierarchy — exactly as the <c>ListLocations</c>/<c>ListBins</c> feeds filter. Rides the same
/// <see cref="SyncCursor"/> + <see cref="MasterDataPaging"/> keyset mechanism over the <c>SyncCursorUtc</c>
/// shadow column and <c>IX_StockLevels_Sync</c> index.
/// </summary>
public class SyncStockLevelsHandler(IamsDbContext db, AccessCheckService accessCheck)
{
    public async Task<Results<Ok<MasterDataPage<StockLevelSyncDto>>, ProblemHttpResult>> HandleAsync(
        SyncStockLevelsQuery query, CancellationToken ct)
    {
        if (!SyncCursor.TryDecode(query.Cursor, out var ts, out var id))
        {
            return ApiError.Problem(StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed, "Malformed cursor.");
        }

        var reachable = (await accessCheck.GetReachableCompanyIdsAsync(ct)).ToArray();
        var take = query.PageSize ?? MasterDataPaging.DefaultPageSize;

        var rows = await db.StockLevels.AsNoTracking()
            .Where(sl => reachable.Contains(sl.CompanyId))
            .Where(sl => EF.Property<DateTime>(sl, SyncCursor.ColumnName) > ts
                      || (EF.Property<DateTime>(sl, SyncCursor.ColumnName) == ts && sl.Id.CompareTo(id) > 0))
            .OrderBy(sl => EF.Property<DateTime>(sl, SyncCursor.ColumnName)).ThenBy(sl => sl.Id)
            .Take(take + 1)
            .Select(sl => new
            {
                Cursor = EF.Property<DateTime>(sl, SyncCursor.ColumnName),
                Dto = new StockLevelSyncDto(
                    sl.Id, sl.InventoryItemId, sl.BinId, sl.RackId, sl.WarehouseId, sl.LocationId,
                    sl.CompanyId, sl.TenantId, sl.QuantityOnHand, sl.Version, sl.CreatedAtUtc, sl.UpdatedAtUtc)
            })
            .ToListAsync(ct);

        return TypedResults.Ok(MasterDataPaging.BuildPage(rows, take, r => r.Cursor, r => r.Dto, r => r.Dto.Id));
    }
}
