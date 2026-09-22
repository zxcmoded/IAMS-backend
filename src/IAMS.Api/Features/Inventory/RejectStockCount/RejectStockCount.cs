using FluentValidation;
using IAMS.Api.Common.Access;
using IAMS.Api.Common.Domain;
using IAMS.Api.Common.Errors;
using IAMS.Api.Common.Inventory;
using IAMS.Api.Common.Persistence;
using IAMS.Api.Common.Time;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Features.Inventory.RejectStockCount;

// ── Contract ────────────────────────────────────────────────────────────────
public record RejectStockCountCommand(string? Reason);

public class RejectStockCountValidator : AbstractValidator<RejectStockCountCommand>
{
    public RejectStockCountValidator()
    {
        RuleFor(x => x.Reason).MaximumLength(400);
    }
}

// ── Handler ─────────────────────────────────────────────────────────────────
/// <summary>
/// Rejects a <see cref="StockCountStatus.PendingApproval"/> count: no stock change, just a terminal
/// <see cref="StockCountStatus.Rejected"/> status with an optional reason. Rejecting a non-pending count → 409.
/// </summary>
public class RejectStockCountHandler(IamsDbContext db, AccessScopeResolver scopeResolver, IClock clock)
{
    public async Task<Results<Ok<StockCountResponse>, ProblemHttpResult>> HandleAsync(
        Guid id, RejectStockCountCommand command, CancellationToken ct)
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

        count.Status = StockCountStatus.Rejected;
        count.RejectionReason = command.Reason;
        count.UpdatedAtUtc = clock.UtcNow.UtcDateTime;

        await db.SaveChangesAsync(ct);

        var version = await db.StockLevels.AsNoTracking()
            .Where(s => s.InventoryItemId == count.InventoryItemId && s.BinId == count.BinId)
            .Select(s => (long?)s.Version).FirstOrDefaultAsync(ct) ?? 0;

        return TypedResults.Ok(StockCountResponse.From(count, version, replayed: false));
    }
}
