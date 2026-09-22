using IAMS.Api.Common.Access;
using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Errors;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Common.Inventory;

/// <summary>
/// One movement to apply to the ledger + stock levels. Shared by the receive / transfer / adjust slices and
/// by stock-count reconciliation. Which bins are set and the sign of <see cref="Quantity"/> follow the
/// per-type rules the <c>CK_InvTxn_TypeShape</c> CHECK enforces at the storage layer.
/// </summary>
public record MovementRequest(
    InventoryTransactionType Type,
    Guid InventoryItemId,
    Guid? SourceBinId,
    Guid? DestinationBinId,
    decimal Quantity,
    string? AdjustmentReason,
    string IdempotencyKey,
    long? BaseSourceStockVersion,
    long? BaseDestinationStockVersion,
    string? DeviceId,
    DateTime? ClientCreatedAtUtc,
    Guid? ReversalOfTransactionId = null);

/// <summary>Uniform response for an applied (or idempotently replayed) stock movement.</summary>
public record StockMovementResponse(
    Guid TransactionId,
    string TransactionType,
    string Status,
    decimal Quantity,
    string? AdjustmentReason,
    IReadOnlyList<StockLevelStateDto> StockLevels,
    bool Replayed);

/// <summary>An error outcome the calling slice maps straight to a ProblemDetails result.</summary>
public record MovementError(int Status, string Code, string Title, IReadOnlyDictionary<string, object?>? Extra = null)
{
    public ProblemHttpResult ToProblem() => Extra is null
        ? ApiError.Problem(Status, Code, Title)
        : ApiError.Problem(Status, Code, Title, null, Extra);
}

public record MovementResult(StockMovementResponse? Success, MovementError? Error);

/// <summary>
/// Applies an idempotent, version-checked inventory movement: it de-duplicates a replayed offline push by
/// <c>IdempotencyKey</c>, rejects a mutation built on a stale <see cref="StockLevel.Version"/> with a 409,
/// pre-checks the non-negative invariant for a clean 4xx before the DB CHECK fires, writes the append-only
/// ledger row, and mutates the affected <see cref="StockLevel"/> rows (whose <c>Version</c> is bumped centrally
/// in <c>IamsDbContext</c>). Scope is resolved live off the caller's JWT (Company + assigned Locations) — a
/// client-supplied company is never trusted, and the touched bins must be inside the caller's Location scope.
/// </summary>
public class StockMovementService(IamsDbContext db, AccessScopeResolver scopeResolver, ICurrentUser currentUser)
{
    public async Task<MovementResult> ApplyAsync(MovementRequest r, CancellationToken ct)
    {
        // Idempotent replay: the same key already applied → return the original transaction + current bin state.
        var existing = await db.InventoryTransactions.AsNoTracking()
            .FirstOrDefaultAsync(t => t.IdempotencyKey == r.IdempotencyKey, ct);
        if (existing is not null)
        {
            return Ok(await ReplayResponseAsync(existing, ct));
        }

        var scope = await scopeResolver.ResolveAsync(ct);

        var item = await db.InventoryItems.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == r.InventoryItemId, ct);
        if (item is null || !scope.CompanyInScope(item.CompanyId))
        {
            return Err(new MovementError(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Inventory item not found."));
        }

        // Resolve the bins the movement touches (each must be a real bin inside the caller's Location scope).
        Bin? sourceBin = null, destBin = null;
        if (r.SourceBinId is Guid srcId)
        {
            sourceBin = await LoadScopedBinAsync(srcId, scope, ct);
            if (sourceBin is null)
            {
                return Err(new MovementError(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Source bin not found."));
            }
        }
        if (r.DestinationBinId is Guid dstId)
        {
            destBin = await LoadScopedBinAsync(dstId, scope, ct);
            if (destBin is null)
            {
                return Err(new MovementError(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Destination bin not found."));
            }
        }

        // Get-or-create the (item, bin) stock rows, tracked so we can mutate on-hand.
        StockLevel? sourceStock = sourceBin is null ? null : await GetOrCreateStockAsync(item, sourceBin, ct);
        StockLevel? destStock = destBin is null ? null : await GetOrCreateStockAsync(item, destBin, ct);

        // Concurrency: a base version the client stamped at commit time that no longer matches ⇒ 409, with the
        // current version(s) so the client can rebase without another round-trip.
        var conflicts = new List<Dictionary<string, object?>>();
        AddConflictIfStale(conflicts, r.BaseSourceStockVersion, sourceStock, sourceBin);
        AddConflictIfStale(conflicts, r.BaseDestinationStockVersion, destStock, destBin);
        if (conflicts.Count > 0)
        {
            return Err(new MovementError(
                StatusCodes.Status409Conflict, ErrorCodes.StockVersionConflict,
                "A concurrent change to the stock level was detected; re-read and retry.",
                new Dictionary<string, object?> { ["conflicts"] = conflicts }));
        }

        // Apply the on-hand effect, pre-checking non-negativity for a clean 4xx ahead of CK_StockLevels_NonNegative.
        switch (r.Type)
        {
            case InventoryTransactionType.Receive:
                destStock!.QuantityOnHand += r.Quantity;
                break;

            case InventoryTransactionType.Transfer:
                if (sourceStock!.QuantityOnHand < r.Quantity)
                {
                    return Err(InsufficientStock(sourceBin!, sourceStock));
                }
                sourceStock.QuantityOnHand -= r.Quantity;
                destStock!.QuantityOnHand += r.Quantity;
                break;

            case InventoryTransactionType.Adjustment:
                var newQty = sourceStock!.QuantityOnHand + r.Quantity; // signed delta
                if (newQty < 0)
                {
                    return Err(InsufficientStock(sourceBin!, sourceStock));
                }
                sourceStock.QuantityOnHand = newQty;
                break;
        }

        var txn = new InventoryTransaction
        {
            Id = Guid.NewGuid(),
            CompanyId = item.CompanyId,
            InventoryItemId = item.Id,
            TransactionType = r.Type,
            SourceBinId = r.SourceBinId,
            DestinationBinId = r.DestinationBinId,
            Quantity = r.Quantity,
            AdjustmentReason = r.AdjustmentReason,
            Status = InventoryTransactionStatus.Applied,
            IdempotencyKey = r.IdempotencyKey,
            BaseSourceStockVersion = r.BaseSourceStockVersion,
            BaseDestinationStockVersion = r.BaseDestinationStockVersion,
            ReversalOfTransactionId = r.ReversalOfTransactionId,
            DeviceId = r.DeviceId,
            CreatedByUserId = currentUser.UserId,
            ClientCreatedAtUtc = r.ClientCreatedAtUtc
        };
        db.InventoryTransactions.Add(txn);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (InventoryConcurrency.IsUniqueViolation(ex, "IdempotencyKey"))
        {
            // A concurrent replay of the same key committed first — return that original result.
            foreach (var e in db.ChangeTracker.Entries().ToList())
            {
                e.State = EntityState.Detached;
            }
            var winner = await db.InventoryTransactions.AsNoTracking()
                .FirstAsync(t => t.IdempotencyKey == r.IdempotencyKey, ct);
            return Ok(await ReplayResponseAsync(winner, ct));
        }

        var states = new List<StockLevelStateDto>();
        if (sourceStock is not null)
        {
            states.Add(new StockLevelStateDto(sourceStock.BinId, sourceStock.QuantityOnHand, sourceStock.Version));
        }
        if (destStock is not null)
        {
            states.Add(new StockLevelStateDto(destStock.BinId, destStock.QuantityOnHand, destStock.Version));
        }

        return Ok(new StockMovementResponse(
            txn.Id, txn.TransactionType.ToString(), txn.Status.ToString(),
            txn.Quantity, txn.AdjustmentReason, states, Replayed: false));
    }

    private async Task<Bin?> LoadScopedBinAsync(Guid binId, UserAccessScope scope, CancellationToken ct)
    {
        var bin = await db.Bins.AsNoTracking().FirstOrDefaultAsync(b => b.Id == binId, ct);
        return bin is not null && scope.LocationInScope(bin.CompanyId, bin.LocationId) ? bin : null;
    }

    private async Task<StockLevel> GetOrCreateStockAsync(InventoryItem item, Bin bin, CancellationToken ct)
    {
        var sl = await db.StockLevels
            .FirstOrDefaultAsync(s => s.InventoryItemId == item.Id && s.BinId == bin.Id, ct);
        if (sl is null)
        {
            sl = InventoryConcurrency.NewStockLevel(item, bin);
            db.StockLevels.Add(sl);
        }
        return sl;
    }

    private static void AddConflictIfStale(
        List<Dictionary<string, object?>> conflicts, long? baseVersion, StockLevel? stock, Bin? bin)
    {
        if (baseVersion is null || bin is null)
        {
            return;
        }

        var current = stock?.Version ?? 0; // a not-yet-existing stock row is version 0
        if (baseVersion.Value != current)
        {
            conflicts.Add(new Dictionary<string, object?>
            {
                ["binId"] = bin.Id,
                ["expectedVersion"] = baseVersion.Value,
                ["currentVersion"] = current,
                ["currentQuantityOnHand"] = stock?.QuantityOnHand ?? 0m
            });
        }
    }

    private static MovementError InsufficientStock(Bin bin, StockLevel stock) => new(
        StatusCodes.Status422UnprocessableEntity, ErrorCodes.InsufficientStock,
        "The movement would drive the bin's on-hand quantity below zero.",
        new Dictionary<string, object?>
        {
            ["binId"] = bin.Id,
            ["availableQuantity"] = stock.QuantityOnHand
        });

    private async Task<StockMovementResponse> ReplayResponseAsync(InventoryTransaction txn, CancellationToken ct)
    {
        // Best-effort current state of the bins the original movement touched.
        var binIds = new List<Guid>();
        if (txn.SourceBinId is Guid s) binIds.Add(s);
        if (txn.DestinationBinId is Guid d) binIds.Add(d);

        var states = await db.StockLevels.AsNoTracking()
            .Where(sl => sl.InventoryItemId == txn.InventoryItemId && binIds.Contains(sl.BinId))
            .Select(sl => new StockLevelStateDto(sl.BinId, sl.QuantityOnHand, sl.Version))
            .ToListAsync(ct);

        return new StockMovementResponse(
            txn.Id, txn.TransactionType.ToString(), txn.Status.ToString(),
            txn.Quantity, txn.AdjustmentReason, states, Replayed: true);
    }

    private static MovementResult Ok(StockMovementResponse r) => new(r, null);
    private static MovementResult Err(MovementError e) => new(null, e);
}
