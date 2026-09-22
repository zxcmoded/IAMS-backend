using FluentValidation;
using IAMS.Api.Common.Access;
using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Errors;
using IAMS.Api.Common.Inventory;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Features.Inventory.CreateStockCount;

// ── Contract ────────────────────────────────────────────────────────────────
/// <summary>Record a physical count of one item in one bin (BR-014). Within threshold auto-applies; over ⇒ PendingApproval.</summary>
public record CreateStockCountCommand(
    string IdempotencyKey,
    Guid InventoryItemId,
    Guid BinId,
    decimal CountedQuantity,
    long? BaseStockVersion,
    string? DeviceId,
    DateTime? ClientCreatedAtUtc);

public class CreateStockCountValidator : AbstractValidator<CreateStockCountCommand>
{
    public CreateStockCountValidator()
    {
        RuleFor(x => x.IdempotencyKey).NotEmpty().MaximumLength(200);
        RuleFor(x => x.InventoryItemId).NotEmpty();
        RuleFor(x => x.BinId).NotEmpty();
        RuleFor(x => x.CountedQuantity).GreaterThanOrEqualTo(0);
        RuleFor(x => x.DeviceId).MaximumLength(200);
    }
}

// ── Handler ─────────────────────────────────────────────────────────────────
/// <summary>
/// Records a stock count and adjudicates it against the company's effective variance threshold in one
/// transaction. The threshold is read from <see cref="InventorySettings"/> (fallback:
/// <see cref="InventoryDefaults.FallbackVarianceThreshold"/> when a company has no row) and SNAPSHOTTED onto
/// the count, so a later config change never re-classifies it. Within threshold ⇒ <see cref="StockCountStatus.Completed"/>
/// and, if the counted total differs, a reconciling <see cref="InventoryTransactionType.Adjustment"/> is written
/// to the ledger and the stock brought to the counted quantity — all atomically. Over threshold ⇒
/// <see cref="StockCountStatus.PendingApproval"/> with no stock change until an approver acts.
///
/// Idempotent by <c>IdempotencyKey</c> and version-checked against <see cref="StockLevel.Version"/> exactly like
/// the movement slices.
/// </summary>
public class CreateStockCountHandler(IamsDbContext db, AccessScopeResolver scopeResolver, ICurrentUser currentUser)
{
    public async Task<Results<Ok<StockCountResponse>, ProblemHttpResult>> HandleAsync(
        CreateStockCountCommand command, CancellationToken ct)
    {
        // Idempotent replay.
        var existing = await db.StockCounts.AsNoTracking()
            .FirstOrDefaultAsync(c => c.IdempotencyKey == command.IdempotencyKey, ct);
        if (existing is not null)
        {
            var version = await CurrentStockVersionAsync(existing.InventoryItemId, existing.BinId, ct);
            return TypedResults.Ok(StockCountResponse.From(existing, version, replayed: true));
        }

        var scope = await scopeResolver.ResolveAsync(ct);

        // The SKU is Company-level; the bin (and thus the count) is Location-bound — so the item is checked
        // against the Company scope and the bin against the assigned-Location scope.
        var item = await db.InventoryItems.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == command.InventoryItemId, ct);
        if (item is null || !scope.CompanyInScope(item.CompanyId))
        {
            return ApiError.Problem(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Inventory item not found.");
        }

        var bin = await db.Bins.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == command.BinId, ct);
        if (bin is null || !scope.LocationInScope(bin.CompanyId, bin.LocationId))
        {
            return ApiError.Problem(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Bin not found.");
        }

        // Tracked stock row (create-on-demand) — the SystemQuantity snapshot and the reconciling apply act on it.
        var stock = await db.StockLevels
            .FirstOrDefaultAsync(s => s.InventoryItemId == item.Id && s.BinId == bin.Id, ct);
        var currentVersion = stock?.Version ?? 0;

        if (command.BaseStockVersion is long baseV && baseV != currentVersion)
        {
            return ApiError.Problem(
                StatusCodes.Status409Conflict, ErrorCodes.StockVersionConflict,
                "A concurrent change to the stock level was detected; re-read and retry.", null,
                new Dictionary<string, object?>
                {
                    ["conflicts"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["binId"] = bin.Id,
                            ["expectedVersion"] = baseV,
                            ["currentVersion"] = currentVersion,
                            ["currentQuantityOnHand"] = stock?.QuantityOnHand ?? 0m
                        }
                    }
                });
        }

        var systemQuantity = stock?.QuantityOnHand ?? 0m;
        var variance = command.CountedQuantity - systemQuantity;

        // Effective threshold: the company's settings row, or the documented fallback. Snapshotted onto the count.
        var settings = await db.InventorySettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == item.CompanyId, ct);
        var threshold = settings?.VarianceThreshold ?? InventoryDefaults.FallbackVarianceThreshold;
        var thresholdType = settings?.VarianceThresholdType ?? InventoryDefaults.FallbackVarianceThresholdType;

        var within = InventoryDefaults.IsVarianceWithinThreshold(variance, systemQuantity, threshold, thresholdType);

        var count = new StockCount
        {
            Id = Guid.NewGuid(),
            CompanyId = item.CompanyId,
            InventoryItemId = item.Id,
            BinId = bin.Id,
            RackId = bin.RackId,
            WarehouseId = bin.WarehouseId,
            LocationId = bin.LocationId,
            CountedQuantity = command.CountedQuantity,
            SystemQuantity = systemQuantity,
            Variance = variance, // authoritative under Npgsql via the STORED generated column; set here for in-memory
            VarianceThreshold = threshold,
            VarianceThresholdType = thresholdType,
            Status = within ? StockCountStatus.Completed : StockCountStatus.PendingApproval,
            CountedByUserId = currentUser.UserId,
            IdempotencyKey = command.IdempotencyKey,
            BaseStockVersion = command.BaseStockVersion,
            DeviceId = command.DeviceId,
            ClientCreatedAtUtc = command.ClientCreatedAtUtc
        };

        if (within && variance != 0)
        {
            // Auto-apply: bring stock to the counted quantity and write the reconciling ledger adjustment,
            // linked back from the count. The stock row's Version is bumped centrally in IamsDbContext.
            stock ??= AddNewStock(item, bin);
            stock.QuantityOnHand = command.CountedQuantity;

            var adjustment = new InventoryTransaction
            {
                Id = Guid.NewGuid(),
                CompanyId = item.CompanyId,
                InventoryItemId = item.Id,
                TransactionType = InventoryTransactionType.Adjustment,
                SourceBinId = bin.Id,
                Quantity = variance, // signed delta system→counted
                AdjustmentReason = "Stock count reconciliation",
                Status = InventoryTransactionStatus.Applied,
                IdempotencyKey = $"{command.IdempotencyKey}:count-apply",
                CreatedByUserId = currentUser.UserId,
                DeviceId = command.DeviceId,
                ClientCreatedAtUtc = command.ClientCreatedAtUtc
            };
            db.InventoryTransactions.Add(adjustment);
            count.AdjustmentTransactionId = adjustment.Id;
        }

        db.StockCounts.Add(count);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (InventoryConcurrency.IsUniqueViolation(ex, "IdempotencyKey"))
        {
            foreach (var e in db.ChangeTracker.Entries().ToList())
            {
                e.State = EntityState.Detached;
            }
            var winner = await db.StockCounts.AsNoTracking()
                .FirstAsync(c => c.IdempotencyKey == command.IdempotencyKey, ct);
            var v = await CurrentStockVersionAsync(winner.InventoryItemId, winner.BinId, ct);
            return TypedResults.Ok(StockCountResponse.From(winner, v, replayed: true));
        }

        var resultVersion = stock?.Version ?? currentVersion;
        return TypedResults.Ok(StockCountResponse.From(count, resultVersion, replayed: false));
    }

    private StockLevel AddNewStock(InventoryItem item, Bin bin)
    {
        var sl = InventoryConcurrency.NewStockLevel(item, bin);
        db.StockLevels.Add(sl);
        return sl;
    }

    private async Task<long> CurrentStockVersionAsync(Guid itemId, Guid binId, CancellationToken ct) =>
        await db.StockLevels.AsNoTracking()
            .Where(s => s.InventoryItemId == itemId && s.BinId == binId)
            .Select(s => (long?)s.Version).FirstOrDefaultAsync(ct) ?? 0;
}
