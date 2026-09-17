using IAMS.Api.Common.Access;
using IAMS.Api.Common.Errors;
using IAMS.Api.Common.Inventory;
using IAMS.Api.Common.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Features.Inventory.GetInventoryItem;

// ── Contract ────────────────────────────────────────────────────────────────
public record StockByBinDto(
    Guid BinId,
    Guid RackId,
    Guid WarehouseId,
    Guid LocationId,
    Guid CompanyId,
    decimal QuantityOnHand,
    long Version);

public record MovementDto(
    Guid Id,
    string TransactionType,
    string Status,
    Guid? SourceBinId,
    Guid? DestinationBinId,
    decimal Quantity,
    string? AdjustmentReason,
    DateTime CreatedAtUtc,
    DateTime? ClientCreatedAtUtc);

public record InventoryItemDetailResponse(
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
    decimal TotalQuantityOnHand,
    IReadOnlyList<StockByBinDto> StockByBin,
    IReadOnlyList<MovementDto> Movements);

// ── Handler ─────────────────────────────────────────────────────────────────
/// <summary>
/// Item detail for the F4 item screen: the SKU master fields, its per-bin on-hand (with denormalized ancestry
/// and the current <c>Version</c> the client stamps mutations with), and a bounded window of recent ledger
/// movements. A 404 is returned when the id is outside the caller's reachable-company set — deliberately not a
/// 403, so a direct object reference cannot confirm the existence of another tenant's item (BR-003 spirit).
/// </summary>
public class GetInventoryItemHandler(IamsDbContext db, AccessCheckService accessCheck)
{
    public async Task<Results<Ok<InventoryItemDetailResponse>, ProblemHttpResult>> HandleAsync(
        Guid id, CancellationToken ct)
    {
        var reachable = (await accessCheck.GetReachableCompanyIdsAsync(ct)).ToArray();

        var item = await db.InventoryItems.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == id && reachable.Contains(i.CompanyId), ct);
        if (item is null)
        {
            return ApiError.Problem(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Inventory item not found.");
        }

        var stock = await db.StockLevels.AsNoTracking()
            .Where(sl => sl.InventoryItemId == id)
            .OrderBy(sl => sl.BinId)
            .Select(sl => new StockByBinDto(
                sl.BinId, sl.RackId, sl.WarehouseId, sl.LocationId, sl.CompanyId, sl.QuantityOnHand, sl.Version))
            .ToListAsync(ct);

        var movements = await db.InventoryTransactions.AsNoTracking()
            .Where(t => t.InventoryItemId == id)
            .OrderByDescending(t => t.CreatedAtUtc).ThenByDescending(t => t.Id)
            .Take(InventoryDefaults.MovementHistoryLimit)
            .Select(t => new MovementDto(
                t.Id, t.TransactionType.ToString(), t.Status.ToString(),
                t.SourceBinId, t.DestinationBinId, t.Quantity, t.AdjustmentReason,
                t.CreatedAtUtc, t.ClientCreatedAtUtc))
            .ToListAsync(ct);

        var total = stock.Sum(s => s.QuantityOnHand);

        return TypedResults.Ok(new InventoryItemDetailResponse(
            item.Id, item.TenantId, item.CompanyId, item.Sku, item.Barcode, item.Name, item.Description,
            item.UnitOfMeasure, item.Category, item.IsActive, total, stock, movements));
    }
}
