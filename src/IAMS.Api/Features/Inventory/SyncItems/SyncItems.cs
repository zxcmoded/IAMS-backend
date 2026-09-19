using FluentValidation;
using IAMS.Api.Common.Access;
using IAMS.Api.Common.Errors;
using IAMS.Api.Common.MasterData;
using IAMS.Api.Common.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Features.Inventory.SyncItems;

// ── Contract ────────────────────────────────────────────────────────────────
/// <summary>
/// One <c>InventoryItem</c> master row for the mobile offline cache. Carries every field the client needs to
/// reconstruct the F4 list + detail + search + filter surfaces entirely offline (SKU/barcode/name/description
/// for search, category for the filter dimension, isActive for soft-deletes) without ever calling the
/// interactive <c>/api/inventory/items</c> endpoints. The opaque resume cursor lives on the page envelope
/// (<see cref="MasterDataPage{T}.NextCursor"/>), exactly as the master-data sync feeds do — it is not a
/// per-row field. Timestamp fields mirror <c>CompanyDto</c>: <see cref="CreatedAtUtc"/> plus a nullable
/// <see cref="UpdatedAtUtc"/> (the <c>SyncCursorUtc</c> keyset basis is <c>COALESCE(UpdatedAtUtc, CreatedAtUtc)</c>).
/// </summary>
public record InventoryItemSyncDto(
    Guid Id,
    Guid TenantId,
    Guid CompanyId,
    string Sku,
    string? Barcode,
    string Name,
    string? Description,
    string? UnitOfMeasure,
    string? Category,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc);

public record SyncItemsQuery(string? Cursor, int? PageSize);

public class SyncItemsQueryValidator : AbstractValidator<SyncItemsQuery>
{
    public SyncItemsQueryValidator()
    {
        RuleFor(x => x.PageSize)
            .GreaterThan(0).LessThanOrEqualTo(MasterDataPaging.MaxPageSize)
            .When(x => x.PageSize.HasValue);
    }
}

// ── Handler ─────────────────────────────────────────────────────────────────
/// <summary>
/// Cursor-paginated offline sync feed for <c>InventoryItems</c>, reachable-company scoped via
/// <see cref="AccessCheckService"/> — the same keyset mechanism the master-data hierarchy feeds use
/// (<see cref="SyncCursor"/> + <see cref="MasterDataPaging"/> over the <c>SyncCursorUtc</c> shadow column and
/// <c>IX_InventoryItems_Sync</c> index). Distinct from the interactive <c>ListInventory</c> query surface:
/// this feed exists solely to hydrate the mobile SQLite cache, so it returns raw master fields (no aggregate
/// on-hand) and delivers inactive/soft-deleted rows too, so the client can converge deletions.
/// </summary>
public class SyncItemsHandler(IamsDbContext db, AccessCheckService accessCheck)
{
    public async Task<Results<Ok<MasterDataPage<InventoryItemSyncDto>>, ProblemHttpResult>> HandleAsync(
        SyncItemsQuery query, CancellationToken ct)
    {
        if (!SyncCursor.TryDecode(query.Cursor, out var ts, out var id))
        {
            return ApiError.Problem(StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed, "Malformed cursor.");
        }

        var reachable = (await accessCheck.GetReachableCompanyIdsAsync(ct)).ToArray();
        var take = query.PageSize ?? MasterDataPaging.DefaultPageSize;

        var rows = await db.InventoryItems.AsNoTracking()
            .Where(i => reachable.Contains(i.CompanyId))
            .Where(i => EF.Property<DateTime>(i, SyncCursor.ColumnName) > ts
                     || (EF.Property<DateTime>(i, SyncCursor.ColumnName) == ts && i.Id.CompareTo(id) > 0))
            .OrderBy(i => EF.Property<DateTime>(i, SyncCursor.ColumnName)).ThenBy(i => i.Id)
            .Take(take + 1)
            .Select(i => new
            {
                Cursor = EF.Property<DateTime>(i, SyncCursor.ColumnName),
                Dto = new InventoryItemSyncDto(
                    i.Id, i.TenantId, i.CompanyId, i.Sku, i.Barcode, i.Name, i.Description,
                    i.UnitOfMeasure, i.Category, i.IsActive, i.CreatedAtUtc, i.UpdatedAtUtc)
            })
            .ToListAsync(ct);

        return TypedResults.Ok(MasterDataPaging.BuildPage(rows, take, r => r.Cursor, r => r.Dto, r => r.Dto.Id));
    }
}
