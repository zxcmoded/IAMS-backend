using IAMS.Api.Common.Domain;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Common.Inventory;

/// <summary>
/// Source-unspecified defaults the F4 slices had to decide (flagged in phase-2a §6). Kept here as named
/// constants so the choice is documented in one place and easy to promote to configuration later.
/// </summary>
public static class InventoryDefaults
{
    /// <summary>
    /// Fallback variance threshold used ONLY when a company has no <see cref="InventorySettings"/> row
    /// (the bootstrap gap in phase-2a §6). Deliberately <c>0</c> and <see cref="VarianceThresholdType.AbsoluteQuantity"/>:
    /// with a zero threshold ANY non-zero variance exceeds it and is parked as
    /// <see cref="StockCountStatus.PendingApproval"/>, so a company that never configured a threshold can
    /// never silently auto-adjust stock — the fail-safe direction. An admin can loosen it by inserting a
    /// settings row. This is a chosen default, not a source requirement.
    /// </summary>
    public const decimal FallbackVarianceThreshold = 0m;

    public const VarianceThresholdType FallbackVarianceThresholdType = VarianceThresholdType.AbsoluteQuantity;

    /// <summary>
    /// Default upper bound (inclusive) for the inventory list's <c>low_stock</c> filter when the caller does
    /// not supply <c>lowStockThreshold</c>: an item is "low stock" when <c>0 &lt; totalOnHand &lt;= 10</c>.
    /// There is no per-SKU reorder point in the Phase-2a schema, so this is a pragmatic single default for the
    /// list screen, overridable per request — flagged as a decision, not a source requirement.
    /// </summary>
    public const decimal DefaultLowStockThreshold = 10m;

    /// <summary>
    /// How many of an item's most-recent ledger rows the item-detail endpoint returns as movement history —
    /// a bounded window so the response can't grow unbounded with an item's lifetime activity.
    /// </summary>
    public const int MovementHistoryLimit = 50;

    /// <summary>
    /// Whether a stock-count variance is within the effective threshold (⇒ auto-apply) or exceeds it
    /// (⇒ PendingApproval). Interprets the snapshotted threshold per its type: an absolute counted-unit delta,
    /// or a percentage of the system quantity (BR-014). A zero threshold means only an exact match auto-applies.
    /// </summary>
    public static bool IsVarianceWithinThreshold(
        decimal variance, decimal systemQuantity, decimal threshold, VarianceThresholdType type)
    {
        var abs = Math.Abs(variance);
        return type switch
        {
            VarianceThresholdType.Percentage =>
                systemQuantity == 0 ? abs == 0 : abs / Math.Abs(systemQuantity) * 100m <= threshold,
            _ => abs <= threshold // AbsoluteQuantity
        };
    }
}

/// <summary>The current server state of one stock row — echoed in mutation responses and in 409 conflict bodies.</summary>
public record StockLevelStateDto(Guid BinId, decimal QuantityOnHand, long Version);

/// <summary>
/// Representation of a stock count returned by create / approve / reject. Shared so the three slices present
/// one shape. <paramref name="StockVersion"/> is the affected bin's current <see cref="StockLevel.Version"/>
/// (0 when the bin has no stock row yet).
/// </summary>
public record StockCountResponse(
    Guid Id,
    string Status,
    decimal CountedQuantity,
    decimal SystemQuantity,
    decimal Variance,
    decimal VarianceThreshold,
    string VarianceThresholdType,
    Guid? AdjustmentTransactionId,
    long StockVersion,
    bool Replayed)
{
    public static StockCountResponse From(StockCount c, long stockVersion, bool replayed) => new(
        c.Id, c.Status.ToString(), c.CountedQuantity, c.SystemQuantity, c.Variance,
        c.VarianceThreshold, c.VarianceThresholdType.ToString(), c.AdjustmentTransactionId, stockVersion, replayed);
}

/// <summary>Shared helpers for the idempotent, version-checked stock mutation slices (receive/transfer/adjust/count).</summary>
public static class InventoryConcurrency
{
    /// <summary>Postgres unique-violation SQLSTATE.</summary>
    private const string UniqueViolation = "23505";

    /// <summary>
    /// True when <paramref name="ex"/> is a Postgres unique-constraint violation whose constraint name
    /// contains <paramref name="constraintFragment"/> (case-insensitive). Used so a racing idempotent replay
    /// — two deliveries of the same <c>IdempotencyKey</c> hitting the unique index simultaneously — is caught
    /// and turned into "return the original result", not a 500. The in-memory test provider never throws this,
    /// so idempotency there is covered by the pre-insert lookup instead.
    /// </summary>
    public static bool IsUniqueViolation(DbUpdateException ex, string constraintFragment)
    {
        // Npgsql surfaces the DB error as an inner PostgresException; avoid a hard package-type dependency by
        // duck-typing on the well-known shape (SqlState + ConstraintName).
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            var type = e.GetType();
            var sqlState = type.GetProperty("SqlState")?.GetValue(e) as string;
            if (sqlState != UniqueViolation)
            {
                continue;
            }

            var constraint = type.GetProperty("ConstraintName")?.GetValue(e) as string;
            if (constraint is null ||
                constraint.Contains(constraintFragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds a fresh <see cref="StockLevel"/> for <paramref name="item"/> in <paramref name="bin"/>, copying the
    /// bin's denormalized ancestry chain (the physical location determines the stock row's scope). Version is
    /// left at 0 and set to 1 centrally by <c>IamsDbContext.BumpStockLevelVersions</c> on first save.
    /// </summary>
    public static StockLevel NewStockLevel(InventoryItem item, Bin bin) => new()
    {
        Id = Guid.NewGuid(),
        InventoryItemId = item.Id,
        BinId = bin.Id,
        RackId = bin.RackId,
        WarehouseId = bin.WarehouseId,
        LocationId = bin.LocationId,
        CompanyId = bin.CompanyId,
        QuantityOnHand = 0m,
        Version = 0
    };
}
