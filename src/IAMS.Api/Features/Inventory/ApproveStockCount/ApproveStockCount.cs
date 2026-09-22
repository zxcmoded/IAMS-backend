using IAMS.Api.Common.Access;
using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Errors;
using IAMS.Api.Common.Inventory;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Security;
using IAMS.Api.Common.Time;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Features.Inventory.ApproveStockCount;

// ── Handler ─────────────────────────────────────────────────────────────────
/// <summary>
/// Approves a <see cref="StockCountStatus.PendingApproval"/> count: brings the bin's on-hand to the counted
/// quantity, writes the reconciling <see cref="InventoryTransactionType.Adjustment"/> to the ledger (linked via
/// <see cref="StockCount.AdjustmentTransactionId"/>), and records the approver. The reconciling delta is
/// computed against the CURRENT on-hand at approval time (stock may have moved since the count), so the final
/// on-hand always equals the counted quantity. Approving a count that is not pending returns a 409.
/// </summary>
public class ApproveStockCountHandler(IamsDbContext db, AccessScopeResolver scopeResolver, ICurrentUser currentUser, IClock clock)
{
    public async Task<Results<Ok<StockCountResponse>, ProblemHttpResult>> HandleAsync(Guid id, CancellationToken ct)
    {
        var scope = await scopeResolver.ResolveAsync(ct);

        var count = await db.StockCounts.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (count is null || !scope.LocationInScope(count.CompanyId, count.LocationId))
        {
            return ApiError.Problem(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Stock count not found.");
        }
        if (count.Status != StockCountStatus.PendingApproval)
        {
            return ApiError.Problem(StatusCodes.Status409Conflict, ErrorCodes.StockCountNotPending,
                $"Stock count is {count.Status}, not PendingApproval.");
        }

        var stock = await db.StockLevels
            .FirstOrDefaultAsync(s => s.InventoryItemId == count.InventoryItemId && s.BinId == count.BinId, ct);
        var currentOnHand = stock?.QuantityOnHand ?? 0m;
        var delta = count.CountedQuantity - currentOnHand;

        if (delta != 0)
        {
            if (stock is null)
            {
                var item = await db.InventoryItems.AsNoTracking().FirstAsync(i => i.Id == count.InventoryItemId, ct);
                var bin = await db.Bins.AsNoTracking().FirstAsync(b => b.Id == count.BinId, ct);
                stock = InventoryConcurrency.NewStockLevel(item, bin);
                db.StockLevels.Add(stock);
            }
            stock.QuantityOnHand = count.CountedQuantity;

            var adjustment = new InventoryTransaction
            {
                Id = Guid.NewGuid(),
                CompanyId = count.CompanyId,
                InventoryItemId = count.InventoryItemId,
                TransactionType = InventoryTransactionType.Adjustment,
                SourceBinId = count.BinId,
                Quantity = delta,
                AdjustmentReason = "Stock count reconciliation (approved)",
                Status = InventoryTransactionStatus.Applied,
                IdempotencyKey = $"{count.Id}:count-approve",
                CreatedByUserId = currentUser.UserId
            };
            db.InventoryTransactions.Add(adjustment);
            count.AdjustmentTransactionId = adjustment.Id;
        }

        count.Status = StockCountStatus.Approved;
        count.ApprovedByUserId = currentUser.UserId;
        count.ApprovedAtUtc = clock.UtcNow.UtcDateTime;
        count.UpdatedAtUtc = clock.UtcNow.UtcDateTime;

        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(StockCountResponse.From(count, stock?.Version ?? 0, replayed: false));
    }
}
